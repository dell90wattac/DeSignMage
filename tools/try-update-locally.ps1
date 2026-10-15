<#
  Tries the whole update path on this computer, with no network and nothing published: a "feed" folder takes the place of the GitHub
  release page.

    1. publishes and packs an older version (default 0.0.1) into the feed folder, and runs its Setup.exe silently (installs for this user only)
    2. publishes and packs a newer version (default 0.0.2) into the same feed folder (so it also makes the delta package)
    3. starts the installed program with DESIGNMAGE_UPDATE_SOURCE pointing at the feed: a fresh start with a newer version waiting shows the
       "Update required" window; the button installs it and the program comes back as the new version

  Look at Help > About afterwards (it shows the version). Remove the test install with:
    & "$env:LOCALAPPDATA\DeSignMageApp\Update.exe" --uninstall
  Your settings and designs (%LOCALAPPDATA%\DeSignMage) are not touched by the install, the update or the uninstall.

    .\tools\try-update-locally.ps1
    .\tools\try-update-locally.ps1 -Work C:\temp\dsm-update-demo -Old 0.0.1 -New 0.0.2
#>
param(
    [string] $Work = (Join-Path $env:TEMP 'dsm-update-demo'),
    [string] $Old = '0.0.1',
    [string] $New = '0.0.2',
    # Passed on to pack-release.ps1 (only for a computer where the pinned vpk cannot run).
    [string] $VpkDll
)
$ErrorActionPreference = 'Stop'
$app = Split-Path $PSScriptRoot -Parent
$feed = Join-Path $Work 'feed'
New-Item -ItemType Directory -Force $Work, $feed | Out-Null

function Build-Version([string] $version) {
    $publish = Join-Path $Work "publish-$version"
    & (Join-Path $app 'publish-portable.ps1') -Output $publish -Version $version
    $extra = if ($VpkDll) { @{ VpkDll = $VpkDll } } else { @{} }
    & (Join-Path $app 'tools\pack-release.ps1') -PublishDir $publish -OutputDir $feed -Version $version @extra
}

Build-Version $Old
$setup = Get-ChildItem $feed -Filter '*Setup.exe' | Select-Object -First 1
if (-not $setup) { throw "No Setup.exe in $feed" }
Write-Host "`nInstalling $Old with $($setup.Name) (silent, this user only)..." -ForegroundColor Cyan
Start-Process $setup.FullName -ArgumentList '--silent' -Wait

Build-Version $New
$packId = (dotnet msbuild (Join-Path $app 'src\DeSignMage.App\DeSignMage.App.csproj') -getProperty:UpdatePackId).Trim()
$exe = Join-Path $env:LOCALAPPDATA "$packId\current\DeSignMage.exe"
if (-not (Test-Path $exe)) { throw "The installed program is not at $exe" }
Write-Host "`nStarting the installed ${Old} with the feed at ${feed}; expect the Update required window for ${New}." -ForegroundColor Cyan
$env:DESIGNMAGE_UPDATE_SOURCE = $feed
Start-Process $exe
