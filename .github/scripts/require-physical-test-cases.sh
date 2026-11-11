#!/usr/bin/env bash
set -euo pipefail

# A valid dotnet test --filter can succeed with zero matching cases, and a
# physically missing provider may be reported as skipped. Neither constitutes
# real six-provider security evidence. Require the exact expected live corpus.
expected="${1:?expected succeeded case count}"
project="${2:?test project path}"
filter="${3:?fully qualified test filter}"

if [[ ! "$expected" =~ ^[1-9][0-9]*$ ]]; then
  echo "::error::Physical test case count must be a positive integer." >&2
  exit 2
fi

output="$(mktemp)"
trap 'rm -f "$output"' EXIT

dotnet test --project "$project" --filter "$filter" | tee "$output"

for result in "succeeded:$expected" "failed:0" "skipped:0"; do
  name="${result%%:*}"
  count="${result#*:}"
  if ! grep -Eq "${name}:[[:space:]]*${count}([[:space:]]|$)" "$output"; then
    echo "::error::Physical integration filter '$filter' did not produce the required $name=$count. A zero-match, skip, or missing database is not evidence." >&2
    exit 1
  fi
done

echo "Verified $expected live physical integration cases, 0 failed, 0 skipped: $filter"
