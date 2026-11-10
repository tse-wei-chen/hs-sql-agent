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

# Check GitHub's run filters AND the returned run identity. A pull_request,
# release-triggered or different-SHA workflow cannot authorize publication.
if ! runs="$(gh api "repos/$repo/actions/workflows/test.yml/runs?head_sha=$sha&branch=main&event=push&status=success&per_page=100")"; then
    echo '::error::Cannot read the exact-commit Tests workflow evidence.' >&2
    exit 1
fi
if ! run_id="$(jq -er --arg sha "$sha" '
    if (.workflow_runs | type) == "array" then
      [.workflow_runs[] | select(.head_sha == $sha and .head_branch == "main"
        and .event == "push" and .status == "completed" and .conclusion == "success")
        | .id | select(type == "number")] | first // empty
    else empty end' <<< "$runs")"; then
    echo '::error::No successful main push Tests run for the exact release SHA.' >&2
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
    and (.jobs | map(.name) | unique | length) == 18
    and (.jobs | all(.[]; .status == "completed" and .conclusion == "success"))
  ' <<< "$jobs" >/dev/null; then
    echo '::error::Release requires exactly 18 completed, successful Tests jobs on the exact main SHA.' >&2
    exit 1
fi

echo "Verified release SHA $sha against main and all 18 Tests jobs (run $run_id)."
