#!/usr/bin/env bash
set -euo pipefail

usage() {
    echo "Usage: $0 VERSION PRIVATE_REPOSITORY PUBLIC_REPOSITORY" >&2
    exit 2
}

[[ $# -eq 3 ]] || usage

version="$1"
private_root="$2"
public_root="$3"

if [[ ! "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
    echo "Invalid version '$version'; expected X.Y.Z." >&2
    exit 2
fi

for repository_root in "$private_root" "$public_root"; do
    props="$repository_root/Directory.Build.props"
    [[ -d "$repository_root/.git" || -f "$repository_root/.git" ]] || {
        echo "Not a git repository: $repository_root" >&2
        exit 2
    }
    [[ -f "$props" ]] || {
        echo "Missing Directory.Build.props: $props" >&2
        exit 2
    }
    grep -Eq '<VersionPrefix>[0-9]+\.[0-9]+\.[0-9]+</VersionPrefix>' "$props" || {
        echo "Missing or invalid VersionPrefix in $props" >&2
        exit 2
    }
done

for repository_root in "$private_root" "$public_root"; do
    props="$repository_root/Directory.Build.props"
    temporary="$props.tmp.$$"
    awk -v version="$version" '
        /<VersionPrefix>[0-9]+\.[0-9]+\.[0-9]+<\/VersionPrefix>/ {
            sub(/<VersionPrefix>[0-9]+\.[0-9]+\.[0-9]+<\/VersionPrefix>/,
                "<VersionPrefix>" version "</VersionPrefix>")
        }
        { print }
    ' "$props" > "$temporary"
    mv "$temporary" "$props"
done

echo "Synchronized VersionPrefix=$version"
