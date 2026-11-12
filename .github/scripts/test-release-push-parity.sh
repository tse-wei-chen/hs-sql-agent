#!/usr/bin/env bash
# Offline regression: both develop and main push CI must run syntax parity
# against the previous branch SHA, or the 18/18 release gate can never pass.
set -euo pipefail
root="$(cd "$(dirname "$0")/../.." && pwd)"
source_file="$root/.github/workflows/test.yml"
scratch="$(mktemp -d)"
trap 'rm -rf "$scratch"' EXIT

read_job() {
    awk '
        /^  sql-syntax-parity:$/ { found = 1; next }
        found && /^  [[:alnum:]][[:alnum:]-]*:$/ { exit }
        found { print }
    ' "$1"
}

assert_push_parity_contract() {
    local block
    block="$(read_job "$1")"
    [[ "$block" == *"if: github.event_name == 'pull_request' || github.event_name == 'push'"* ]] &&
    [[ "$block" == *"github.event.pull_request.base.sha || (github.event_name == 'push' && github.event.before)"* ]] &&
    [[ "$block" == *' "$BASE_SHA" == "$GITHUB_SHA" '* ]] &&
    [[ "$block" == *'git fetch origin "$BASE_SHA" --depth=1'* ]]
}

expect_fail() {
    if assert_push_parity_contract "$scratch/workflow.yml"; then
        echo "::error::False green SQL syntax parity push contract: $1" >&2
        exit 1
    fi
}

assert_push_parity_contract "$source_file" || {
    echo "::error::Develop/main push SQL syntax parity cannot be skipped or self-compared." >&2
    exit 1
}
cp "$source_file" "$scratch/workflow.yml"
sed -i "s/if: github.event_name == 'pull_request' || github.event_name == 'push'/if: github.event_name == 'pull_request'/" "$scratch/workflow.yml"
expect_fail 'PR-only job is skipped on develop and main push'
cp "$source_file" "$scratch/workflow.yml"
sed -i "s/github.event.pull_request.base.sha || (github.event_name == 'push' \\&\\& github.event.before) || ''/github.event.pull_request.base.sha/" "$scratch/workflow.yml"
expect_fail 'push has no PR base SHA'
cp "$source_file" "$scratch/workflow.yml"
sed -i '/"$BASE_SHA" == "$GITHUB_SHA"/d' "$scratch/workflow.yml"
expect_fail 'self comparison is not a valid syntax-parity floor'
cp "$source_file" "$scratch/workflow.yml"
sed -i '/git fetch origin "\$BASE_SHA" --depth=1/d' "$scratch/workflow.yml"
expect_fail 'base compiler was not fetched'
echo 'Push syntax parity contract: 1 positive and 4 negative controls passed.'
