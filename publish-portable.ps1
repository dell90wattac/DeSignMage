<#
  Publishes DeSign Mage as a portable folder (and optionally a zip): 64-bit Windows, ReadyToRun precompiled (fast startup),
  without the native symbol files. Self-contained by default, so it runs on a PC with no .NET installed.

    .\publish-portable.ps1 -Output ..\build\DeSignMage-portable -Zip
    .\publish-portable.ps1 -Output C:\temp\dsm-fdd -FrameworkDependent     # needs the .NET 10 Desktop runtime on the target PC

  The feedback webhook (secrets\feedback-webhook.txt) is built in when that file exists (it is never committed).
  Palette files in local-assets\palettes are copied next to the exe when that folder exists.
  Measure startup with:  DeSignMage.exe --first-frame-exit out.txt
#>
param(
    [Parameter(Mandatory)] [string] $Output,
    [switch] $FrameworkDependent,
    [switch] $Zip
)
$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'src\DeSignMage.App'
$selfContained = if ($FrameworkDependent) { 'false' } else { 'true' }
if (Test-Path $Output) { Remove-Item $Output -Recurse -Force }
dotnet publish $project -c Release -r win-x64 --self-contained $selfContained -o $Output
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }
$mb = (Get-ChildItem $Output -Recurse -File | Measure-Object Length -Sum).Sum / 1MB
"Published to $Output ({0:N1} MB)" -f $mb
if ($Zip) {
    $zipPath = "$($Output.TrimEnd('\')).zip"
    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    Compress-Archive -Path (Join-Path $Output '*') -DestinationPath $zipPath -CompressionLevel Optimal
    "Zip: $zipPath ({0:N1} MB)" -f ((Get-Item $zipPath).Length / 1MB)
}
