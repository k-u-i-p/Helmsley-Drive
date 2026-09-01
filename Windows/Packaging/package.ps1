<#
.SYNOPSIS
    Builds the installer a tester downloads: publish, then compile, optionally signing both.

.DESCRIPTION
    One command from a clean checkout to dist\HelmsleyDrive-<version>-<arch>.exe. Run it from
    anywhere; every path below is resolved from the script's own location.

    Signing is optional and off by default, because it is the one step that needs something the
    repository cannot carry. Unsigned, the installer works — every tester meets SmartScreen's
    "Windows protected your PC" once and has to choose More info, then Run anyway. That is a
    tolerable ask of a handful of colleagues and an untenable one of anybody else.

.PARAMETER Runtime
    Which architectures to build. win-x64 covers Intel and AMD laptops and runs on ARM under
    emulation; win-arm64 is the native build for Snapdragon machines.

.PARAMETER Version
    Stamped into the executable and onto the installer's uninstall entry, and used in the output
    filename. Give every build handed to anybody a distinct one — a bug report against "0.1.0"
    when three different 0.1.0s exist is a bug report about nothing.

.PARAMETER CertThumbprint
    A code-signing certificate in the current user's store. Signs the published executable before
    it is packaged and the installer after it is built; both, because SmartScreen judges them
    separately.

.PARAMETER SkipInstaller
    Publish only. Useful for checking what the publish produced without Inno Setup installed.

.EXAMPLE
    .\package.ps1 -Version 0.2.0 -Runtime win-x64, win-arm64
#>
[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string[]] $Runtime = @('win-x64'),
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string] $Version = '0.1.0',
    [string] $CertThumbprint,
    [switch] $SkipInstaller
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$packaging = $PSScriptRoot
$windows = Split-Path $packaging -Parent
$repo = Split-Path $windows -Parent
$dist = Join-Path $repo 'dist'

# Looked up once, and only when they are going to be used: a publish-only run has no business
# failing because a machine that will never compile an installer has no Inno Setup on it.
function Find-Tool([string] $name, [string[]] $candidates, [string] $remedy) {
    $onPath = Get-Command $name -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }
    foreach ($candidate in $candidates) {
        # Newest first, so a machine carrying several SDKs signs with the current one.
        $found = Get-Item $candidate -ErrorAction SilentlyContinue | Sort-Object FullName -Descending
        if ($found) { return $found[0].FullName }
    }
    throw "$name was not found. $remedy"
}

if ($CertThumbprint) {
    $signtool = Find-Tool 'signtool.exe' @(
        "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe",
        "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\arm64\signtool.exe"
    ) 'It ships with the Windows SDK.'
}

if (-not $SkipInstaller) {
    # Wildcarded across major versions and across the three places Inno Setup's own installer puts
    # itself, per-user included — which is how it goes on a machine nobody has administrator on.
    $iscc = Find-Tool 'ISCC.exe' @(
        "${env:ProgramFiles(x86)}\Inno Setup *\ISCC.exe",
        "$env:ProgramFiles\Inno Setup *\ISCC.exe",
        "$env:LOCALAPPDATA\Programs\Inno Setup *\ISCC.exe"
    ) 'Install Inno Setup 6.3 or later from https://jrsoftware.org/isdl.php, or pass -SkipInstaller.'
}

# Timestamped, so the signature outlives the certificate rather than expiring with it.
function Invoke-Sign([string] $path) {
    if (-not $CertThumbprint) { return }
    Write-Host "signing $(Split-Path $path -Leaf)"
    & $signtool sign /sha1 $CertThumbprint /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 $path
    if ($LASTEXITCODE -ne 0) { throw "signtool failed on $path" }
}

New-Item -ItemType Directory -Force -Path $dist | Out-Null

foreach ($rid in $Runtime) {
    $arch = $rid -replace '^win-', ''
    Write-Host "`n=== $rid $Version ===" -ForegroundColor Cyan

    # The profile carries the shape (self-contained, single-file, no trimming); only the version
    # is passed here, so what a tester runs and what a developer publishes cannot drift apart.
    & dotnet publish (Join-Path $windows 'App') -p:PublishProfile=$rid -p:Version=$Version
    if ($LASTEXITCODE -ne 0) { throw "publish failed for $rid" }

    $publishDir = Join-Path $windows "App\bin\publish\$rid"
    Invoke-Sign (Join-Path $publishDir 'HelmsleyDrive.App.exe')

    if ($SkipInstaller) {
        Write-Host "published to $publishDir"
        continue
    }

    & $iscc "/DAppVersion=$Version" "/DArch=$arch" "/DPublishDir=$publishDir" `
        (Join-Path $packaging 'HelmsleyDrive.iss')
    if ($LASTEXITCODE -ne 0) { throw "ISCC failed for $rid" }

    $installer = Join-Path $dist "HelmsleyDrive-$Version-$arch.exe"
    Invoke-Sign $installer
    Write-Host "built $installer" -ForegroundColor Green
}
