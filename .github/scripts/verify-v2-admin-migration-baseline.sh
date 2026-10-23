#!/usr/bin/env bash
# Fail if a v3 branch silently rewrites any migration shipped in v2.0.6.
# An upgrade test built only from current source otherwise proves little:
# its historical migration classes might already have been changed.
set -euo pipefail

baseline_tag="v2.0.6"
baseline_commit="e6f289ac183a89a649acaa46c6845c763a9b6c68"
git fetch --quiet --no-tags --depth=1 origin "refs/tags/${baseline_tag}:refs/tags/${baseline_tag}"
actual_commit="$(git rev-parse "refs/tags/${baseline_tag}^{commit}")"
if [[ "$actual_commit" != "$baseline_commit" ]]; then
    echo "ERROR: ${baseline_tag} moved from immutable release ${baseline_commit} to ${actual_commit}." >&2
    exit 1
fi

count=0
sqlite_count=0
postgres_count=0
while IFS= read -r path; do
    if [[ ! "$path" =~ /[0-9]{14}_[^/]+\.cs$ ]]; then
        continue
    fi
    if ! current_blob="$(git rev-parse --verify "HEAD:${path}" 2>/dev/null)"; then
        echo "ERROR: published v2 migration was removed: ${path}" >&2
        exit 1
    fi
    original_blob="$(git rev-parse "refs/tags/${baseline_tag}:${path}")"
    if [[ "$current_blob" != "$original_blob" ]]; then
        echo "ERROR: published v2 migration was modified in-place: ${path}" >&2
        echo "Add a new v3 migration; never silently rewrite a historical release migration." >&2
        exit 1
    fi
    count=$((count + 1))
    if [[ "$path" == *"/HsSqlAgent.SqliteMigrations/"* ]]; then
        sqlite_count=$((sqlite_count + 1))
    else
        postgres_count=$((postgres_count + 1))
    fi
done < <(git ls-tree -r --name-only "refs/tags/${baseline_tag}" -- \
    backend/src/Modules/HsSqlAgent.SqliteMigrations/Admin \
    backend/src/Modules/HsSqlAgent.PostgresMigrations/Admin)

if (( count < 2 || sqlite_count == 0 || postgres_count == 0 )); then
    echo "ERROR: v2.0.6 Admin Store migration baseline was incomplete." >&2
    exit 1
fi

# Each provider must include the actual latest published v2.0.6 migration,
# not just the September durable-approval schema.
for latest in \
    backend/src/Modules/HsSqlAgent.SqliteMigrations/Admin/20261018150000_AddManagementApiKeys.cs \
    backend/src/Modules/HsSqlAgent.PostgresMigrations/Admin/20261018150000_AddManagementApiKeysPostgres.cs; do
    git cat-file -e "HEAD:${latest}" || exit 1
done
echo "Pinned v2.0.6 Admin Store migration ABI preserved: ${count} historical files (SQLite=${sqlite_count}, PostgreSQL=${postgres_count}); baseline ${baseline_commit}."
