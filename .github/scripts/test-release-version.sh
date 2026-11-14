#!/usr/bin/env bash
# Verify shared metadata parsing fails closed without network or build tools.
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/release-version.sh"
version="$(read_release_property VersionPrefix)"
read_release_property SecurityGateVersion >/dev/null
read_release_property PackageApiBaselineVersion >/dev/null
read_release_property AdminMigrationBaselineVersion >/dev/null
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT
mkdir -p "$tmp/backend"
release_repo_root="$tmp"
printf '<Project>\n<VersionPrefix>%s</VersionPrefix>\n</Project>\n' "$version" > "$tmp/backend/Directory.Build.props"
[[ "$(read_release_property VersionPrefix)" == "$version" ]]
for value in '' "$version-rc.1" invalid; do
    printf '<Project>\n<VersionPrefix>%s</VersionPrefix>\n</Project>\n' "$value" > "$tmp/backend/Directory.Build.props"
    if read_release_property VersionPrefix >/dev/null 2>&1; then
        echo "Invalid metadata was accepted: $value" >&2
        exit 1
    fi
done
printf '<VersionPrefix>%s</VersionPrefix>\n<VersionPrefix>%s</VersionPrefix>\n' "$version" "$version" > "$tmp/backend/Directory.Build.props"
if read_release_property VersionPrefix >/dev/null 2>&1; then
    echo 'Duplicate release metadata was accepted' >&2
    exit 1
fi
echo 'Shared release metadata: valid, empty, malformed, prerelease and duplicate checks passed.'
