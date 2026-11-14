#!/usr/bin/env bash
set -euo pipefail

# Compare published packages against the immutable baseline in shared MSBuild metadata.
source "$(dirname "${BASH_SOURCE[0]}")/release-version.sh"
baseline_version="$(read_release_property PackageApiBaselineVersion)"
package_dir="$(cd "${1:?Packed NuGet directory is required}" && pwd)"
version="${2:?Packed NuGet version is required}"

if [[ "$version" == "$baseline_version" || "$version" == "$baseline_version"-* ]]; then
  echo "[Package API baseline] Initial $baseline_version candidate: no published baseline package yet."
  echo "[Package API baseline] Subsequent builds will be checked against NuGet $baseline_version."
  exit 0
fi
if [[ "${version%%.*}" != "${baseline_version%%.*}" ]]; then
  echo "[Package API baseline] Version $version is outside the baseline compatibility track."
  exit 0
fi

shopt -s nullglob
current_packages=("$package_dir"/HsSqlAgent.*."$version".nupkg)
if (( ${#current_packages[@]} == 0 )); then
  echo "No public HsSqlAgent packages for $version in $package_dir" >&2
  exit 1
fi

temp_dir="$(mktemp -d)"
trap 'rm -rf "$temp_dir"' EXIT
baseline_dir="$temp_dir/baseline"
mkdir -p "$baseline_dir"

# Pin the SDK-aligned APICompat tool. It checks removed/changed public
# signatures and parameter names; no ignore list or silent suppression.
dotnet tool install Microsoft.DotNet.ApiCompat.Tool \
  --version 10.0.100 --tool-path "$temp_dir/tools"
apicompat="$temp_dir/tools/apicompat"

for package in "${current_packages[@]}"; do
  filename="$(basename "$package")"
  package_id="${filename%.$version.nupkg}"
  if [[ -z "$package_id" || "$package_id" == "$filename" ]]; then
    echo "Cannot extract package ID from $filename" >&2
    exit 1
  fi
  id_lower="$(printf '%s' "$package_id" | tr '[:upper:]' '[:lower:]')"
  baseline="$baseline_dir/$package_id.$baseline_version.nupkg"
  url="https://api.nuget.org/v3-flatcontainer/$id_lower/$baseline_version/$id_lower.$baseline_version.nupkg"
  echo "[Package API baseline] $package_id: $version against published $baseline_version"
  # A missing baseline is not a green skip. Introducing a new package needs
  # an explicit release-contract decision, never an implicit compatibility pass.
  curl --fail --show-error --silent --location --retry 3 \
    "$url" --output "$baseline"
  "$apicompat" package "$package" --baseline-package "$baseline" \
    --enable-rule-cannot-change-parameter-name
done

echo "[Package API baseline] All published HsSqlAgent package APIs are compatible."
