#!/usr/bin/env bash
# The ownership manifest is the *single* source for Server's excluded suites.
# Its positive selectors must belong to real mandatory workflow jobs.
set -euo pipefail

manifest="${HS_SQL_AGENT_SERVER_SUITE_MANIFEST:-.github/test-suites/server-integration.tsv}"
workflow="${HS_SQL_AGENT_SERVER_SUITE_WORKFLOW:-.github/workflows/test.yml}"
test_root="${HS_SQL_AGENT_SERVER_SUITE_TEST_ROOT:-backend/src/UnitTest/HsSqlAgent.Server.Test}"
mode="${1:---audit}"

if [[ ! -f "$manifest" || ! -f "$workflow" || ! -d "$test_root" ]]; then
    echo "::error::Server integration ownership inputs are missing." >&2
    exit 1
fi

declare -A unique_suites=()
declare -A unique_assignments=()
declare -a exclusions=()
declare -a classes=()
declare -a jobs=()
declare -a selectors=()

while IFS=$'\t' read -r suite job selector extra || [[ -n "${suite:-}" ]]; do
    [[ -z "$suite" || "$suite" == \#* ]] && continue
    if [[ -n "${extra:-}" || ! "$suite" =~ ^[A-Za-z][A-Za-z0-9_]*$
          || ! "$job" =~ ^[a-z][a-z0-9-]*$
          || ! "$selector" =~ ^FullyQualifiedName~[A-Za-z0-9_.]+$ ]]; then
        echo "::error::Malformed Server integration assignment: $suite / $job / $selector" >&2
        exit 1
    fi
    if [[ -n "${unique_assignments[$suite:$job:$selector]:-}" ]]; then
        echo "::error::Duplicate integration ownership: $suite / $job" >&2
        exit 1
    fi
    unique_assignments["$suite:$job:$selector"]=1
    if [[ -z "${unique_suites[$suite]:-}" ]]; then
        unique_suites["$suite"]=1
        exclusions+=("FullyQualifiedName!~$suite")
    fi
    classes+=("$suite")
    jobs+=("$job")
    selectors+=("$selector")
done < "$manifest"

if (( ${#exclusions[@]} == 0 )); then
    echo "::error::An empty integration manifest would run destructive tests in the unit shard." >&2
    exit 1
fi

case "$mode" in
    --default-filter)
        # Stable manifest ordering retains the exact pre-refactor VSTest selection.
        joined="$(IFS='&'; echo "${exclusions[*]}")"
        printf '%s\n' "$joined"
        ;;
    --audit)
        failures=0
        for i in "${!classes[@]}"; do
            suite="${classes[i]}"
            job="${jobs[i]}"
            selector="${selectors[i]}"
            if [[ "$selector" != "FullyQualifiedName~$suite"
                  && "$selector" != "FullyQualifiedName~$suite."* ]]; then
                echo "::error::Selector escapes suite '$suite': $selector" >&2
                failures=$((failures + 1))
            fi
            # Do not trust arbitrary string occurrences elsewhere in YAML:
            # the positive selector must be inside its named workflow job.
            job_block="$(awk -v job="$job" '
                $0 == "  " job ":" { found = 1; next }
                found && /^  [A-Za-z][A-Za-z0-9-]*:/ { exit }
                found { print }
            ' "$workflow")"
            if [[ -z "$job_block" || "$job_block" != *"$selector"* ]]; then
                echo "::error::No required positive selector '$selector' in CI job '$job'." >&2
                failures=$((failures + 1))
            fi
            if ! grep -RlEq --include='*.cs'                 "class[[:space:]]+$suite([^[:alnum:]_]|$)" "$test_root"; then
                echo "::error::Stale integration class '$suite' in manifest." >&2
                failures=$((failures + 1))
            fi
        done

        # Separate destructive Docker cases must be excluded from the
        # ordinary shard *and* positively selected in the dedicated step.
        for suite in PostgresNativeFaultTestcontainersTests RedisProcessOutageTestcontainersTests NetworkFaultTestcontainersTests ProviderTcpPartitionTestcontainersTests; do
            if [[ -z "${unique_suites[$suite]:-}" ]]; then
                echo "::error::Missing mandatory fault suite from manifest: $suite" >&2
                failures=$((failures + 1))
            fi
        done

        if ! grep -Fq 'bash .github/scripts/audit-server-integration-ownership.sh --default-filter' "$workflow"; then
            echo "::error::Default Server unit shard is not driven by the audited manifest." >&2
            failures=$((failures + 1))
        fi
        if ! grep -Fq -- 'require-physical-test-cases.sh 4' "$workflow"; then
            echo "::error::Four-case backend/process fault step is missing." >&2
            failures=$((failures + 1))
        fi
        if [[ "$(grep -Fc 'require-physical-test-cases.sh 1' "$workflow")" -lt 2 ]]; then
            echo "::error::PostgreSQL and Redis TCP-partition cases must run as two isolated mandatory one-case steps." >&2
            failures=$((failures + 1))
        fi
        # A single proxy test class is partitioned across four dedicated
        # real-provider jobs. Assert every required assignment exists; the
        # mere presence of one class name is not four-provider evidence.
        for assignment in \
            "sqlagent-test:FullyQualifiedName~ProviderTcpPartitionTestcontainersTests.MySql_" \
            "sqlagent-fence-sqlserver:FullyQualifiedName~ProviderTcpPartitionTestcontainersTests.SqlServer_" \
            "sqlagent-oracle-ddl-parity:FullyQualifiedName~ProviderTcpPartitionTestcontainersTests.Oracle_" \
            "sqlagent-firebird-ddl-parity:FullyQualifiedName~ProviderTcpPartitionTestcontainersTests.Firebird_"; do
            job="${assignment%%:*}"
            selector="${assignment#*:}"
            if [[ -z "${unique_assignments[ProviderTcpPartitionTestcontainersTests:$job:$selector]:-}" ]]; then
                echo "::error::Missing mandatory provider TCP-partition assignment: $assignment" >&2
                failures=$((failures + 1))
            fi
            # Reject a provider step that mentions the selector but no longer
            # verifies exactly one passed, zero failed and zero skipped.
            provider_block="$(awk -v job="$job" '
                $0 == "  " job ":" { found = 1; next }
                found && /^  [A-Za-z][A-Za-z0-9-]*:/ { exit }
                found { print }
            ' "$workflow")"
            if [[ "$provider_block" != *"require-physical-test-cases.sh 1"* ]]; then
                echo "::error::Provider $job lacks a mandatory one-case no-skip physical gate." >&2
                failures=$((failures + 1))
            fi
        done
        # The already-implemented physical UDF / identity negative controls are
        # release security evidence, not optional filtered smoke tests. Require
        # each provider's exact test count, no-failure and no-skip gate.
        for spec in \
            "sqlagent-test:FullyQualifiedName~QueryNativeFunctionLiveTests.MySql_:3" \
            "sqlagent-fence-sqlserver:FullyQualifiedName~QueryNativeFunctionLiveTests.SqlServer_:2" \
            "sqlagent-oracle-ddl-parity:FullyQualifiedName~QueryNativeFunctionLiveTests.Oracle_:4" \
            "sqlagent-firebird-ddl-parity:FullyQualifiedName~QueryNativeFunctionLiveTests.Firebird_:1"; do
            IFS=: read -r job selector expected <<< "$spec"
            if [[ -z "${unique_assignments[QueryNativeFunctionLiveTests:$job:$selector]:-}" ]]; then
                echo "::error::Missing mandatory native Query security suite: $spec" >&2
                failures=$((failures + 1))
                continue
            fi
            job_block="$(awk -v job="$job" '
                $0 == "  " job ":" { found = 1; next }
                found && /^  [A-Za-z][A-Za-z0-9-]*:/ { exit }
                found { print }
            ' "$workflow")"
            expected_fragment="require-physical-test-cases.sh $expected
          backend/src/UnitTest/HsSqlAgent.Server.Test/HsSqlAgent.Server.Test.csproj
          '$selector'"
            if [[ "$job_block" != *"$expected_fragment"* ]]; then
                echo "::error::Native Query security test '$spec' is missing its exact-case no-skip gate." >&2
                failures=$((failures + 1))
            fi
        done

        if (( failures > 0 )); then
            echo "::error::Server integration ownership audit failed ($failures errors)." >&2
            exit 1
        fi
        echo "Server integration ownership passed: ${#unique_suites[@]} suites, ${#classes[@]} explicit CI assignments."
        ;;
    *)
        echo "Usage: bash $0 [--audit|--default-filter]" >&2
        exit 2
        ;;
esac
