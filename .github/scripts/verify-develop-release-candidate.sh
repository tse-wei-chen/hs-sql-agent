#!/usr/bin/env bash
# Read-only Release Candidate attestation for the exact develop *push* SHA.
# NEVER checks/promotes main, issues tags, publishes packages or closes issues.
# Production publication still uses verify-release-security-gate.sh AND
# verify-release-head-ci-gate.sh on the promoted main SHA.
set -euo pipefail

repo="tse-wei-chen/hs-sql-agent"
sha="${GITHUB_SHA:-}"
output="${HS_SQL_AGENT_CANDIDATE_REPORT:-artifacts/release-candidate/develop-3.1.0.json}"

die() { echo "::error::Release candidate: $*" >&2; exit 1; }
pending() { echo "Release candidate pending: $*" >&2; exit 75; }

[[ "${GITHUB_REPOSITORY:-}" == "$repo" ]] || die "unexpected repository identity"
[[ "${GITHUB_REF:-}" == "refs/heads/develop" ]] || die "only a develop branch push is eligible"
[[ "$sha" =~ ^[0-9a-f]{40}$ ]] || die "invalid develop SHA"
[[ -n "${GH_TOKEN:-}" ]] || die "GitHub Actions read token is missing"

# The candidate is always the *current* develop head, not a historical green
# PR, a stale branch SHA or a previously successful workflow run.
current="$(gh api "repos/$repo/branches/develop" --jq '.commit.sha')" ||
    die "cannot read develop HEAD"
[[ "$current" == "$sha" ]] || die "develop moved; discard stale candidate $sha"

grep -Eq '<VersionPrefix[^>]*>3\.1\.0</VersionPrefix>' backend/Directory.Build.props ||
    die "backend VersionPrefix is not 3.1.0"
grep -Fqx '## [3.1.0] - Unreleased' <(grep '^## \[' CHANGELOG.md) ||
    die "3.1.0 unreleased changelog entry is missing"
[[ -s .github/test-suites/query-security-deployment-contract.md ]] ||
    die "six-provider Query least-privilege deployment contract missing"
[[ -s .github/test-suites/physical-fault-release-evidence.md ]] ||
    die "bounded physical fault and limitations evidence missing"

latest_run() {
    local workflow="$1"
    local response
    response="$(gh api "repos/$repo/actions/workflows/$workflow/runs?head_sha=$sha&branch=develop&event=push&per_page=100")" ||
        die "cannot read $workflow push runs"
    if ! jq -er --arg sha "$sha" '
        if (.workflow_runs | type) != "array" then empty
        else [.workflow_runs[] | select(
            .head_sha == $sha and .head_branch == "develop" and
            .event == "push" and (.id | type) == "number"
        )] | max_by(.id)
        end
    ' <<< "$response"; then
        pending "no exact-develop push run for $workflow"
    fi
}

verify_run() {
    local workflow="$1" state
    state="$(latest_run "$workflow")" || {
        local rc="$?"
        if (( rc == 75 )); then exit 75; fi
        die "invalid $workflow workflow response"
    }
    local status conclusion
    status="$(jq -r '.status' <<< "$state")"
    conclusion="$(jq -r '.conclusion // ""' <<< "$state")"
    if [[ "$status" != "completed" ]]; then
        pending "$workflow is $status"
    fi
    [[ "$conclusion" == "success" ]] || die "$workflow completed as $conclusion"
    jq -er '.id' <<< "$state"
}

tests_run="$(verify_run test.yml)"

# Exact names and count matter: 18 unrelated green jobs cannot substitute
# for the server, six-provider real-DB, Redis and release-advisory checks.
jobs="$(gh api "repos/$repo/actions/runs/$tests_run/jobs?per_page=100")" ||
    die "cannot inspect Tests jobs"
jq -e '
    .total_count == 18
    and (.jobs | type) == "array"
    and (.jobs | length) == 18
    and ([.jobs[].name] | sort) == ([
      "admin-api-openapi", "admin-test", "auth-test", "common-test",
      "frontend-lint", "frontend-test", "infrastructure-test",
      "redis-approval-integration", "server-test", "sql-syntax-parity",
      "sqlagent-capability-stack", "sqlagent-fence-sqlserver",
      "sqlagent-firebird-ddl-parity", "sqlagent-oracle-ddl-parity",
      "sqlagent-package-smoke", "sqlagent-syntax-matrix",
      "sqlagent-test", "sqlagent-test-rest"
    ] | sort)
    and (.jobs | all(.[]; .status == "completed" and .conclusion == "success"))
' <<< "$jobs" >/dev/null || die "required Tests job name/count/result contract failed"

artifacts="$(gh api "repos/$repo/actions/runs/$tests_run/artifacts?per_page=100")" ||
    die "cannot read physical/security evidence artifacts"
jq -e '
    (.artifacts | type) == "array"
    and ([.artifacts[] | select(.name == "security-dependency-advisory-evidence"
      and .expired == false and .size_in_bytes > 0)] | length) == 1
' <<< "$artifacts" >/dev/null || die "required exact-head dependency/CVE/compiler-warning evidence artifact missing"

mkdir -p "$(dirname "$output")"
jq -n \
    --arg sha "$sha" \
    --argjson tests "$tests_run" \
    '{
      schemaVersion: 2,
      version: "3.1.0",
      stage: "develop-release-candidate",
      sha: $sha,
      testsRunId: $tests,
      testsJobsPassed: 18,
      advisoryAndCompilerArtifact: "security-dependency-advisory-evidence",
      providerProof: "six-provider mandatory physical positive/negative and fault CI jobs",
      publicationAuthorized: false,
      mainPromotionPerformed: false,
      remainingPublicationGate: "actual promoted main push SHA + completed issue #575"
    }' > "$output"
echo "Develop 3.1.0 release candidate verified at $sha: 18 Tests jobs (run $tests_run), advisory/warning evidence present. No publication authorized."
