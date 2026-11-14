#!/usr/bin/env bash
set -euo pipefail

# A filtered dotnet test runs all target frameworks. Require the exact corpus
# for each TFM instead of accidentally comparing the combined total to one TFM.
expected="${1:?expected succeeded case count per framework}"
project="${2:?test project path}"
filter="${3:?fully qualified test filter}"
if [[ ! "$expected" =~ ^[1-9][0-9]*$ ]]; then
  echo "::error::Physical test case count must be a positive integer." >&2
  exit 2
fi

frameworks="$(dotnet msbuild "$project" -getProperty:TargetFrameworks -nologo | tail -n 1)"
if [[ -z "$frameworks" ]]; then
  frameworks="$(dotnet msbuild "$project" -getProperty:TargetFramework -nologo | tail -n 1)"
fi
IFS=';' read -r -a tfms <<< "$frameworks"
if (( ${#tfms[@]} == 0 )); then
  echo "::error::Could not determine test frameworks: $project" >&2
  exit 1
fi

output="$(mktemp)"
clean="$(mktemp)"
trap 'rm -f "$output" "$clean"' EXIT
dotnet test --project "$project" --filter "$filter" | tee "$output"
sed -E $'s/\033\\[[0-9;]*[[:alpha:]]//g' "$output" > "$clean"

for tfm in "${tfms[@]}"; do
  mapfile -t results < <(grep -F "($tfm|" "$clean" | grep -F " passed [" || true)
  if (( ${#results[@]} != 1 )); then
    echo "::error::Expected one physical result for $tfm, got ${#results[@]}: $filter" >&2
    exit 1
  fi
  compact="$(printf '%s' "${results[0]}" | tr -d '[:space:]')"
  if [[ "$compact" != *"passed[+$expected/x0/?0]"* ]]; then
    echo "::error::Physical $tfm must pass exactly $expected with no skips or failures: $filter" >&2
    exit 1
  fi
done

total=$(( expected * ${#tfms[@]} ))
for entry in "succeeded:$total" "failed:0" "skipped:0"; do
  name="${entry%%:*}"
  count="${entry#*:}"
  if ! grep -Eq "${name}:[[:space:]]*${count}([[:space:]]|$)" "$clean"; then
    echo "::error::Physical integration '$filter' missing total $name=$count." >&2
    exit 1
  fi
done

echo "Verified $expected physical cases per TFM (${tfms[*]}), $total total, 0 failed, 0 skipped: $filter"
