#requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string]$GameDirectory)
. (Join-Path $PSScriptRoot 'Common.ps1')
Assert-GameStopped
$paths = Get-GamePaths $GameDirectory
$repo = Split-Path $PSScriptRoot -Parent
$build = Join-Path $repo 'build'
$report = Get-Content -LiteralPath (Join-Path $build 'patch-report.json') -Raw | ConvertFrom-Json
if ($report.schema -ne 1 -or $report.product -ne 'hollow-knight-jev-source' -or !$report.originalBodiesPreserved -or
    $report.inputSHA256 -ne 'E9048EF6A633970F735E01EC166D3959F610EAEA7A88D827D48D67B1E5FB87BD') { throw 'Unsupported build report.' }
Assert-Hash (Join-Path $build 'Assembly-CSharp.dll') $report.patchedSHA256
Assert-Hash (Join-Path $build 'HollowKnightTrial.dll') $report.helperSHA256
if (Test-Path -LiteralPath (Join-Path $paths.Runtime 'installed-report.json')) { throw 'An install receipt already exists. Restore that installation before installing again.' }
Assert-Hash $paths.Assembly $report.inputSHA256
if (Test-Path -LiteralPath $paths.Helper) { throw 'An existing bridge helper was found. Refusing to overwrite it.' }
$backupDirectory = Join-Path $build ('backups/' + $report.inputSHA256)
New-Item -ItemType Directory -Path $backupDirectory -Force | Out-Null
$backup = Join-Path $backupDirectory 'Assembly-CSharp.dll'
if (!(Test-Path -LiteralPath $backup)) { Copy-Item -LiteralPath $paths.Assembly -Destination $backup }
Assert-Hash $backup $report.inputSHA256
New-Item -ItemType Directory -Path $paths.Runtime -Force | Out-Null
Set-Content -LiteralPath (Join-Path $paths.Runtime 'STOP') -Value 'Installed with control stopped; clear deliberately for a new session.' -Encoding utf8NoBOM
try {
    Copy-Item -LiteralPath (Join-Path $build 'HollowKnightTrial.dll') -Destination $paths.Helper
    Copy-Item -LiteralPath (Join-Path $build 'Assembly-CSharp.dll') -Destination $paths.Assembly -Force
    Assert-Hash $paths.Assembly $report.patchedSHA256
    Assert-Hash $paths.Helper $report.helperSHA256
    Write-JsonFile (Join-Path $paths.Runtime 'installed-report.json') $report
} catch {
    Copy-Item -LiteralPath $backup -Destination $paths.Assembly -Force
    if ((Test-Path -LiteralPath $paths.Helper) -and (Get-Sha256 $paths.Helper) -eq $report.helperSHA256) { Remove-Item -LiteralPath $paths.Helper }
    throw
}
Write-Output 'Bridge installed; vanilla assembly backed up under ignored build/backups. No save files were read or changed. STOP is latched.'
