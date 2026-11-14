#!/usr/bin/env bash
# Offline positive and fail-closed negative regressions for develop only.
set -euo pipefail
root="$(cd "$(dirname "$0")/../.." && pwd)"
gate="$root/.github/scripts/verify-develop-release-candidate.sh"
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT

cat > "$tmp/gh" <<'MOCK'
#!/usr/bin/env bash
set -euo pipefail
[[ "$1" == api ]] || exit 40
case "$2" in
  repos/tse-wei-chen/hs-sql-agent/branches/develop)
    [[ "$3" == --jq && "$4" == .commit.sha ]] || exit 41
    printf '%s\n' "${MOCK_DEVELOP_SHA:-$GITHUB_SHA}"
    ;;
  "repos/tse-wei-chen/hs-sql-agent/actions/workflows/test.yml/runs?head_sha=${GITHUB_SHA}&branch=develop&event=push&per_page=100")
    if [[ "${MOCK_NO_TEST_RUN:-0}" == 1 ]]; then
      echo '{"workflow_runs":[]}'
    else
      jq -n \
        --arg sha "${MOCK_TEST_SHA:-$GITHUB_SHA}" \
        --arg branch "${MOCK_TEST_BRANCH:-develop}" \
        --arg event "${MOCK_TEST_EVENT:-push}" \
        --arg status "${MOCK_TEST_STATUS:-completed}" \
        --arg conclusion "${MOCK_TEST_CONCLUSION:-success}" \
        --arg old "${MOCK_OLD_TEST_SUCCESS:-0}" \
        '{workflow_runs: ([{id: 1001, head_sha: $sha, head_branch: $branch,
           event: $event, status: $status,
           conclusion: (if $status == "completed" then $conclusion else null end)}] +
           (if $old == "1" then [{id: 1000, head_sha: $sha, head_branch: $branch,
           event: $event, status: "completed", conclusion: "success"}] else [] end))}'
    fi
    ;;
  repos/tse-wei-chen/hs-sql-agent/actions/runs/1001/jobs?per_page=100)
    names=(admin-api-openapi admin-test auth-test common-test frontend-lint
      frontend-test infrastructure-test redis-approval-integration server-test
      sql-syntax-parity sqlagent-capability-stack sqlagent-fence-sqlserver
      sqlagent-firebird-ddl-parity sqlagent-oracle-ddl-parity
      sqlagent-package-smoke sqlagent-syntax-matrix sqlagent-test sqlagent-test-rest)
    count="${MOCK_TEST_JOBS:-18}"
    printf '{"total_count":%d,"jobs":[' "$count"
    for (( i=0; i<count; i++ )); do
      (( i == 0 )) || printf ','
      name="${names[i]:-impostor}"
      [[ "$i" != 0 || "${MOCK_WRONG_TEST_JOB:-0}" != 1 ]] || name=impostor
      result=success
      [[ "$i" != 0 || "${MOCK_FAIL_JOB:-0}" != 1 ]] || result=failure
      printf '{"name":"%s","status":"completed","conclusion":"%s"}' "$name" "$result"
    done
    echo ']}'
    ;;
  repos/tse-wei-chen/hs-sql-agent/actions/runs/1001/artifacts?per_page=100)
    if [[ "${MOCK_NO_SECURITY_ARTIFACT:-0}" == 1 ]]; then
      echo '{"artifacts":[]}'
    else
      jq -n --argjson expired "${MOCK_EXPIRED_ARTIFACT:-false}" \
        '{artifacts:[{name:"security-dependency-advisory-evidence",size_in_bytes:4096,
           expired:$expired}]}'
    fi
    ;;
  *) exit 42 ;;
esac
MOCK
chmod +x "$tmp/gh"
export PATH="$tmp:$PATH"
export GITHUB_REPOSITORY=tse-wei-chen/hs-sql-agent
export GITHUB_REF=refs/heads/develop
export GITHUB_SHA=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa
export GH_TOKEN=offline-token
export HS_SQL_AGENT_CANDIDATE_REPORT="$tmp/readiness.json"

expect_pass() {
    if ! bash "$gate" >"$tmp/out" 2>&1; then
        echo "Expected candidate pass: $1" >&2
        cat "$tmp/out" >&2
        exit 1
    fi
}
expect_deny() {
    local status
    status=0
    bash "$gate" >"$tmp/out" 2>&1 || status=$?
    if (( status != 1 )); then
        echo "Expected candidate denial (exit 1, got $status): $1" >&2
        cat "$tmp/out" >&2
        exit 1
    fi
}
expect_pending() {
    local status
    status=0
    bash "$gate" >"$tmp/out" 2>&1 || status=$?
    if (( status != 75 )); then
        echo "Expected candidate pending (exit 75, got $status): $1" >&2
        cat "$tmp/out" >&2
        exit 1
    fi
}

cd "$root"
expect_pass 'exact develop push with all 18 jobs and security artifact'
jq -e '.publicationAuthorized == false and .mainPromotionPerformed == false
       and .stage == "develop-release-candidate" and .testsJobsPassed == 18' \
    "$HS_SQL_AGENT_CANDIDATE_REPORT" >/dev/null

export GITHUB_REF=refs/heads/main
expect_deny 'never authorize main through the candidate gate'
export GITHUB_REF=refs/heads/develop
export MOCK_DEVELOP_SHA=bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb
expect_deny 'reject stale candidate commit'
unset MOCK_DEVELOP_SHA
export MOCK_NO_TEST_RUN=1
expect_pending 'missing exact develop Tests run'
unset MOCK_NO_TEST_RUN
export MOCK_TEST_STATUS=in_progress
expect_pending 'running Tests not a pass'
unset MOCK_TEST_STATUS
export MOCK_TEST_CONCLUSION=failure
export MOCK_OLD_TEST_SUCCESS=1
expect_deny 'newer failed Tests run overrides older success'
unset MOCK_TEST_CONCLUSION MOCK_OLD_TEST_SUCCESS
export MOCK_TEST_BRANCH=main
expect_pending 'main push is not develop candidate evidence'
unset MOCK_TEST_BRANCH
export MOCK_TEST_SHA=bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb
expect_pending 'wrong Tests SHA'
unset MOCK_TEST_SHA
export MOCK_TEST_JOBS=17
expect_deny 'missing Tests job'
unset MOCK_TEST_JOBS
export MOCK_WRONG_TEST_JOB=1
expect_deny 'impostor Tests job'
unset MOCK_WRONG_TEST_JOB
export MOCK_FAIL_JOB=1
expect_deny 'failed mandatory Tests job'
unset MOCK_FAIL_JOB
export MOCK_NO_SECURITY_ARTIFACT=1
expect_deny 'missing live dependency/compiler evidence'
unset MOCK_NO_SECURITY_ARTIFACT
export MOCK_EXPIRED_ARTIFACT=true
expect_deny 'expired security evidence'
unset MOCK_EXPIRED_ARTIFACT
export GH_TOKEN=
expect_deny 'no actions read token'
export GH_TOKEN=offline-token
export GITHUB_REPOSITORY=somebody/fork
expect_deny 'not upstream repository'
export GITHUB_REPOSITORY=tse-wei-chen/hs-sql-agent
echo "Develop-only release-candidate verifier: 15 positive/negative controls passed."
