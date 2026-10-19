# Shared helpers; dot-source from the scripts in this directory.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-GamePaths([string]$GameDirectory) {
    $game = (Resolve-Path -LiteralPath $GameDirectory).Path
    $managed = Join-Path $game 'hollow_knight_Data/Managed'
    if (!(Test-Path -LiteralPath (Join-Path $game 'hollow_knight.exe') -PathType Leaf) -or
        !(Test-Path -LiteralPath (Join-Path $managed 'Assembly-CSharp.dll') -PathType Leaf)) {
        throw 'GameDirectory must be a Windows Hollow Knight installation.'
    }
    [pscustomobject]@{ Game = $game; Managed = $managed; Runtime = Join-Path $game '.jev/runtime'; Assembly = Join-Path $managed 'Assembly-CSharp.dll'; Helper = Join-Path $managed 'HollowKnightTrial.dll' }
}

function Assert-GameStopped {
    if (Get-Process -Name hollow_knight -ErrorAction SilentlyContinue) { throw 'Close Hollow Knight before this operation.' }
}

function Get-Sha256([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash }

function Assert-Hash([string]$Path, [string]$Expected) {
    if ($Expected -notmatch '^[A-Fa-f0-9]{64}$' -or !(Test-Path -LiteralPath $Path -PathType Leaf) -or (Get-Sha256 $Path) -ne $Expected) {
        throw "Hash verification failed: $([IO.Path]::GetFileName($Path))"
    }
}

function Write-JsonFile([string]$Path, $Value) {
    $Value | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $Path -Encoding utf8NoBOM
}

function Get-InstalledReceipt($Paths) {
    $receipt = Get-Content -LiteralPath (Join-Path $Paths.Runtime 'installed-report.json') -Raw | ConvertFrom-Json
    if ($receipt.schema -ne 1 -or $receipt.product -ne 'hollow-knight-jev-source' -or !$receipt.originalBodiesPreserved -or
        $receipt.inputSHA256 -ne 'E9048EF6A633970F735E01EC166D3959F610EAEA7A88D827D48D67B1E5FB87BD') { throw 'Not a supported portable install receipt.' }
    Assert-Hash $Paths.Assembly $receipt.patchedSHA256
    Assert-Hash $Paths.Helper $receipt.helperSHA256
    $receipt
}
