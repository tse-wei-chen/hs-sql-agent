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

# The benchmark comparison must also attest THIS promoted main commit.
# A successful PR run or a workflow_dispatch self-comparison does not prove
# that the actual release SHA passed the baseline on main.
if ! baseline_runs="$(gh api "repos/$repo/actions/workflows/sqlcore-comparison.yml/runs?head_sha=$sha&branch=main&event=push&status=success&per_page=100")"; then
    echo '::error::Cannot read the exact-commit SqlCore comparison evidence.' >&2
    exit 1
fi
if ! baseline_run_id="$(jq -er --arg sha "$sha" '
    if (.workflow_runs | type) == "array" then
      [.workflow_runs[] | select(.head_sha == $sha and .head_branch == "main"
        and .event == "push" and .status == "completed" and .conclusion == "success")
        | .id | select(type == "number")] | first // empty
    else empty end' <<< "$baseline_runs")"; then
    echo '::error::No successful main push SqlCore comparison for this release SHA.' >&2
    exit 1
fi
if ! baseline_jobs="$(gh api "repos/$repo/actions/runs/$baseline_run_id/jobs?per_page=100")"; then
    echo '::error::Cannot read the exact-commit SqlCore comparison job.' >&2
    exit 1
fi
if ! jq -e '
    .total_count == 1
    and (.jobs | type) == "array"
    and (.jobs | length) == 1
    and (.jobs[0].name == "sqlcore-baseline-comparison")
    and (.jobs[0].status == "completed")
    and (.jobs[0].conclusion == "success")
  ' <<< "$baseline_jobs" >/dev/null; then
    echo '::error::The exact-main SqlCore baseline comparison is missing or unsuccessful.' >&2
    exit 1
fi

echo "Verified release SHA $sha against main, all 18 Tests jobs (run $run_id) and SqlCore baseline (run $baseline_run_id)."
