#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$GameDirectory,
    [Parameter(Mandatory)][string]$SteamExecutable,
    [switch]$Control,
    [switch]$NewTrialSlot4,
    [switch]$ResumeTrialSlot4,
    [switch]$Godseeker,
    [switch]$GruzMother,
    [switch]$BossTraining,
    [switch]$Capture,
    [switch]$SelfTest,
    [switch]$ClearStop
)
. (Join-Path $PSScriptRoot 'Common.ps1')
Assert-GameStopped
$paths = Get-GamePaths $GameDirectory
$null = Get-InstalledReceipt $paths
$steam = (Resolve-Path -LiteralPath $SteamExecutable).Path
if ([IO.Path]::GetFileName($steam) -ine 'steam.exe') { throw 'SteamExecutable must identify steam.exe.' }
if ($NewTrialSlot4 -and $ResumeTrialSlot4) { throw 'Choose new or resume, not both.' }
if ($Control -and !$NewTrialSlot4 -and !$ResumeTrialSlot4) { throw 'Control requires native verified slot-4 setup or resume.' }
if ($GruzMother -and (!$Godseeker -or (!$NewTrialSlot4 -and !$ResumeTrialSlot4))) { throw 'GruzMother requires a verified Godseeker slot-4 session.' }
if ($BossTraining -and !$GruzMother) { throw 'BossTraining requires GruzMother. Training is not hitless verification.' }
if ($ResumeTrialSlot4 -and !(Test-Path -LiteralPath (Join-Path $paths.Runtime 'slot4-ownership.json'))) { throw 'No native ownership receipt. Existing personal saves cannot be adopted by this harness.' }
$stop = Join-Path $paths.Runtime 'STOP'
if ($ClearStop -and (Test-Path -LiteralPath $stop)) { Remove-Item -LiteralPath $stop }
if ($Control -and (Test-Path -LiteralPath $stop)) { throw 'STOP is latched. Use -ClearStop deliberately for a new control session.' }
# Closed-game stale requests must not execute in the new session.
foreach ($name in @('command.json','enter-gruz.request','pause.request','resume.request','finish.request','capture.request')) {
    $request = Join-Path $paths.Runtime $name
    if (Test-Path -LiteralPath $request) { Remove-Item -LiteralPath $request }
}
$launchArgs = '-applaunch 367520 --jev-bridge'
foreach ($pair in @(@($Control,'--jev-control'),@($NewTrialSlot4,'--jev-new-slot4'),@($ResumeTrialSlot4,'--jev-resume-slot4'),@($Godseeker,'--jev-godseeker'),@($GruzMother,'--jev-gruz-mother'),@($BossTraining,'--jev-boss-training'),@($Capture,'--jev-capture'),@($SelfTest,'--jev-selftest'))) {
    if ($pair[0]) { $launchArgs += ' ' + $pair[1] }
}
$launchArgs += ' -logFile "' + (Join-Path $paths.Runtime 'game.log') + '"'
Start-Process -FilePath $steam -ArgumentList $launchArgs -WindowStyle Hidden
Write-Output 'Launched through Steam. Native slot checks run before controls can activate. Capture, when requested, is a single screenshot updated once per second, not video.'
