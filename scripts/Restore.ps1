#requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string]$GameDirectory)
. (Join-Path $PSScriptRoot 'Common.ps1')
Assert-GameStopped
$paths = Get-GamePaths $GameDirectory
$receipt = Get-InstalledReceipt $paths
$repo = Split-Path $PSScriptRoot -Parent
$backup = Join-Path $repo ('build/backups/' + $receipt.inputSHA256 + '/Assembly-CSharp.dll')
Assert-Hash $backup $receipt.inputSHA256
# Fixed destination files only. Hash checks above refuse changed or unrelated installs.
Copy-Item -LiteralPath $backup -Destination $paths.Assembly -Force
Assert-Hash $paths.Assembly $receipt.inputSHA256
Remove-Item -LiteralPath $paths.Helper
Remove-Item -LiteralPath (Join-Path $paths.Runtime 'installed-report.json')
Set-Content -LiteralPath (Join-Path $paths.Runtime 'STOP') -Value 'Restored vanilla assembly.' -Encoding utf8NoBOM
Write-Output 'Vanilla assembly restored and matching bridge helper removed. Saves and local telemetry were retained.'
