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

# A manually closed issue is not proof that every blocking release gate
# passed. Require a completed closure and a nonempty, fully checked Markdown
# checklist. Read the full structured issue in one API request and deny on
# missing/malformed fields, API failures and unchecked tasks.
if ! issue="$(gh api "repos/${GITHUB_REPOSITORY}/issues/575")"; then
    echo "::error::Could not verify security release gate #575; refusing publication." >&2
    exit 1
fi
if ! jq -e '
    .state == "closed"
    and .state_reason == "completed"
    and (.body | type == "string")
    and (.body | split("\n") | any(.[]; test("^[ \\t]*[-*][ \\t]+\\[[xX]\\]")))
    and (.body | split("\n") | all(.[]; test("^[ \\t]*[-*][ \\t]+\\[[ \\t]+\\]") | not))
' <<< "$issue" >/dev/null; then
    echo "::error::Security release gate #575 must be closed as completed with a nonempty, fully checked checklist; refusing v3.1.0 publication." >&2
    exit 1
fi

echo "Security release gate #575 is completed with no unchecked tasks. Continue only with exact-head CI and live-provider evidence verified."
