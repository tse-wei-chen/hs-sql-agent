#!/usr/bin/env bash
# Offline negative/positive evidence for release SHA and all required Tests jobs.
set -euo pipefail
root="$(cd "$(dirname "$0")/../.." && pwd)"
gate="$root/.github/scripts/verify-release-head-ci-gate.sh"
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT

cat > "$tmp/gh" <<'MOCK'
#!/usr/bin/env bash
set -euo pipefail
[[ "$1" == api ]] || exit 30
case "$2" in
  repos/tse-wei-chen/hs-sql-agent/branches/main)
    [[ "$#" == 4 && "$3" == --jq && "$4" == .commit.sha && "${MOCK_MAIN_API_FAIL:-0}" != 1 ]] || exit 31
    printf '%s\n' "${MOCK_MAIN_SHA:-$GITHUB_SHA}"
    ;;
  "repos/tse-wei-chen/hs-sql-agent/actions/workflows/test.yml/runs?head_sha=${GITHUB_SHA}&branch=main&event=push&status=success&per_page=100") | 
  "repos/tse-wei-chen/hs-sql-agent/actions/workflows/test.yml/runs?head_sha=${GITHUB_SHA}&branch=main&event=push&per_page=100")
    [[ "${MOCK_RUN_API_FAIL:-0}" != 1 ]] || exit 32
    if [[ "${MOCK_NO_RUN:-0}" == 1 ]]; then
      printf '{"workflow_runs":[]}\n'
    elif [[ "${MOCK_LATEST_TEST_STATUS:-}" != "" && "$2" != *'&status=success&'* ]]; then
      # GitHub's status=success filter hides a newer failed/pending run, leaving
      # the older successful run as false publication evidence.
      printf '{"workflow_runs":[{"id":1987,"head_sha":"%s","head_branch":"main","event":"push","status":"%s","conclusion":%s},{"id":987,"head_sha":"%s","head_branch":"main","event":"push","status":"completed","conclusion":"success"}]}\n' \
        "$GITHUB_SHA" "$MOCK_LATEST_TEST_STATUS" "${MOCK_LATEST_TEST_CONCLUSION:-null}" "$GITHUB_SHA"
    else
      printf '{"workflow_runs":[{"id":987,"head_sha":"%s","head_branch":"%s","event":"%s","status":"completed","conclusion":"%s"}]}\n' \
        "${MOCK_RUN_SHA:-$GITHUB_SHA}" "${MOCK_RUN_BRANCH:-main}" "${MOCK_RUN_EVENT:-push}" "${MOCK_RUN_CONCLUSION:-success}"
    fi
    ;;
  "repos/tse-wei-chen/hs-sql-agent/actions/workflows/sqlcore-comparison.yml/runs?head_sha=${GITHUB_SHA}&branch=main&event=push&status=success&per_page=100") | 
  "repos/tse-wei-chen/hs-sql-agent/actions/workflows/sqlcore-comparison.yml/runs?head_sha=${GITHUB_SHA}&branch=main&event=push&per_page=100")
    [[ "${MOCK_BASELINE_RUN_API_FAIL:-0}" != 1 ]] || exit 35
    if [[ "${MOCK_NO_BASELINE_RUN:-0}" == 1 ]]; then
      printf '{"workflow_runs":[]}\n'
    elif [[ "${MOCK_LATEST_BASELINE_STATUS:-}" != "" && "$2" != *'&status=success&'* ]]; then
      printf '{"workflow_runs":[{"id":1988,"head_sha":"%s","head_branch":"main","event":"push","status":"%s","conclusion":%s},{"id":988,"head_sha":"%s","head_branch":"main","event":"push","status":"completed","conclusion":"success"}]}\n' \
        "$GITHUB_SHA" "$MOCK_LATEST_BASELINE_STATUS" "${MOCK_LATEST_BASELINE_CONCLUSION:-null}" "$GITHUB_SHA"
    else
      printf '{"workflow_runs":[{"id":988,"head_sha":"%s","head_branch":"%s","event":"%s","status":"completed","conclusion":"%s"}]}\n' \
        "${MOCK_BASELINE_SHA:-$GITHUB_SHA}" "${MOCK_BASELINE_BRANCH:-main}" "${MOCK_BASELINE_EVENT:-push}" "${MOCK_BASELINE_CONCLUSION:-success}"
    fi
    ;;
  repos/tse-wei-chen/hs-sql-agent/actions/runs/988/jobs?per_page=100)
    [[ "${MOCK_BASELINE_JOBS_API_FAIL:-0}" != 1 ]] || exit 36
    printf '{"total_count":%d,"jobs":[{"name":"%s","status":"completed","conclusion":"%s"}]}\n' \
      "${MOCK_BASELINE_JOB_COUNT:-1}" "${MOCK_BASELINE_JOB_NAME:-sqlcore-baseline-comparison}" "${MOCK_BASELINE_JOB_CONCLUSION:-success}"
    ;;
  repos/tse-wei-chen/hs-sql-agent/actions/runs/987/jobs?per_page=100)
    [[ "${MOCK_JOBS_API_FAIL:-0}" != 1 ]] || exit 33
    count="${MOCK_JOB_COUNT:-18}"
    names=(
      admin-api-openapi admin-test auth-test common-test frontend-lint
      frontend-test infrastructure-test redis-approval-integration
      server-test sql-syntax-parity sqlagent-capability-stack
      sqlagent-fence-sqlserver sqlagent-firebird-ddl-parity
      sqlagent-oracle-ddl-parity sqlagent-package-smoke
      sqlagent-syntax-matrix sqlagent-test sqlagent-test-rest
    )
    printf '{"total_count":%d,"jobs":[' "$count"
    for ((i=1;i<=count;i++)); do
      (( i == 1 )) || printf ','
      conclusion=success
      name="${names[i-1]:-unexpected-job}"
      if (( i == 1 )) && [[ "${MOCK_FAIL_JOB:-0}" == 1 ]]; then conclusion=failure; fi
      if (( i == 1 )) && [[ "${MOCK_WRONG_TEST_JOB:-0}" == 1 ]]; then name=impostor-job; fi
      printf '{"name":"%s","status":"completed","conclusion":"%s"}' "$name" "$conclusion"
    done
    printf ']}\n'
    ;;
  *) exit 34 ;;
esac
MOCK
chmod +x "$tmp/gh"
export PATH="$tmp:$PATH"
export GH_TOKEN=offline-token GITHUB_REPOSITORY=tse-wei-chen/hs-sql-agent
export GITHUB_SHA=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa
expect_deny() {
  if bash "$gate" >"$tmp/out" 2>&1; then
    echo "Expected release gate deny: $1" >&2
    cat "$tmp/out" >&2
    exit 1
  fi
}
expect_allow() {
  if ! bash "$gate" >"$tmp/out" 2>&1; then
    echo "Expected release gate allow: $1" >&2
    cat "$tmp/out" >&2
    exit 1
  fi
}
expect_allow 'all 18 successful jobs at exact main SHA'
# RED: a previous success cannot authorize publication when a newer run for
# the *same* main SHA has failed or is still in progress.
export MOCK_LATEST_TEST_STATUS=completed MOCK_LATEST_TEST_CONCLUSION='"failure"'
expect_deny 'newer Tests run failed despite older successful run'
unset MOCK_LATEST_TEST_CONCLUSION
export MOCK_LATEST_TEST_STATUS=in_progress
expect_deny 'newer Tests run is still in progress'
unset MOCK_LATEST_TEST_STATUS
export MOCK_LATEST_BASELINE_STATUS=completed MOCK_LATEST_BASELINE_CONCLUSION='"failure"'
expect_deny 'newer SqlCore comparison failed despite older successful run'
unset MOCK_LATEST_BASELINE_CONCLUSION
export MOCK_LATEST_BASELINE_STATUS=in_progress
expect_deny 'newer SqlCore comparison is still in progress'
unset MOCK_LATEST_BASELINE_STATUS
export MOCK_NO_BASELINE_RUN=1
expect_deny 'missing exact-main SqlCore baseline'
unset MOCK_NO_BASELINE_RUN
export MOCK_BASELINE_SHA=bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb
expect_deny 'baseline for wrong commit'
unset MOCK_BASELINE_SHA
export MOCK_BASELINE_BRANCH=develop
expect_deny 'baseline from wrong branch'
unset MOCK_BASELINE_BRANCH
export MOCK_BASELINE_EVENT=workflow_dispatch
expect_deny 'manual baseline is not main push evidence'
unset MOCK_BASELINE_EVENT
export MOCK_BASELINE_CONCLUSION=failure
expect_deny 'failed baseline'
unset MOCK_BASELINE_CONCLUSION
export MOCK_BASELINE_RUN_API_FAIL=1
expect_deny 'baseline runs API failure'
unset MOCK_BASELINE_RUN_API_FAIL
export MOCK_BASELINE_JOB_COUNT=2
expect_deny 'unexpected baseline job count'
unset MOCK_BASELINE_JOB_COUNT
export MOCK_BASELINE_JOB_NAME=unrelated-job
expect_deny 'missing baseline job identity'
unset MOCK_BASELINE_JOB_NAME
export MOCK_BASELINE_JOB_CONCLUSION=failure
expect_deny 'failed baseline job'
unset MOCK_BASELINE_JOB_CONCLUSION
export MOCK_BASELINE_JOBS_API_FAIL=1
expect_deny 'baseline jobs API failure'
unset MOCK_BASELINE_JOBS_API_FAIL
export MOCK_MAIN_SHA=bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb
expect_deny 'tag not at main HEAD'
unset MOCK_MAIN_SHA
export MOCK_MAIN_API_FAIL=1
expect_deny 'main API failure'
unset MOCK_MAIN_API_FAIL
export MOCK_NO_RUN=1
expect_deny 'no successful run'
unset MOCK_NO_RUN
export MOCK_RUN_SHA=bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb
expect_deny 'other SHA'
unset MOCK_RUN_SHA
export MOCK_RUN_BRANCH=develop
expect_deny 'other branch'
unset MOCK_RUN_BRANCH
export MOCK_RUN_EVENT=pull_request
expect_deny 'PR run not push'
unset MOCK_RUN_EVENT
export MOCK_RUN_CONCLUSION=failure
expect_deny 'failed run'
unset MOCK_RUN_CONCLUSION
export MOCK_RUN_API_FAIL=1
expect_deny 'runs API failure'
unset MOCK_RUN_API_FAIL
export MOCK_JOB_COUNT=17
expect_deny 'missing job'
unset MOCK_JOB_COUNT
export MOCK_FAIL_JOB=1
expect_deny 'failed job'
unset MOCK_FAIL_JOB
export MOCK_WRONG_TEST_JOB=1
expect_deny 'unrelated jobs cannot substitute for required Tests jobs'
unset MOCK_WRONG_TEST_JOB
export MOCK_JOBS_API_FAIL=1
expect_deny 'jobs API failure'
unset MOCK_JOBS_API_FAIL
export GH_TOKEN=
expect_deny 'missing token'
export GH_TOKEN=offline-token
export GITHUB_REPOSITORY=someone/else
expect_deny 'other repo'
export GITHUB_REPOSITORY=tse-wei-chen/hs-sql-agent
export GITHUB_SHA=invalid
expect_deny 'invalid SHA'
echo 'Exact release-head Tests gate: 30 scenarios passed.'