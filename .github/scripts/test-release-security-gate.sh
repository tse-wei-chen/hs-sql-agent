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
[[ "$1" == api && "$#" -eq 2 ]] || exit 32
case "$2" in
  repos/tse-wei-chen/hs-sql-agent/issues/575)
    [[ "${MOCK_ISSUE_API_FAIL:-0}" != 1 ]] || exit 33
    state="${MOCK_ISSUE_STATE:-open}"
    reason="${MOCK_ISSUE_REASON:-completed}"
    body="${MOCK_ISSUE_BODY-- [x] completed develop security gate}"
    ;;
  repos/tse-wei-chen/hs-sql-agent/issues/634)
    [[ "${MOCK_PUB_API_FAIL:-0}" != 1 ]] || exit 34
    state="${MOCK_PUB_STATE:-closed}"
    reason="${MOCK_PUB_REASON:-completed}"
    body="${MOCK_PUB_BODY-- [x] owner authorized main promotion and release}"
    ;;
  *) exit 32 ;;
esac
printf '{"state":"%s","state_reason":"%s","body":"%s"}\n' "$state" "$reason" "$body"
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

# Owner-gated publication issue #634 is a distinct, required security
# preflight. A completed develop gate #575 alone MUST NOT authorize 3.1.0.
export MOCK_ISSUE_STATE=closed
export MOCK_PUB_STATE=open
expect_deny v3.1.0
expect_deny 3.1.0-rc.1
export MOCK_PUB_STATE=closed
export MOCK_PUB_REASON=not_planned
expect_deny v3.1.0
unset MOCK_PUB_REASON
export MOCK_PUB_BODY='- [x] main passed\n- [ ] owner did not approve release'
expect_deny v3.1.0
unset MOCK_PUB_BODY
export MOCK_PUB_BODY=''
expect_deny v3.1.0
unset MOCK_PUB_BODY
export MOCK_PUB_API_FAIL=1
expect_deny v3.1.0
unset MOCK_PUB_API_FAIL

# Both security/develop completion and owner/publication sign-off needed.
expect_allow v3.1.0

# Never change the release policy of earlier, unrelated versions.
export MOCK_ISSUE_STATE=open
expect_allow v3.0.0
expect_allow 3.1.1

echo "3.1.0 two-issue publication blocker regression: 23 scenarios passed."
