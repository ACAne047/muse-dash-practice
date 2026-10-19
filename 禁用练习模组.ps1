param([string]$GameRoot = 'D:\Lilywhite\Steam\steamapps\common\Muse Dash')
$ErrorActionPreference = 'Stop'
if (Get-Process -Name MuseDash -ErrorAction SilentlyContinue) { throw 'Please close Muse Dash first.' }
$resolvedGame = (Resolve-Path -LiteralPath $GameRoot).Path
if (-not (Test-Path -LiteralPath (Join-Path $resolvedGame 'MuseDash.exe'))) { throw 'Not a Muse Dash folder.' }
$modPath = Join-Path $resolvedGame 'Mods\MuseDashPracticePrototype.dll'
if (-not (Test-Path -LiteralPath $modPath)) { Write-Output 'The practice mod is already disabled.'; return }
$disabledPath = "$modPath.disabled"
if (Test-Path -LiteralPath $disabledPath) { $disabledPath = "$modPath.$(Get-Date -Format yyyyMMdd-HHmmss).disabled" }
Move-Item -LiteralPath $modPath -Destination $disabledPath
Write-Output "Disabled practice mod: $disabledPath"
