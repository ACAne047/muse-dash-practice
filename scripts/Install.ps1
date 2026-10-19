[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$GameRoot = $env:MUSEDASH_GAME_ROOT,
    [string]$DllPath
)
. (Join-Path $PSScriptRoot 'Common.ps1')
$game = Get-GameDirectory $GameRoot
Assert-GameClosed
Assert-GameFingerprint $game
if (-not (Test-Path -LiteralPath (Join-Path $game 'MelonLoader\net6\MelonLoader.dll'))) {
    throw 'MelonLoader 0.7.0 must already be installed.'
}
$state = Get-MaintenanceState
if ([string]::IsNullOrWhiteSpace($DllPath)) {
    $DllPath = Join-Path $RepositoryRoot $state.release_dll
    if ((Get-FileHash -LiteralPath $DllPath -Algorithm SHA256).Hash -ne $state.dll_sha256) {
        throw 'The bundled release DLL failed its SHA-256 check.'
    }
}
$source = (Resolve-Path -LiteralPath $DllPath).Path
$identity = [Reflection.AssemblyName]::GetAssemblyName($source)
if ($identity.Name -ne 'MuseDashPracticePrototype') { throw 'DllPath is not the practice mod assembly.' }
$mods = Join-Path $game 'Mods'
$target = Join-Path $mods 'MuseDashPracticePrototype.dll'
$sourceHash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
if ((Test-Path -LiteralPath $target) -and (Get-FileHash -LiteralPath $target).Hash -eq $sourceHash) {
    Write-Output 'The same practice mod DLL is already installed.'
    return
}
if ($PSCmdlet.ShouldProcess($target, "Install practice mod $($identity.Version), backing up any existing practice DLL")) {
    if (Test-Path -LiteralPath $target) {
        $backup = Join-Path $game ('UserData\MuseDashPracticeBackups\' + (Get-Date -Format 'yyyyMMdd-HHmmss-fffffff'))
        New-Item -ItemType Directory -Path $backup | Out-Null
        Copy-Item -LiteralPath $target -Destination (Join-Path $backup 'MuseDashPracticePrototype.dll')
        Write-Output "Previous practice DLL saved to: $backup"
    }
    New-Item -ItemType Directory -Path $mods -Force | Out-Null
    Copy-Item -LiteralPath $source -Destination $target -Force
    if ((Get-FileHash -LiteralPath $target).Hash -ne $sourceHash) { throw 'Installed DLL checksum mismatch.' }
    Write-Output "Installed: $target"
    Write-Output "SHA-256: $sourceHash"
}
