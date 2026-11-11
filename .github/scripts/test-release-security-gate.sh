#!/usr/bin/env bash
# Offline regression suite: no credentials or network access required.
set -euo pipefail

root="$(cd "$(dirname "$0")/../.." && pwd)"
gate="${root}/.github/scripts/verify-release-security-gate.sh"
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT
cat > "$tmp/gh" <<'MOCK'
#!/usr/bin/env bash
set -euo pipefail
[[ "$1" == api && "$2" == repos/tse-wei-chen/hs-sql-agent/issues/575 ]] || exit 32
[[ "${MOCK_ISSUE_API_FAIL:-}" != 1 ]] || exit 33
if [[ "$#" -eq 4 && "$3" == --jq && "$4" == .state ]]; then
    # Historical behavior: state-only inspection allows an incomplete close.
    printf '%s\n' "${MOCK_ISSUE_STATE:-open}"
elif [[ "$#" -eq 2 ]]; then
    # Full issue response is necessary to check closure reason and checklist.
    printf '{"state":"%s","state_reason":"%s","body":"%s"}\n' \
        "${MOCK_ISSUE_STATE:-open}" "${MOCK_ISSUE_REASON:-completed}" \
        "${MOCK_ISSUE_BODY-- [x] completed gate}"
else
    exit 32
fi
MOCK
chmod +x "$tmp/gh"
export PATH="$tmp:$PATH"
export GITHUB_REPOSITORY=tse-wei-chen/hs-sql-agent
export GH_TOKEN=offline-test-token

expect_deny() {
    if bash "$gate" "$1" >"$tmp/out" 2>&1; then
        echo "Expected release to be denied: $1" >&2
        cat "$tmp/out" >&2
        exit 1
    fi
}
expect_allow() {
    if ! bash "$gate" "$1" >"$tmp/out" 2>&1; then
        echo "Expected release to be allowed: $1" >&2
        cat "$tmp/out" >&2
        exit 1
    fi
}

# RED until publication guard exists: open issue blocks stable and prerelease.
export MOCK_ISSUE_STATE=open
expect_deny v3.1.0
expect_deny 3.1.0
expect_deny 3.1.0-rc.1
expect_deny v3.1.0+build.1

# Release must fail closed even when GitHub cannot be consulted.
export MOCK_ISSUE_STATE=unexpected
expect_deny v3.1.0
export MOCK_ISSUE_STATE=closed
export MOCK_ISSUE_API_FAIL=1
expect_deny v3.1.0
unset MOCK_ISSUE_API_FAIL
export GH_TOKEN=
expect_deny v3.1.0
export GH_TOKEN=offline-test-token
export GITHUB_REPOSITORY=someone/else
expect_deny v3.1.0
export GITHUB_REPOSITORY=tse-wei-chen/hs-sql-agent

# Test-first RED: mere closure is not equivalent to all release gates passing.
export MOCK_ISSUE_REASON=not_planned
expect_deny v3.1.0
unset MOCK_ISSUE_REASON
export MOCK_ISSUE_BODY='- [x] reviewed\n- [ ] live validation remains'
expect_deny v3.1.0
unset MOCK_ISSUE_BODY
export MOCK_ISSUE_BODY='- [ ] outstanding live-provider proof'
expect_deny v3.1.0
unset MOCK_ISSUE_BODY
export MOCK_ISSUE_BODY=''
expect_deny v3.1.0
unset MOCK_ISSUE_BODY

# Completed closure with verified, nonempty checklist is necessary, but not
# sufficient to publish without the separate exact-head Tests/baseline gate.
expect_allow v3.1.0
expect_allow 3.1.0-beta

# Never change the release policy of earlier, unrelated versions.
export MOCK_ISSUE_STATE=open
expect_allow v3.0.0
expect_allow 3.1.1

echo "3.1.0 release blocker regression: 16 scenarios passed."
