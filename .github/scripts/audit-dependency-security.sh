#!/usr/bin/env bash
# #575 reproducible production dependency advisory evidence for the exact CI SHA.
# Do not silently equate "scan command completed" with a vulnerability-free build.
set -euo pipefail

root="$(cd "$(dirname "$0")/../.." && pwd)"
out="${HS_SQL_AGENT_SECURITY_AUDIT_DIR:-$root/artifacts/security}"
mkdir -p "$out"
nuget="$out/nuget-vulnerabilities.json"
pnpm_report="$out/pnpm-production-vulnerabilities.json"

verify_reports() {
  local nuget_report="$1" node_report="$2"
  jq -e '.version == 1 and (.projects | type == "array")' "$nuget_report" >/dev/null || {
    echo "::error::NuGet vulnerability report missing or malformed (no coverage evidence)." >&2
    exit 1
  }
  # NuGet vulnerability severity is expressed as Low/Moderate/High/Critical.
  # Only high/critical block this gate; lower severities remain in the artifact
  # for explicit review (they are not silently removed).
  local nuget_total nuget_high node_high node_critical
  nuget_total="$(jq '[.projects[]?.frameworks[]? | (.topLevelPackages[]?, .transitivePackages[]?) |
                     (.vulnerabilities // [])[]] | length' "$nuget_report")"
  nuget_high="$(jq '[.projects[]?.frameworks[]? | (.topLevelPackages[]?, .transitivePackages[]?) |
                    (.vulnerabilities // [])[] |
                    select((.severity | tostring | ascii_downcase) == "high"
                           or (.severity | tostring | ascii_downcase) == "critical")] | length' "$nuget_report")"
  jq -e '(.metadata.vulnerabilities | type == "object") and (.error == null)' "$node_report" >/dev/null || {
    echo "::error::pnpm registry audit failed or returned no usable advisory metadata." >&2
    exit 1
  }
  node_high="$(jq -r '.metadata.vulnerabilities.high // 0' "$node_report")"
  node_critical="$(jq -r '.metadata.vulnerabilities.critical // 0' "$node_report")"
  printf 'NuGet advisory entries: %s (high/critical: %s)\n' "$nuget_total" "$nuget_high"
  printf 'pnpm production high: %s; critical: %s\n' "$node_high" "$node_critical"
  if (( nuget_high > 0 || node_high > 0 || node_critical > 0 )); then
    echo "::error::Unreviewed high/critical dependency advisories block the release evidence gate." >&2
    exit 1
  fi
}

if [[ "${1:-}" == "--verify" ]]; then
  verify_reports "${2:?NuGet report path}" "${3:?pnpm report path}"
  exit 0
fi
[[ "$#" == 0 ]] || { echo "Usage: $0 [--verify nuget.json pnpm.json]" >&2; exit 2; }

# Exact project/solution and committed pnpm lockfile only; fail on inability
# to reach advisory feeds. No --ignore-registry-errors or best-effort skip.
dotnet restore "$root/backend/hs-sql-agent.slnx" --verbosity quiet
dotnet package list --project "$root/backend/hs-sql-agent.slnx" \
  --no-restore --vulnerable --include-transitive \
  --format json --output-version 1 > "$nuget"

set +e
(cd "$root/frontend" && pnpm audit --prod --json) > "$pnpm_report"
audit_exit=$?
set -e
# Nonzero from pnpm can mean a vulnerability; parsing the report distinguishes
# advisories from registry errors and always checks high+critical severity.
if (( audit_exit > 1 )); then
  echo "::error::pnpm audit failed before advisory evaluation (exit=$audit_exit)." >&2
  exit 1
fi
verify_reports "$nuget" "$pnpm_report"
