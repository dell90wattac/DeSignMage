<#
  Fails when a published folder contains something that looks like a delivery secret: a webhook address (plain or as UTF-16 text inside a
  .NET assembly) or the name of the old embedded webhook resource. Run by the release workflow on every build before it is uploaded.

    .\tools\check-no-secrets.ps1 -Folder ..\build\DeSignMage-portable
#>
param([Parameter(Mandatory)] [string] $Folder)
$ErrorActionPreference = 'Stop'

# "webhooks/<long number>", with an optional zero byte between characters so UTF-16 text matches too.
$sep = '\x00?'
$address = (('webhooks/'.ToCharArray() | ForEach-Object { [regex]::Escape([string]$_) }) -join $sep) + "($sep[0-9]){15,}"
$resource = (('feedback-webhook'.ToCharArray() | ForEach-Object { [regex]::Escape([string]$_) }) -join $sep)
$patterns = @{ 'a webhook address' = [regex]::new($address); 'the old embedded webhook resource' = [regex]::new($resource) }

# Our own files only: third-party libraries are large and cannot contain our secrets. Text files are checked too.
$files = Get-ChildItem $Folder -Recurse -File | Where-Object { $_.Name -like 'DeSignMage*' -or $_.Extension -in '.json', '.txt', '.xml', '.config' }
$failed = $false
foreach ($file in $files) {
    $text = [Text.Encoding]::GetEncoding(28591).GetString([IO.File]::ReadAllBytes($file.FullName))
    foreach ($name in $patterns.Keys) {
        if ($patterns[$name].IsMatch($text)) { Write-Error "$($file.Name) contains $name"; $failed = $true }
    }
}
if ($failed) { exit 1 }
"Checked $(@($files).Count) files: no webhook address or secret resource found."
