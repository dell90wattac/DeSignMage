<#
  Publishes DeSign Mage as a portable folder (and optionally a zip): 64-bit Windows, without the native symbol files.
  Self-contained by default, so it runs on a PC with no .NET installed.

  ReadyToRun precompiles only the program's own assemblies, so every third-party file stays byte-identical to its NuGet package (the
  release signing and Smart App Control rely on that). -PrecompileAll also precompiles Avalonia, SkiaSharp, ...: about 0.7 s faster to
  start, for personal builds that are not signed.

    .\publish-portable.ps1 -Output ..\build\DeSignMage-portable -Zip
    .\publish-portable.ps1 -Output ..\build\DeSignMage-portable -PrecompileAll
    .\publish-portable.ps1 -Output C:\temp\dsm-fdd -FrameworkDependent     # needs the .NET 10 Desktop runtime on the target PC

  Feedback reports go to the public relay (tools\feedback-relay); no secret is built into the program.
  Measure startup with:  DeSignMage.exe --first-frame-exit out.txt
#>
param(
    [Parameter(Mandatory)] [string] $Output,
    [switch] $FrameworkDependent,
    [switch] $PrecompileAll,
    [switch] $Zip,
    # A version other than the one in Directory.Build.props, for trying an update on this computer (tools\try-update-locally.ps1). A release uses the real one.
    [string] $Version
)
$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'src\DeSignMage.App'
$selfContained = if ($FrameworkDependent) { 'false' } else { 'true' }
if (Test-Path $Output) { Remove-Item $Output -Recurse -Force }
$all = if ($PrecompileAll) { 'true' } else { 'false' }
$versionArg = if ($Version) { "-p:Version=$Version" } else { $null }
dotnet publish $project -c Release -r win-x64 --self-contained $selfContained -p:PrecompileAllAssemblies=$all $versionArg -o $Output
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }
$mb = (Get-ChildItem $Output -Recurse -File | Measure-Object Length -Sum).Sum / 1MB
"Published to $Output ({0:N1} MB)" -f $mb
if ($Zip) {
    $zipPath = "$($Output.TrimEnd('\')).zip"
    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    Compress-Archive -Path (Join-Path $Output '*') -DestinationPath $zipPath -CompressionLevel Optimal
    "Zip: $zipPath ({0:N1} MB)" -f ((Get-Item $zipPath).Length / 1MB)
}
