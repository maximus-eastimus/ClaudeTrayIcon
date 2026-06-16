# Cross-platform build helper (Windows). Requires the .NET 10 SDK: https://dotnet.microsoft.com/download
# Produces a self-contained single-file exe (no runtime install needed by end users).
param([string]$Rid = "win-x64")
$ErrorActionPreference = "Stop"
$proj = Join-Path $PSScriptRoot "src/ClaudeTrayIcon/ClaudeTrayIcon.csproj"
dotnet publish $proj -c Release -r $Rid --self-contained `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -o (Join-Path $PSScriptRoot "publish/$Rid")
Write-Output "Built into publish/$Rid"
