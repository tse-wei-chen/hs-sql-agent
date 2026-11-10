#!/usr/bin/env bash
# Offline negative/positive evidence for release SHA and all Tests jobs.
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
  "repos/tse-wei-chen/hs-sql-agent/actions/workflows/test.yml/runs?head_sha=${GITHUB_SHA}&branch=main&event=push&status=success&per_page=100")
    [[ "${MOCK_RUN_API_FAIL:-0}" != 1 ]] || exit 32
    if [[ "${MOCK_NO_RUN:-0}" == 1 ]]; then
      printf '{"workflow_runs":[]}\n'
    else
      printf '{"workflow_runs":[{"id":987,"head_sha":"%s","head_branch":"%s","event":"%s","status":"completed","conclusion":"%s"}]}\n' \
        "${MOCK_RUN_SHA:-$GITHUB_SHA}" "${MOCK_RUN_BRANCH:-main}" "${MOCK_RUN_EVENT:-push}" "${MOCK_RUN_CONCLUSION:-success}"
    fi
    ;;
  repos/tse-wei-chen/hs-sql-agent/actions/runs/987/jobs?per_page=100)
    [[ "${MOCK_JOBS_API_FAIL:-0}" != 1 ]] || exit 33
    count="${MOCK_JOB_COUNT:-18}"
    printf '{"total_count":%d,"jobs":[' "$count"
    for ((i=1;i<=count;i++)); do
      (( i == 1 )) || printf ','
      conclusion=success
      if (( i == 1 )) && [[ "${MOCK_FAIL_JOB:-0}" == 1 ]]; then conclusion=failure; fi
      printf '{"name":"job-%d","status":"completed","conclusion":"%s"}' "$i" "$conclusion"
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
echo 'Exact release-head Tests gate: 14 scenarios passed.'