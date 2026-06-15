# Builds the legacy Windows-only ClaudeUsageTray.exe using the in-box .NET Framework
# C# compiler. No SDK or downloads required on Windows 10/11.
# (The cross-platform Avalonia version under /src is the recommended build.)
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$csc  = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) { throw "csc.exe not found at $csc" }

$src = Join-Path $here 'ClaudeUsageTray.cs'
$out = Join-Path $here 'ClaudeUsageTray.exe'

$refs = @(
  '/r:System.dll',
  '/r:System.Core.dll',
  '/r:System.Drawing.dll',
  '/r:System.Windows.Forms.dll',
  '/r:System.Web.Extensions.dll',
  '/r:System.Net.Http.dll'
)

& $csc /nologo /target:winexe /optimize+ /platform:anycpu `
  ("/out:" + $out) @refs $src

if ($LASTEXITCODE -ne 0) { throw "Compilation failed ($LASTEXITCODE)" }
Write-Output ("Built: " + $out)
