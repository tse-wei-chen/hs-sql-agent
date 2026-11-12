#!/usr/bin/env bash
# CI contract: no server integration suite may be silently excluded from the
# default test job without a corresponding mandatory positive selection.
set -euo pipefail

workflow="${1:-.github/workflows/test.yml}"
test_root="backend/src/UnitTest/HsSqlAgent.Server.Test"
if [[ ! -f "$workflow" || ! -d "$test_root" ]]; then
  echo "Missing workflow or server test project" >&2
  exit 1
fi

# The default job must have exactly one exclusion expression. Never rely on
# grep matching a class in its own negative filter as proof it actually ran.
default_line="$(grep -F "run: dotnet test --project $test_root/HsSqlAgent.Server.Test.csproj --filter 'FullyQualifiedName!~" "$workflow" || true)"
if [[ -z "$default_line" || "$(printf '%s\n' "$default_line" | wc -l)" -ne 1 ]]; then
  echo "Expected exactly one explicit default Server test exclusion filter" >&2
  exit 1
fi

failures=0
IFS='&' read -ra exclusions <<< "${default_line#*--filter }"
for fragment in "${exclusions[@]}"; do
  class="${fragment#*FullyQualifiedName!~}"
  class="${class%\'}"
  if [[ "$class" == "$fragment" || -z "$class" ]]; then
    echo "Unexpected Server exclusion fragment: $fragment" >&2
    failures=$((failures + 1))
    continue
  fi
  # Positive selection can live in a different job (e.g. Redis, Oracle).
  # Native-function suites select per-provider method prefixes, not the
  # entire mixed-provider class, and must cover all five live providers.
  if [[ "$class" == "QueryNativeFunctionLiveTests" ]]; then
    for provider in MySql SqlServer Oracle Firebird; do
      if ! grep -Fq "FullyQualifiedName~QueryNativeFunctionLiveTests.${provider}_" "$workflow"; then
        echo "Missing positive native-function selection: $provider" >&2
        failures=$((failures + 1))
      fi
    done
    # This mixed-provider class currently contains four provider prefixes.
    continue
  fi
  if ! grep -Fq "FullyQualifiedName~${class}" "$workflow"; then
    echo "Orphan Server exclusion (never positively selected): $class" >&2
    failures=$((failures + 1))
  fi
  if ! grep -Rlq --include='*.cs' "class ${class}\\b" "$test_root"; then
    echo "Stale Server exclusion (test class no longer exists): $class" >&2
    failures=$((failures + 1))
  fi
done

# Dedicated Docker-backed fault tests must not be accidentally included in
# the generic unit suite or silently removed from their own required job.
for class in PostgresNativeFaultTestcontainersTests RedisProcessOutageTestcontainersTests; do
  if [[ "$default_line" != *"FullyQualifiedName!~${class}"* ]]; then
    echo "Physical fault test not isolated from generic Server suite: $class" >&2
    failures=$((failures + 1))
  fi
  if ! grep -Fq "FullyQualifiedName~${class}" "$workflow"; then
    echo "Physical fault test has no positive mandatory CI selection: $class" >&2
    failures=$((failures + 1))
  fi
done
if ! grep -Fq "Verify real Testcontainers PostgreSQL 16 native socket faults and Redis 7 process outage" "$workflow"; then
  echo "Missing dedicated physical-fault CI step" >&2
  failures=$((failures + 1))
fi

if (( failures > 0 )); then
  echo "Server integration ownership audit failed: $failures error(s)" >&2
  exit 1
fi
echo "Server integration ownership audit passed: all explicit exclusions have positive CI owners."
