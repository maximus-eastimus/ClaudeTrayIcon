#!/usr/bin/env bash
# Build helper (macOS/Linux). Requires the .NET 10 SDK:
#   https://dotnet.microsoft.com/download
# Usage: ./build.sh [rid]
#   Linux : linux-x64   (default)
set -euo pipefail
RID="${1:-linux-x64}"
DIR="$(cd "$(dirname "$0")" && pwd)"
case "$RID" in
  win*) TFM="net10.0-windows" ;;
  *)    TFM="net10.0" ;;
esac
dotnet publish "$DIR/src/ClaudeTrayIcon/ClaudeTrayIcon.csproj" \
  -f "$TFM" -c Release -r "$RID" --self-contained \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -o "$DIR/publish/$RID"
echo "Built into publish/$RID"
