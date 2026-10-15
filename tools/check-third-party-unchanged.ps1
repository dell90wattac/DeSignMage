<#
  Fails when a published file that is not ours differs from the copy in the restored NuGet packages (which includes the .NET runtime pack
  of a self-contained publish). Our own files are the ones named DeSignMage*. Run by the release workflow before signing, and again
  on the signed result, so third-party files provably leave the build exactly as their publishers shipped them.

    .\tools\check-third-party-unchanged.ps1 -Folder ..\build\DeSignMage-portable
    .\tools\check-third-party-unchanged.ps1 -Folder ... -PackagesFolder D:\nuget-cache
#>
param(
    [Parameter(Mandatory)] [string] $Folder,
    [string] $PackagesFolder = $(if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget\packages' })
)
$ErrorActionPreference = 'Stop'
if (-not (Test-Path $PackagesFolder)) { throw "Package folder not found: $PackagesFolder" }
$root = (Resolve-Path $Folder).Path.TrimEnd('\')

$theirs = Get-ChildItem $root -Recurse -File | Where-Object { $_.Name -notlike 'DeSignMage*' }
if (-not $theirs) { throw "No third-party files found in ${root}: wrong folder?" }

# Which file sizes exist, so only candidates are hashed in the (large) package cache.
$sizes = @{}
foreach ($file in $theirs) { $sizes[$file.Length] = $true }
$known = @{}
foreach ($file in Get-ChildItem $PackagesFolder -Recurse -File -ErrorAction SilentlyContinue) {
    if ($sizes.ContainsKey($file.Length)) { $known[(Get-FileHash $file.FullName -Algorithm SHA256).Hash] = $file.FullName }
}

$changed = @()
foreach ($file in $theirs) {
    if (-not $known.ContainsKey((Get-FileHash $file.FullName -Algorithm SHA256).Hash)) { $changed += $file.FullName.Substring($root.Length + 1) }
}

# Release zips carry no color palette files.
$palettes = @(Get-ChildItem $root -Recurse -File | Where-Object { $_.Extension -in '.pal', '.clr' } | ForEach-Object { $_.FullName.Substring($root.Length + 1) })

if ($changed.Count -or $palettes.Count) {
    if ($changed.Count) { Write-Host "::error::Not identical to any restored package file: $($changed -join ', ')" }
    if ($palettes.Count) { Write-Host "::error::Palette files in the build: $($palettes -join ', ')" }
    exit 1
}
"All $(@($theirs).Count) third-party files are identical to their package copies; no palette files."
