[CmdletBinding(SupportsShouldProcess = $true)]
param([string]$GameRoot = $env:MUSEDASH_GAME_ROOT)
. (Join-Path $PSScriptRoot 'Common.ps1')
$game = Get-GameDirectory $GameRoot
Assert-GameClosed
$modPath = Join-Path $game 'Mods\MuseDashPracticePrototype.dll'
if (-not (Test-Path -LiteralPath $modPath)) { Write-Output 'The practice mod is already disabled.'; return }
$disabledPath = "$modPath.disabled"
if (Test-Path -LiteralPath $disabledPath) { $disabledPath = "$modPath.$(Get-Date -Format yyyyMMdd-HHmmss-fffffff).disabled" }
if ($PSCmdlet.ShouldProcess($modPath, "Rename practice DLL to $disabledPath")) {
    Move-Item -LiteralPath $modPath -Destination $disabledPath
    Write-Output "Disabled practice mod: $disabledPath"
}
