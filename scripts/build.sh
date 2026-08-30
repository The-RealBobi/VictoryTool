#!/usr/bin/env bash
set -euo pipefail

usage() {
    echo "Usage: $0 [RID] [CONFIGURATION] [OUTPUT_ROOT]" >&2
    echo "Example: $0 osx-arm64 Release" >&2
    exit 2
}

[[ $# -le 3 ]] || usage

script_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
project="$script_root/src/VictoryTool.Desktop/VictoryTool.Desktop.csproj"
version="$(sed -nE 's/.*<VersionPrefix>([^<]+)<\/VersionPrefix>.*/\1/p' "$script_root/Directory.Build.props" | head -n 1)"
rid="${1:-${RID:-}}"
configuration="${2:-${CONFIGURATION:-Release}}"
output_root="${3:-${OUTPUT_ROOT:-$script_root/dist}}"

[[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || {
    echo "Invalid VersionPrefix in Directory.Build.props: '$version'" >&2
    exit 2
}

publish_root="$output_root/v$version/${rid:-framework-dependent}"
archive="$output_root/VictoryTool-$version-${rid:-framework-dependent}.zip"
mkdir -p "$publish_root"

dotnet restore "$project"
publish_args=("$project" -c "$configuration" --no-restore -o "$publish_root")
if [[ -n "$rid" ]]; then
    publish_args+=(-r "$rid" --self-contained true)
fi
dotnet publish "${publish_args[@]}"

rm -f "$archive"
(cd "$publish_root" && zip -qr "$archive" .)
echo "Created $archive"
