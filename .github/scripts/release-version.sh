#!/usr/bin/env bash
# Source this helper to read product versions from the shared MSBuild file.
release_repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"

read_release_property() {
    local name="$1" value
    value="$(sed -nE "s@^[[:space:]]*<${name}([^>]*)>([^<]+)</${name}>[[:space:]]*\$@\\2@p" \
        "$release_repo_root/backend/Directory.Build.props")" || return 1
    if [[ ! "$value" =~ ^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$ ]]; then
        echo "Invalid or missing release property: $name" >&2
        return 1
    fi
    printf '%s\n' "$value"
}
