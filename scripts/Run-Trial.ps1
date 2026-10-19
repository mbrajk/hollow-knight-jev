#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$GameDirectory,
    [ValidateRange(1,600)][int]$Seconds = 10,
    [ValidateRange(1,6000)][int]$MaxCalls = 24,
    [ValidateRange(0.003,1.0)][double]$MaxSpendUSD = 0.05,
    [switch]$AllowTraining
)
. (Join-Path $PSScriptRoot 'Common.ps1')
$repo = Split-Path $PSScriptRoot -Parent
$paths = Get-GamePaths $GameDirectory
$null = Get-InstalledReceipt $paths
$controller = Join-Path $repo 'controller/bin/Release/net10.0/JevController.dll'
if (!(Test-Path -LiteralPath $controller)) { throw 'Build the controller in Release configuration first.' }
if ([string]::IsNullOrWhiteSpace($env:TYPESAFE_API_KEY)) { throw 'Set TYPESAFE_API_KEY in this process environment. Do not put it in source or a command argument.' }
if (Test-Path -LiteralPath (Join-Path $paths.Runtime 'STOP')) { throw 'STOP is latched. Restart the game deliberately before a new trial.' }

function Get-ReadyState {
    $state = Get-Content -LiteralPath (Join-Path $paths.Runtime 'state.json') -Raw | ConvertFrom-Json
    $age = ([DateTime]::UtcNow - [DateTime]::Parse($state.observed_utc).ToUniversalTime()).TotalSeconds
    if ($age -lt -0.1 -or $age -gt 0.5 -or $state.scene -ne 'GG_Gruz_Mother' -or
        $state.trial.slot -ne 4 -or !$state.trial.owned_session -or $state.episode.status -ne 'running') {
        throw 'A fresh owned slot-4 Gruz Mother session is required. No provider calls made.'
    }
    if ($state.episode.training -and !$AllowTraining) { throw 'This is a training session. Explicit -AllowTraining is required; it cannot verify a hitless win.' }
    $state
}
$initial = Get-ReadyState
if (!$initial.paused) { throw 'Pause the arena before starting a new paid trial.' }
$run = Join-Path $repo ('artifacts/trial-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $run | Out-Null
$budgetPath = Join-Path $run 'budget.json'
Write-JsonFile $budgetPath @{ limit_usd = $MaxSpendUSD; input_price_per_million_usd = 0.042; spent_upper_usd = 0; calls = @() }
$previousBudget = [Environment]::GetEnvironmentVariable('JEV_TRIAL_BUDGET_FILE', 'Process')
try {
    [Environment]::SetEnvironmentVariable('JEV_TRIAL_BUDGET_FILE', $budgetPath, 'Process')
    $pause = Join-Path $paths.Runtime 'pause.request'
    if (Test-Path -LiteralPath $pause) { Remove-Item -LiteralPath $pause }
    Set-Content -LiteralPath (Join-Path $paths.Runtime 'resume.request') -Value 'Resume this bounded paid trial.' -Encoding utf8NoBOM
    $deadline = [DateTime]::UtcNow.AddSeconds(5)
    $ready = $false
    do {
        try {
            $state = Get-ReadyState
            $ready = $state.session_id -eq $initial.session_id -and !$state.paused -and $state.control_enabled -and
                !(Test-Path -LiteralPath (Join-Path $paths.Runtime 'resume.request'))
        } catch { $ready = $false }
        if (!$ready) { Start-Sleep -Milliseconds 25 }
    } while (!$ready -and [DateTime]::UtcNow -lt $deadline)
    if (!$ready) { throw 'The owned session did not become ready; no provider calls made.' }
    Write-Output "Private run output: $run"
    & dotnet $controller trial --state (Join-Path $paths.Runtime 'state.json') --command (Join-Path $paths.Runtime 'command.json') --output $run --model jev-1.13.0 --scenario gruz-mother --decision-mode grouped --combat-compact true --danger-margin 2 --attack-mode pulse --selection argmax --seconds $Seconds --max-calls $MaxCalls
    if ($LASTEXITCODE -ne 0) { throw 'Controller stopped with an error. Review local redacted diagnostics before another trial.' }
} finally {
    [Environment]::SetEnvironmentVariable('JEV_TRIAL_BUDGET_FILE', $previousBudget, 'Process')
    Set-Content -LiteralPath (Join-Path $paths.Runtime 'STOP') -Value 'Bounded trial ended.' -Encoding utf8NoBOM
    Set-Content -LiteralPath (Join-Path $paths.Runtime 'pause.request') -Value 'Pause after bounded trial.' -Encoding utf8NoBOM
}
