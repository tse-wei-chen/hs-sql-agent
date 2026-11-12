#!/usr/bin/env bash
# Offline negative checks for the security evidence parser: an empty scan,
# a hidden advisory, or an unavailable advisory feed cannot authorize release.
set -euo pipefail
root="$(cd "$(dirname "$0")/../.." && pwd)"
gate="$root/.github/scripts/audit-dependency-security.sh"
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT

cat > "$tmp/nuget.json" <<'JSON'
{"version":1,"projects":[{"path":"api.csproj","frameworks":[
  {"framework":"net10.0","topLevelPackages":[{"id":"safe","vulnerabilities":[]}],
  "transitivePackages":[]}]}]}
JSON
cat > "$tmp/pnpm.json" <<'JSON'
{"metadata":{"vulnerabilities":{"low":0,"moderate":0,"high":0,"critical":0}}}
JSON

require_success() {
  if ! bash "$gate" --verify "$tmp/nuget.json" "$tmp/pnpm.json" >/dev/null; then
    echo "::error::Clean evidence was incorrectly rejected: $1" >&2
    exit 1
  fi
}
require_reject() {
  if bash "$gate" --verify "$tmp/nuget.json" "$tmp/pnpm.json" >/dev/null 2>&1; then
    echo "::error::Unsafe evidence was accepted: $1" >&2
    exit 1
  fi
}

require_success clean
cp "$tmp/nuget.json" "$tmp/nuget.clean"
cp "$tmp/pnpm.json" "$tmp/pnpm.clean"

echo '{}' > "$tmp/nuget.json"
require_reject missing-nuget-scan
cp "$tmp/nuget.clean" "$tmp/nuget.json"

jq '.projects[0].frameworks[0].topLevelPackages[0].vulnerabilities =
  [{"severity":"High","advisoryurl":"https://example.test/ghsa"}]' \
  "$tmp/nuget.clean" > "$tmp/nuget.json"
require_reject nuget-high
jq '.projects[0].frameworks[0].topLevelPackages[0].vulnerabilities =
  [{"severity":3,"advisoryurl":"https://example.test/cve"}]' \
  "$tmp/nuget.clean" > "$tmp/nuget.json"
require_reject nuget-critical-numeric
cp "$tmp/nuget.clean" "$tmp/nuget.json"

jq '.metadata.vulnerabilities.high = 1' "$tmp/pnpm.clean" > "$tmp/pnpm.json"
require_reject pnpm-high
jq '.metadata.vulnerabilities.critical = 1' "$tmp/pnpm.clean" > "$tmp/pnpm.json"
require_reject pnpm-critical
echo '{"error":"registry unavailable"}' > "$tmp/pnpm.json"
require_reject unavailable-registry
echo '{}' > "$tmp/pnpm.json"
require_reject unpopulated-report
cp "$tmp/pnpm.clean" "$tmp/pnpm.json"

# Moderate NuGet advisories must remain visible in evidence but do not
# silently elevate to a high/critical automatic veto.
jq '.projects[0].frameworks[0].topLevelPackages[0].vulnerabilities =
  [{"severity":"Moderate"}]' "$tmp/nuget.clean" > "$tmp/nuget.json"
require_success moderate-recorded
echo 'Security advisory parser: 9 positive/negative controls passed.'
