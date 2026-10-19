<#
  Lists which files of a published build differ from a GitHub release (by default the latest one). Informational: it never fails a build.
  Every changed file is a new, unknown file to Windows Smart App Control, so this shows what a release really changes
  (RELEASING.md, "Keep unchanged files identical"). Normally only the program itself changes: DeSignMage.exe, DeSignMage.dll,
  DeSignMage.deps.json and DeSignMage.pdb, plus each library whose code changed.

    .\tools\compare-with-release.ps1 -Folder ..\build\publish
    .\tools\compare-with-release.ps1 -Folder ..\build\publish -Tag v0.4.3
  Needs the GitHub CLI (gh), signed in, or GH_TOKEN set (the release workflow passes its token).
#>
param(
    [Parameter(Mandatory)] [string] $Folder,
    [string] $Tag,
    [string] $Repo = 'dell90wattac/DeSignMage'
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path $Folder).Path.TrimEnd('\')
if (-not $Tag) { $Tag = (gh release view --repo $Repo --json tagName --jq .tagName) }
if (-not $Tag) { "No earlier release found; nothing to compare."; return }

$work = Join-Path ([System.IO.Path]::GetTempPath()) ("dsm-compare-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory $work | Out-Null
try {
    gh release download $Tag --repo $Repo --pattern 'DeSignMage-*-win-x64.zip' --dir $work
    if ($LASTEXITCODE -ne 0) { "Could not download the portable zip of $Tag; nothing compared."; return }
    $zip = Get-ChildItem $work -Filter *.zip | Select-Object -First 1
    $old = Join-Path $work 'old'
    Expand-Archive $zip.FullName -DestinationPath $old

    $changed = @(); $added = @(); $removed = @()
    $newFiles = Get-ChildItem $root -Recurse -File
    foreach ($file in $newFiles) {
        $rel = $file.FullName.Substring($root.Length + 1)
        $before = Join-Path $old $rel
        if (-not (Test-Path $before)) { $added += $rel }
        elseif ((Get-FileHash $file.FullName).Hash -ne (Get-FileHash $before).Hash) { $changed += $rel }
    }
    foreach ($file in Get-ChildItem $old -Recurse -File) {
        $rel = $file.FullName.Substring($old.Length + 1)
        if (-not (Test-Path (Join-Path $root $rel))) { $removed += $rel }
    }

    $lines = @("Compared with ${Tag}: $($newFiles.Count) files, $($changed.Count) changed, $($added.Count) new, $($removed.Count) gone.")
    $lines += $changed | ForEach-Object { "  changed  $_" }
    $lines += $added | ForEach-Object { "  new      $_" }
    $lines += $removed | ForEach-Object { "  gone     $_" }
    $lines
    if ($env:GITHUB_STEP_SUMMARY) { ("### Files changed since $Tag", '```', ($lines -join "`n"), '```') | Out-File $env:GITHUB_STEP_SUMMARY -Append -Encoding utf8 }
}
finally {
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
