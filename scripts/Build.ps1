#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$GameDirectory,
    [string]$OriginalAssemblyPath,
    [string]$CecilAssemblyPath,
    [switch]$Offline
)
. (Join-Path $PSScriptRoot 'Common.ps1')
$repo = Split-Path $PSScriptRoot -Parent
$paths = Get-GamePaths $GameDirectory
if (!$OriginalAssemblyPath) { $OriginalAssemblyPath = $paths.Assembly }
$OriginalAssemblyPath = (Resolve-Path -LiteralPath $OriginalAssemblyPath).Path
Assert-Hash $OriginalAssemblyPath 'E9048EF6A633970F735E01EC166D3959F610EAEA7A88D827D48D67B1E5FB87BD'

$sdkVersion = (& dotnet --version).Trim()
if ($LASTEXITCODE -ne 0 -or $sdkVersion -notmatch '^10\.') { throw '.NET SDK 10 is required.' }
$sdkLine = @(& dotnet --list-sdks) | Where-Object { $_ -match ('^' + [regex]::Escape($sdkVersion) + ' \[') } | Select-Object -First 1
if (!$sdkLine -or $sdkLine -notmatch '\[(.+)\]$') { throw 'Cannot locate the selected .NET compiler.' }
$compiler = Join-Path $Matches[1] "$sdkVersion/Roslyn/bincore/csc.dll"
$build = Join-Path $repo 'build'
$staging = Join-Path $build ('staging-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $staging -Force | Out-Null
$compileArgs = @($compiler, '/nologo', '/target:library', '/optimize+', '/nostdlib+', "/out:$staging/HollowKnightTrial.dll", "/pathmap:$repo=/_/", "/reference:$OriginalAssemblyPath")
$compileArgs += Get-ChildItem -LiteralPath $paths.Managed -Filter '*.dll' | Where-Object Name -notin @('Assembly-CSharp.dll','HollowKnightTrial.dll','BossHealthDisplay.dll') | ForEach-Object { '/reference:' + $_.FullName }
$compileArgs += Get-ChildItem -LiteralPath (Join-Path $repo 'bridge') -Filter '*.cs' | ForEach-Object FullName
& dotnet @compileArgs
if ($LASTEXITCODE -ne 0) { throw 'Bridge compilation failed. No installed game files were changed.' }

$patcherArgs = @('build', (Join-Path $repo 'patcher/Patcher.csproj'), '-c', 'Release', '-p:UseSharedCompilation=false')
if ($CecilAssemblyPath) {
    $CecilAssemblyPath = (Resolve-Path -LiteralPath $CecilAssemblyPath).Path
    $patcherArgs += "-p:CecilAssemblyPath=$CecilAssemblyPath"
}
if ($Offline) { $patcherArgs += '--no-restore' }
& dotnet @patcherArgs
if ($LASTEXITCODE -ne 0) { throw 'Patcher build failed. First build needs the pinned Mono.Cecil NuGet package (or an explicit trusted local CecilAssemblyPath).' }
$patcher = Join-Path $repo 'patcher/bin/Release/net10.0/Patcher.dll'
$reportText = & dotnet $patcher $OriginalAssemblyPath (Join-Path $staging 'HollowKnightTrial.dll') (Join-Path $staging 'Assembly-CSharp.dll') $paths.Managed
if ($LASTEXITCODE -ne 0) { throw 'Patcher compatibility/method-body audit failed; nothing installed.' }
$report = $reportText | ConvertFrom-Json
$report | Add-Member -NotePropertyName product -NotePropertyValue 'hollow-knight-jev-source'
Write-JsonFile (Join-Path $staging 'patch-report.json') $report
# Only promote fully audited artifacts. This folder is excluded from Git.
foreach ($name in @('HollowKnightTrial.dll','Assembly-CSharp.dll','patch-report.json')) {
    Copy-Item -LiteralPath (Join-Path $staging $name) -Destination (Join-Path $build $name) -Force
}
Write-Output 'Compiled and audited locally. Game installation and saves are unchanged. Build outputs contain game code and must never be shared.'
