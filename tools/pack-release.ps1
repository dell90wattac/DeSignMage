<#
  Turns a published (and, for a release, already signed) folder into the files of an installed, self-updating copy, with Velopack (MIT):

    Setup.exe                     the installer the person runs once (per user, no administrator, Start menu and Desktop shortcut)
    <id>-<version>-win-full.nupkg  the whole program of that version
    <id>-<version>-win-delta.nupkg only what changed since the previous release (when the previous release is available)
    releases.win.json, RELEASES, assets.win.json   the list the installed copies read to find updates

  The folder is packed as it is: nothing in it is changed or signed here (vpk only adds its own Update.exe). The portable zip made by
  publish-portable.ps1 is a different thing and keeps working without any of this.

    .\publish-portable.ps1 -Output ..\build\publish
    .\tools\pack-release.ps1 -PublishDir ..\build\publish -OutputDir ..\build\velopack
    .\tools\pack-release.ps1 -PublishDir ..\build\publish -OutputDir ..\build\velopack -DownloadPrevious -Repo https://github.com/dell90wattac/DeSignMage -Token $env:GITHUB_TOKEN

  -DownloadPrevious first fetches the latest published release into -OutputDir, so vpk can build a delta package and keep the feed
  complete. With no earlier release (the very first one) there is nothing to fetch and it says so.
  Needs the dotnet tool vpk, pinned in .config\dotnet-tools.json (`dotnet tool restore`); its version must equal the Velopack package
  version in Directory.Packages.props, which this script checks.
#>
param(
    [Parameter(Mandatory)] [string] $PublishDir,
    [Parameter(Mandatory)] [string] $OutputDir,
    # Default: the Version in Directory.Build.props (or whatever the publish folder was built with, see publish-portable.ps1 -Version).
    [string] $Version,
    # Markdown file shown as the release notes inside the update; a short default is written when omitted.
    [string] $ReleaseNotesFile,
    [switch] $DownloadPrevious,
    [string] $Repo = 'https://github.com/dell90wattac/DeSignMage',
    [string] $Token,
    [string] $Channel = 'win',
    # Run this vpk.dll instead of the pinned tool (to try the script where the pinned one cannot run); the version check is skipped then.
    [string] $VpkDll
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot | Split-Path -Parent
$PublishDir = (Resolve-Path $PublishDir).Path
if (-not (Test-Path (Join-Path $PublishDir 'DeSignMage.exe'))) { throw "$PublishDir does not contain DeSignMage.exe" }

# One id for ever: the installed copy lives in %LocalAppData%\<id>, away from the data folder %LocalAppData%\DeSignMage (see Directory.Build.props).
$packId = (dotnet msbuild (Join-Path $root 'src\DeSignMage.App\DeSignMage.App.csproj') -getProperty:UpdatePackId).Trim()
if (-not $packId -or $packId -eq 'DeSignMage') { throw "UpdatePackId must be set in Directory.Build.props and must differ from DeSignMage (the data folder); it is '$packId'." }
if (-not $Version) { $Version = (dotnet msbuild (Join-Path $root 'src\DeSignMage.App\DeSignMage.App.csproj') -getProperty:Version).Trim() }

# The vpk that packs must be the Velopack version the program contains.
$manifest = Get-Content (Join-Path $root '.config\dotnet-tools.json') -Raw | ConvertFrom-Json
$vpkVersion = $manifest.tools.vpk.version
$libVersion = ([xml](Get-Content (Join-Path $root 'Directory.Packages.props') -Raw)).Project.ItemGroup.PackageVersion | Where-Object Include -eq 'Velopack' | ForEach-Object { $_.Version }
if (-not $VpkDll -and $vpkVersion -ne $libVersion) { throw "vpk $vpkVersion (.config/dotnet-tools.json) and Velopack $libVersion (Directory.Packages.props) must be the same version." }

New-Item -ItemType Directory -Force $OutputDir | Out-Null
$OutputDir = (Resolve-Path $OutputDir).Path
Push-Location $root
try {
    if ($VpkDll) { $vpk = @($VpkDll) } else {
        dotnet tool restore | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'dotnet tool restore failed' }
        $vpk = @('vpk')
    }

    if ($DownloadPrevious) {
        $api = ($Repo -replace '^https://github.com/', 'https://api.github.com/repos/') + '/releases/latest'
        $headers = @{ 'User-Agent' = 'DeSignMage-release' }
        if ($Token) { $headers['Authorization'] = "Bearer $Token" }
        $hasRelease = $true
        try { Invoke-RestMethod $api -Headers $headers | Out-Null }
        catch { if ($_.Exception.Response -and [int]$_.Exception.Response.StatusCode -eq 404) { $hasRelease = $false } else { throw } }
        if ($hasRelease) {
            $dl = @('download', 'github', '--repoUrl', $Repo, '--channel', $Channel, '--outputDir', $OutputDir)
            if ($Token) { $dl += @('--token', $Token) }
            dotnet @vpk @dl
            if ($LASTEXITCODE -ne 0) { throw 'vpk download github failed' }
        } else { Write-Host 'No earlier release: this is the first one, so there is no delta package.' }
    }

    if (-not $ReleaseNotesFile) {
        $ReleaseNotesFile = Join-Path $OutputDir "release-notes-$Version.md"
        [IO.File]::WriteAllText($ReleaseNotesFile, "# DeSign Mage $Version`n`nImprovements and fixes. The full list is on the release page.`n")
    }

    $pack = @(
        'pack', '--packId', $packId, '--packVersion', $Version, '--packDir', $PublishDir, '--mainExe', 'DeSignMage.exe',
        '--packTitle', 'DeSign Mage', '--packAuthors', 'dell90wattac',
        '--icon', (Join-Path $root 'src\DeSignMage.App\Assets\DeSignMage.ico'),
        '--releaseNotes', $ReleaseNotesFile, '--channel', $Channel, '--outputDir', $OutputDir,
        '--noPortable'                 # our own portable zip stays the portable build
    )
    # Never asks for an administrator, and everything stays in the person's own folders. (An older vpk, used only with -VpkDll, does not know the option.)
    if (-not $VpkDll) { $pack += @('--instLocation', 'PerUser') }
    dotnet @vpk @pack
    if ($LASTEXITCODE -ne 0) { throw 'vpk pack failed' }
}
finally { Pop-Location }

# Nothing in the folder may have been changed by packing (the signed files must reach the person exactly as signed): compare every file in the
# full package with the folder it was made from.
Add-Type -AssemblyName System.IO.Compression.FileSystem
$full = Get-ChildItem $OutputDir -Filter "$packId-$Version-*full.nupkg" | Select-Object -First 1
if (-not $full) { throw "The full package for $Version is missing in $OutputDir" }
$zip = [IO.Compression.ZipFile]::OpenRead($full.FullName)
try {
    $sha = [Security.Cryptography.SHA256]::Create()
    $bad = @()
    foreach ($file in Get-ChildItem $PublishDir -Recurse -File | Where-Object { $_.Extension -notin '.pdb', '.nupkg' -and $_.Name -ne 'createdump.exe' }) {   # what vpk leaves out by default
        $relative = $file.FullName.Substring($PublishDir.Length).TrimStart('\').Replace('\', '/')
        $entry = $zip.GetEntry("lib/app/$relative")
        if (-not $entry) { $bad += "$relative (missing from the package)"; continue }
        $stream = $entry.Open()
        try { $inPackage = [BitConverter]::ToString($sha.ComputeHash($stream)) } finally { $stream.Dispose() }
        if ($inPackage -ne [BitConverter]::ToString($sha.ComputeHash([IO.File]::ReadAllBytes($file.FullName)))) { $bad += "$relative (changed)" }
    }
    if ($bad) { throw "The package differs from the folder it was made from:`n  " + ($bad -join "`n  ") }
    Write-Host "The package contains every file of the folder unchanged."
}
finally { $zip.Dispose() }

Write-Host "`nFiles for the release ($OutputDir):"
Get-ChildItem $OutputDir -File | Where-Object { $_.Name -notlike 'release-notes-*' } | ForEach-Object { '  {0}  ({1:N1} MB)' -f $_.Name, ($_.Length / 1MB) }
