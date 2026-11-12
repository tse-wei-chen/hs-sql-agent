#!/usr/bin/env bash
# Negative tests for the *test governance* itself. A green suite-ownership
# audit is only meaningful if it rejects plausible silent test-loss changes.
set -euo pipefail

root="$(pwd)"
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT

export HS_SQL_AGENT_SERVER_SUITE_MANIFEST="$tmp/server-integration.tsv"
export HS_SQL_AGENT_SERVER_SUITE_WORKFLOW="$tmp/test.yml"
export HS_SQL_AGENT_SERVER_SUITE_TEST_ROOT="$root/backend/src/UnitTest/HsSqlAgent.Server.Test"
audit="$root/.github/scripts/audit-server-integration-ownership.sh"

reset_fixtures() {
    cp "$root/.github/test-suites/server-integration.tsv" "$HS_SQL_AGENT_SERVER_SUITE_MANIFEST"
    cp "$root/.github/workflows/test.yml" "$HS_SQL_AGENT_SERVER_SUITE_WORKFLOW"
}

assert_rejected() {
    local scenario="$1"
    if bash "$audit" --audit > "$tmp/audit.out" 2>&1; then
        echo "::error::Audit wrongly accepted $scenario" >&2
        cat "$tmp/audit.out" >&2
        exit 1
    fi
    echo "PASS: ownership audit rejects $scenario"
}

reset_fixtures
bash "$audit" --audit
default_filter="$(bash "$audit" --default-filter)"
[[ "$default_filter" == *"FullyQualifiedName!~PostgresNativeFaultTestcontainersTests"* ]]
[[ "$default_filter" == *"FullyQualifiedName!~RedisProcessOutageTestcontainersTests"* ]]
[[ "$default_filter" == *"FullyQualifiedName!~NetworkFaultTestcontainersTests"* ]]
[[ "$default_filter" == *"FullyQualifiedName!~ProviderTcpPartitionTestcontainersTests"* ]]
echo "PASS: valid ownership manifest and derived default filter"

reset_fixtures
# Wrong job must not pass merely because the selector exists in another job.
sed -i 's/^RedisDmlApprovalIntegrationTests[[:space:]]*redis-approval-integration/RedisDmlApprovalIntegrationTests\tserver-test/' "$HS_SQL_AGENT_SERVER_SUITE_MANIFEST"
assert_rejected "positive selector assigned to the wrong job"

reset_fixtures
# A renamed class in the manifest cannot leave a stale negative filter.
sed -i 's/^RedisDmlApprovalIntegrationTests/RedisDmlApprovalIntegrationTestsRenamed/' "$HS_SQL_AGENT_SERVER_SUITE_MANIFEST"
assert_rejected "stale class name"

reset_fixtures
# Duplicate ownership is not a valid way to disguise an unowned suite.
duplicate="$(grep '^RedisDmlApprovalIntegrationTests' "$HS_SQL_AGENT_SERVER_SUITE_MANIFEST")"
printf '%s\\n' "$duplicate" >> "$HS_SQL_AGENT_SERVER_SUITE_MANIFEST"
assert_rejected "duplicate ownership assignment"

reset_fixtures
# Physical fault tests must remain explicit in the manifest.
sed -i '/^PostgresNativeFaultTestcontainersTests[[:space:]]/d' "$HS_SQL_AGENT_SERVER_SUITE_MANIFEST"
assert_rejected "missing destructive Testcontainers class"

reset_fixtures
# A missing positive selector is a silent loss even if the class exists.
sed -i 's/FullyQualifiedName~RedisProcessOutageTestcontainersTests/NO_REDIS_OUTAGE_CASES/g' "$HS_SQL_AGENT_SERVER_SUITE_WORKFLOW"
assert_rejected "physical suite with no positive CI selector"

reset_fixtures
# The normal unit shard must derive its filter from the audited manifest.
sed -i 's/audit-server-integration-ownership.sh --default-filter/echo NO_FILTER/g' "$HS_SQL_AGENT_SERVER_SUITE_WORKFLOW"
assert_rejected "unit shard disconnected from manifest"

reset_fixtures
# The Docker gate must prove the exact number of physical tests.
sed -i 's/require-physical-test-cases.sh 1/require-physical-test-cases.sh 0/g' "$HS_SQL_AGENT_SERVER_SUITE_WORKFLOW"
assert_rejected "physical case-count gate removed"

reset_fixtures
# Removing one of the four providers must not be confused with the three
# other positive assignments to the same test class.
sed -i '/^ProviderTcpPartitionTestcontainersTests[[:space:]]*sqlagent-oracle-ddl-parity[[:space:]]/d' "$HS_SQL_AGENT_SERVER_SUITE_MANIFEST"
assert_rejected "Oracle omitted from mandatory four-provider TCP matrix"

reset_fixtures
# Merely invoking the test filter is not enough: the exact no-skip physical
# case count must be enforced in every required owner.
sed -i '/^      - name: Require Firebird real TCP-partition/,/^      - name: Verify live Firebird/s/require-physical-test-cases.sh 1/require-physical-test-cases.sh 0/' "$HS_SQL_AGENT_SERVER_SUITE_WORKFLOW"
assert_rejected "Firebird one-case gate disabled"

echo "All 9 negative ownership-audit regressions were rejected."
