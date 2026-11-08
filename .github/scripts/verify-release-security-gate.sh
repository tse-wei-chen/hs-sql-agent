#!/usr/bin/env bash
# Refuse 3.1.0 publication until the tracked security release gate is closed.
# Every publisher invokes this BEFORE obtaining registry credentials or pushing.
set -euo pipefail

version="${1:?Pass the release tag or evaluated VersionPrefix}"
# Include 3.1.0 pre-releases from manual NuGet dispatch: a suffix must not
# bypass the blocker when the underlying package version is still 3.1.0.
if [[ ! "$version" =~ ^v?3\.1\.0($|[-+].*) ]]; then
    echo "Security issue #575 is specific to v3.1.0; version ${version} is outside its scope."
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

# The API must respond successfully with an explicitly closed issue. Missing
# privileges, rate limits, network errors or malformed states all deny release.
if ! state="$(gh api "repos/${GITHUB_REPOSITORY}/issues/575" --jq '.state')"; then
    echo "::error::Could not verify security release gate #575; refusing publication." >&2
    exit 1
fi
if [[ "$state" != "closed" ]]; then
    echo "::error::Security release gate #575 is not closed (state=${state}); refusing v3.1.0 publication." >&2
    exit 1
fi

echo "Security release gate #575 is closed. Continue only with exact-head CI and live-provider evidence verified."
