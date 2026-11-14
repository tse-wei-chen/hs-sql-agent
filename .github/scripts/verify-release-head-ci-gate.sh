#!/usr/bin/env bash
# Refuse registry publication unless the release tag is the promoted main HEAD
# and all required Tests jobs already succeeded for that exact main push SHA.
set -euo pipefail

repo="${GITHUB_REPOSITORY:-}"
sha="${GITHUB_SHA:-}"
if [[ "$repo" != "tse-wei-chen/hs-sql-agent" || ! "$sha" =~ ^[0-9a-f]{40}$ || -z "${GH_TOKEN:-}" ]]; then
    echo '::error::Cannot attest release identity, SHA or GitHub Actions credentials.' >&2
    exit 1
fi

if ! main_sha="$(gh api "repos/$repo/branches/main" --jq '.commit.sha')"; then
    echo '::error::Cannot attest the main branch HEAD.' >&2
    exit 1
fi
if [[ "$main_sha" != "$sha" ]]; then
    echo '::error::Release SHA does not match the promoted main HEAD.' >&2
    exit 1
fi

# Inspect *all* runs for the release SHA, not only successful ones. GitHub's
# status=success filter can hide a newer failed/pending attempt, leaving an
# older green run to incorrectly authorize publication. The newest matching
# run ID (creation order) must itself be completed and successful.
# Pull-request, release-triggered or different-SHA runs never qualify.
if ! runs="$(gh api "repos/$repo/actions/workflows/test.yml/runs?head_sha=$sha&branch=main&event=push&per_page=100")"; then
    echo '::error::Cannot read the exact-commit Tests workflow evidence.' >&2
    exit 1
fi
if ! run_id="$(jq -er --arg sha "$sha" '
    if (.workflow_runs | type) == "array" then
      [.workflow_runs[] | select(.head_sha == $sha and .head_branch == "main"
        and .event == "push")]
      | max_by(.id)
      | select(.status == "completed" and .conclusion == "success"
          and (.id | type) == "number")
      | .id
    else empty end' <<< "$runs")"; then
    echo '::error::Newest main push Tests run for this SHA is missing, pending or unsuccessful.' >&2
    exit 1
fi

if ! jobs="$(gh api "repos/$repo/actions/runs/$run_id/jobs?per_page=100")"; then
    echo '::error::Cannot read the exact-commit Tests job conclusions.' >&2
    exit 1
fi
if ! jq -e '
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
  ' <<< "$jobs" >/dev/null; then
    echo '::error::Release requires exactly 18 completed, successful Tests jobs on the exact main SHA.' >&2
    exit 1
fi

echo "Verified release SHA $sha against main and all 18 Tests jobs (run $run_id)."
