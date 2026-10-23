#!/usr/bin/env bash
set -euo pipefail

# Compare *published binaries*, not source projects: downstream consumers load
# the public signatures and assets from .nupkg. This gate becomes mandatory
# once v3.0.0 has been published. The initial 3.0.0 PR cannot compare against
# a release that does not exist, but all later v3.x PRs must.
package_dir="$(cd "${1:?Packed NuGet directory is required}" && pwd)"
version="${2:?Packed NuGet version is required}"

if [[ "$version" == "3.0.0" || "$version" == 3.0.0-* ]]; then
  echo "[v3 API baseline] Initial 3.0.0 candidate: no published 3.0.0 package yet."
  echo "[v3 API baseline] v3.0.1+ builds will be checked against NuGet 3.0.0."
  exit 0
fi
if [[ "${version%%.*}" != "3" ]]; then
  echo "[v3 API baseline] Version $version is outside the v3 compatibility track."
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
  baseline="$baseline_dir/$package_id.3.0.0.nupkg"
  url="https://api.nuget.org/v3-flatcontainer/$id_lower/3.0.0/$id_lower.3.0.0.nupkg"
  echo "[v3 API baseline] $package_id: $version against published 3.0.0"
  # A missing baseline is not a green skip. Introducing a new package needs
  # an explicit release-contract decision, never an implicit compatibility pass.
  curl --fail --show-error --silent --location --retry 3 \
    "$url" --output "$baseline"
  "$apicompat" package "$package" --baseline-package "$baseline" \
    --enable-rule-cannot-change-parameter-name
done

echo "[v3 API baseline] All published HsSqlAgent package APIs are compatible."
