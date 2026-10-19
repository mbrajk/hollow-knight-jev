#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$GameDirectory,
    [Parameter(Mandatory)][ValidateSet('Create','Resume','EnterArena','Pause','ResumePlay','Finish','Stop')][string]$Action,
    [string]$SteamExecutable,
    [switch]$Control,
    [switch]$ClearStop
)
. (Join-Path $PSScriptRoot 'Common.ps1')
$paths = Get-GamePaths $GameDirectory
$null = Get-InstalledReceipt $paths
if ($Action -in @('Create','Resume')) {
    if (!$SteamExecutable) { throw 'SteamExecutable is required to launch.' }
    $launch = @{ GameDirectory = $paths.Game; SteamExecutable = $SteamExecutable; Godseeker = $true; GruzMother = $true; Control = $Control; ClearStop = $ClearStop }
    if ($Action -eq 'Create') { $launch.NewTrialSlot4 = $true } else { $launch.ResumeTrialSlot4 = $true }
    & (Join-Path $PSScriptRoot 'Launch.ps1') @launch
    return
}
if ($Action -eq 'Stop') {
    Set-Content -LiteralPath (Join-Path $paths.Runtime 'STOP') -Value 'Explicit operator stop.' -Encoding utf8NoBOM
    Set-Content -LiteralPath (Join-Path $paths.Runtime 'pause.request') -Value 'Explicit operator pause.' -Encoding utf8NoBOM
    Write-Output 'STOP latched. Control leases are finite; pause will be requested on the owned gameplay session. Use Finish separately to save and quit.'
    return
}
$state = Get-Content -LiteralPath (Join-Path $paths.Runtime 'state.json') -Raw | ConvertFrom-Json
$age = [DateTime]::UtcNow - [DateTime]::Parse($state.observed_utc).ToUniversalTime()
if ($age.TotalSeconds -gt 2 -or $age.TotalSeconds -lt -1) { throw 'No fresh game state. Request not written.' }
if ($state.trial.slot -ne 4 -or !$state.trial.owned_session) { throw 'No native owned slot-4 session. Request not written.' }
$requests = @{ EnterArena = 'enter-gruz.request'; Pause = 'pause.request'; ResumePlay = 'resume.request'; Finish = 'finish.request' }
Set-Content -LiteralPath (Join-Path $paths.Runtime $requests[$Action]) -Value 'Explicit operator request.' -Encoding utf8NoBOM
Write-Output "Requested $Action. The native bridge independently enforces owned slot 4. Finish saves through the native API, writes an ownership receipt, then quits. Inspect trial-save-result.json for completion."
