#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$GameDirectory,
    [Parameter(Mandatory)][string]$OriginalAssemblyPath
)
. (Join-Path $PSScriptRoot 'Common.ps1')
$repo = Split-Path $PSScriptRoot -Parent
$build = Join-Path $repo 'build'
$real = Get-GamePaths $GameDirectory
$beforeReal = Get-Sha256 $real.Assembly
Assert-Hash $OriginalAssemblyPath 'E9048EF6A633970F735E01EC166D3959F610EAEA7A88D827D48D67B1E5FB87BD'
$fixture = Join-Path $build ('safety-fixture-' + [guid]::NewGuid().ToString('N'))
$managed = Join-Path $fixture 'hollow_knight_Data/Managed'
New-Item -ItemType Directory -Path $managed -Force | Out-Null
# A text marker satisfies the installation layout check; this is never launched.
Set-Content -LiteralPath (Join-Path $fixture 'hollow_knight.exe') -Value 'NON-EXECUTABLE TEST FIXTURE' -Encoding utf8NoBOM
Copy-Item -LiteralPath $OriginalAssemblyPath -Destination (Join-Path $managed 'Assembly-CSharp.dll')
$fixturePaths = Get-GamePaths $fixture
$checks = [ordered]@{}
function Check([string]$Name, [bool]$Pass) { $checks[$Name] = $Pass; if (!$Pass) { throw "Safety check failed: $Name" } }
function Refuses([scriptblock]$Operation) { try { & $Operation | Out-Null; return $false } catch { return $true } }

& (Join-Path $PSScriptRoot 'Install.ps1') -GameDirectory $fixture | Out-Null
$receipt = Get-InstalledReceipt $fixturePaths
Check 'vanilla_backup_and_matching_install' ($receipt.inputSHA256 -eq (Get-Sha256 $OriginalAssemblyPath))
Check 'stop_latched_on_install' (Test-Path -LiteralPath (Join-Path $fixturePaths.Runtime 'STOP'))
Check 'duplicate_install_refused' (Refuses { & (Join-Path $PSScriptRoot 'Install.ps1') -GameDirectory $fixture })

[IO.File]::AppendAllText($fixturePaths.Helper, 'changed-test-helper')
Check 'changed_helper_restore_refused' (Refuses { & (Join-Path $PSScriptRoot 'Restore.ps1') -GameDirectory $fixture })
Check 'failed_restore_leaves_assembly_unchanged' ((Get-Sha256 $fixturePaths.Assembly) -eq $receipt.patchedSHA256)
Copy-Item -LiteralPath (Join-Path $build 'HollowKnightTrial.dll') -Destination $fixturePaths.Helper -Force
& (Join-Path $PSScriptRoot 'Restore.ps1') -GameDirectory $fixture | Out-Null
Check 'vanilla_restored_exactly' ((Get-Sha256 $fixturePaths.Assembly) -eq $receipt.inputSHA256)
Check 'only_matching_helper_removed' (!(Test-Path -LiteralPath $fixturePaths.Helper))
Check 'install_receipt_removed_on_restore' (!(Test-Path -LiteralPath (Join-Path $fixturePaths.Runtime 'installed-report.json')))

Set-Content -LiteralPath $fixturePaths.Helper -Value 'UNRELATED HELPER' -Encoding utf8NoBOM
Check 'existing_helper_install_refused' (Refuses { & (Join-Path $PSScriptRoot 'Install.ps1') -GameDirectory $fixture })
Check 'refused_install_leaves_vanilla_unchanged' ((Get-Sha256 $fixturePaths.Assembly) -eq $receipt.inputSHA256)
$rejectedOutput = Join-Path $fixture 'unsupported-output.dll'
$null = & dotnet (Join-Path $repo 'patcher/bin/Release/net10.0/Patcher.dll') (Join-Path $build 'Assembly-CSharp.dll') (Join-Path $build 'HollowKnightTrial.dll') $rejectedOutput $real.Managed 2>&1
Check 'modified_input_patcher_refused_without_output' ($LASTEXITCODE -ne 0 -and !(Test-Path -LiteralPath $rejectedOutput))
Check 'actual_game_assembly_unchanged' ((Get-Sha256 $real.Assembly) -eq $beforeReal)
$checks | ConvertTo-Json
Write-Output 'Only ignored fixtures/build outputs were written. No actual installation, launch, input, saves, or provider calls. Native save ownership lifecycle still requires game runtime validation.'
