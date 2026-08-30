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

if [[ -z "$rid" ]]; then
    case "$(uname -s)" in
        Darwin) platform="osx" ;;
        MINGW*|MSYS*|CYGWIN*) platform="win" ;;
        Linux) platform="linux" ;;
        *) echo "Could not infer a runtime identifier; pass one explicitly." >&2; exit 2 ;;
    esac
    case "$(uname -m)" in
        arm64|aarch64) architecture="arm64" ;;
        x86_64|amd64) architecture="x64" ;;
        *) echo "Could not infer the CPU architecture; pass a RID explicitly." >&2; exit 2 ;;
    esac
    rid="$platform-$architecture"
fi

if [[ "$output_root" != /* ]]; then
    output_root="$script_root/$output_root"
fi

[[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || {
    echo "Invalid VersionPrefix in Directory.Build.props: '$version'" >&2
    exit 2
}

publish_root="$output_root/v$version/$rid"
archive="$output_root/VictoryTool-$version-$rid.zip"
rm -rf "$publish_root"
mkdir -p "$publish_root"

dotnet restore "$project" --runtime "$rid"
dotnet publish "$project" \
    -c "$configuration" \
    --no-restore \
    -r "$rid" \
    --self-contained true \
    -o "$publish_root" \
    -p:PublishSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:EnableCompressionInSingleFile=true \
    -p:IncludeAllContentForSelfExtract=true \
    -p:DebugType=None \
    -p:DebugSymbols=false

# Native dependency packages can add symbol files after the single-file publish.
# They are not required by the application and would break the one-file output.
find "$publish_root" -type f \( -name '*.pdb' -o -name '*.dbg' \) -delete
published_file_count="$(find "$publish_root" -type f -print | wc -l | tr -d ' ')"
if [[ "$published_file_count" != "1" ]]; then
    echo "Single-file publish produced $published_file_count files in $publish_root:" >&2
    find "$publish_root" -type f -print >&2
    exit 1
fi
published_file="$(find "$publish_root" -type f -print -quit)"

rm -f "$archive"
(cd "$publish_root" && zip -q "$archive" "$(basename "$published_file")")
echo "Created $archive"
