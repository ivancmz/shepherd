<#
.SYNOPSIS
    Publishes the NativeAOT `shepherd` executable.

.DESCRIPTION
    win-x64 is built here. linux-x64 must be built on Linux (NativeAOT does not cross-compile
    between OSes): run `scripts/publish.sh` inside WSL.

    Workaround: some Visual Studio installs lack the Windows SDK registry key (KitsRoot10), so
    vcvarsall.bat cannot add the SDK libraries and linking fails with LNK1104 'ucrt.lib'. When
    that key is missing, this script points LIB at the newest installed Windows SDK and MSVC
    libraries for this process only. It also puts vswhere.exe on PATH, which the ILCompiler
    targets expect.

.PARAMETER Output
    Output directory (default: publish/win-x64).

.EXAMPLE
    pwsh scripts/publish.ps1
#>
param(
    [string]$Output = (Join-Path $PSScriptRoot "..\publish\win-x64")
)
$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..")

$installer = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer"
if (Test-Path "$installer\vswhere.exe") { $env:PATH += ";$installer" }

$kitsRoot = (Get-ItemProperty "HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows Kits\Installed Roots" -ErrorAction SilentlyContinue).KitsRoot10
if (-not $kitsRoot) {
    $kitsLib = "${env:ProgramFiles(x86)}\Windows Kits\10\Lib"
    $sdk = Get-ChildItem $kitsLib -Directory -ErrorAction SilentlyContinue |
        Where-Object { Test-Path (Join-Path $_.FullName "ucrt\x64\ucrt.lib") } |
        Sort-Object { [version]$_.Name } | Select-Object -Last 1
    $vsPath = & "$installer\vswhere.exe" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    $msvc = Get-ChildItem (Join-Path $vsPath "VC\Tools\MSVC") -Directory | Sort-Object { [version]$_.Name } | Select-Object -Last 1
    if (-not $sdk -or -not $msvc) { throw "Windows SDK or MSVC tools not found; install the 'Desktop development with C++' workload." }
    Write-Host "KitsRoot10 missing: using Windows SDK $($sdk.Name) and MSVC $($msvc.Name) libraries directly."
    $env:LIB = @(
        (Join-Path $msvc.FullName "lib\x64"),
        (Join-Path $sdk.FullName "ucrt\x64"),
        (Join-Path $sdk.FullName "um\x64")
    ) -join ";"
}

dotnet publish (Join-Path $root "src\Shepherd.Cli\Shepherd.Cli.csproj") -c Release -r win-x64 -o $Output
if ($LASTEXITCODE -ne 0) { throw "publish failed" }
Get-ChildItem $Output | Where-Object Extension -notin ".pdb" | Format-Table Name, @{n = "KB"; e = { [math]::Round($_.Length / 1KB) } }
