#!/usr/bin/env bash
# Refuse protected-release publication until the tracked security release gate is closed.
# Every publisher invokes this BEFORE obtaining registry credentials or pushing.
set -euo pipefail

version="${1:?Pass the release tag or evaluated VersionPrefix}"
source "$(dirname "${BASH_SOURCE[0]}")/release-version.sh"
protected_version="$(read_release_property SecurityGateVersion)"
release_base="${version#v}"
release_base="${release_base%%[-+]*}"
if [[ "$release_base" != "$protected_version" ]]; then
    echo "Security issue #575 applies to $protected_version; version $version is outside its scope."
    exit 0
fi

if [[ "${GITHUB_REPOSITORY:-}" != "tse-wei-chen/hs-sql-agent" ]]; then
    echo "::error::Cannot verify #575 without the expected upstream repository identity." >&2
    exit 1
fi
if [[ -z "${GH_TOKEN:-}" ]]; then
    echo "::error::Cannot verify #575 without GitHub Issues read credentials." >&2
    exit 1
fi

# Security completion and publication authorization are distinct trust boundaries.
# Closing #575 after exact-develop security acceptance must never authorize
# NuGet/Docker publication without the owner's independently completed #634.
# Both are required even on a matching main SHA; the separate strict
# verify-release-head-ci-gate.sh additionally checks that main push.
for issue_number in 575 634; do
    if ! issue="$(gh api "repos/${GITHUB_REPOSITORY}/issues/$issue_number")"; then
        echo "::error::Cannot inspect security/publication gate #$issue_number; refusing publication of $protected_version." >&2
        exit 1
    fi
    if ! jq -e '
    .state == "closed"
    and .state_reason == "completed"
    and (.body | type == "string")
    and (.body | split("\n") | any(.[]; test("^[ \\t]*[-*][ \\t]+\\[[xX]\\]")))
    and (.body | split("\n") | all(.[]; test("^[ \\t]*[-*][ \\t]+\\[[ \\t]+\\]") | not))
' <<< "$issue" >/dev/null; then
        echo "::error::Security/publication gate #$issue_number must be completed with a nonempty, fully checked checklist; refusing publication of $protected_version." >&2
        exit 1
    fi
done

echo "Security develop gate #575 and owner publication gate #634 are both completed with no unchecked tasks. Exact main-head CI and release evidence remain separately mandatory."
