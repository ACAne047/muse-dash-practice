param([string]$GameRoot = $env:MUSEDASH_GAME_ROOT)
& (Join-Path $PSScriptRoot '..\scripts\Build.ps1') -GameRoot $GameRoot
