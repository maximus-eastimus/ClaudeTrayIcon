#!/usr/bin/env bash
# Cross-platform build helper (macOS/Linux). Requires the .NET 10 SDK:
#   https://dotnet.microsoft.com/download
# Usage: ./build.sh [rid]
#   macOS Apple Silicon : osx-arm64   (default)
#   macOS Intel         : osx-x64
#   Linux               : linux-x64
set -euo pipefail
RID="${1:-osx-arm64}"
DIR="$(cd "$(dirname "$0")" && pwd)"
dotnet publish "$DIR/src/ClaudeTrayIcon/ClaudeTrayIcon.csproj" \
  -c Release -r "$RID" --self-contained \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -o "$DIR/publish/$RID"
echo "Built into publish/$RID"
