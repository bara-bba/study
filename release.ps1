<#
.SYNOPSIS
  Builds the release study.exe: one self-contained file, no .NET install needed on the target PC.

.EXAMPLE
  .\release.ps1                 # version from src\study\study.csproj
  .\release.ps1 -Version 0.2.0  # explicit version (CI passes the tag)

  Output: dist\study-<version>.exe, then a headless --probe of that exe (sounds + libplctag load).
#>
[CmdletBinding()]
param(
    [string] $Version
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$project = Join-Path $root 'src\study\study.csproj'

if (-not $Version) {
    $Version = ([xml](Get-Content $project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
}
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Version '$Version' is not MAJOR.MINOR.PATCH" }

$publish = Join-Path $root 'dist\publish'
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }

# Self-contained single file: the .NET runtime, libplctag's native DLL and the built-in MP3s are
# all inside study.exe and extracted on first start, so AppContext.BaseDirectory still finds Sounds\.
dotnet publish $project -c Release -r win-x64 --self-contained true -o $publish `
    -p:Version=$Version `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:IncludeAllContentForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=None
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

$exe = Join-Path $root "dist\study-$Version.exe"
Copy-Item (Join-Path $publish 'study.exe') $exe -Force
Remove-Item $publish -Recurse -Force

# Smoke test the packaged exe itself, not the build output.
$probe = Join-Path $root 'dist\probe'
$p = Start-Process -FilePath $exe -ArgumentList "--probe `"$probe`"" -PassThru -Wait
Get-Content (Join-Path $probe 'probe.txt')
Remove-Item $probe -Recurse -Force
if ($p.ExitCode -ne 0) { throw "probe failed (exit $($p.ExitCode))" }

'{0}  {1:N1} MB' -f $exe, ((Get-Item $exe).Length / 1MB)
