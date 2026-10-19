$ErrorActionPreference = 'Stop'
$RepositoryRoot = Split-Path -Parent $PSScriptRoot

function Get-GameDirectory([string]$GameRoot) {
    if ([string]::IsNullOrWhiteSpace($GameRoot)) {
        throw 'Specify -GameRoot or set MUSEDASH_GAME_ROOT to the Muse Dash installation folder.'
    }
    $resolved = (Resolve-Path -LiteralPath $GameRoot).Path
    if (-not (Test-Path -LiteralPath (Join-Path $resolved 'MuseDash.exe') -PathType Leaf)) {
        throw 'GameRoot must contain MuseDash.exe.'
    }
    return $resolved
}

function Assert-GameClosed {
    if (Get-Process -Name MuseDash -ErrorAction SilentlyContinue) {
        throw 'Close Muse Dash before changing the installed practice mod.'
    }
}

function Get-MaintenanceState {
    return (Get-Content -LiteralPath (Join-Path $RepositoryRoot 'maintenance-state.json') -Raw | ConvertFrom-Json)
}

function Assert-GameFingerprint([string]$GameRoot) {
    $state = Get-MaintenanceState
    $native = Join-Path $GameRoot 'GameAssembly.dll'
    if ((Get-FileHash -LiteralPath $native -Algorithm SHA256).Hash -ne $state.gameassembly_sha256) {
        throw 'GameAssembly.dll differs from the verified game build. Recheck compatibility before installing this release.'
    }
}

function Initialize-DotNet {
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
    $env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
    $env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = 'false'
    $env:DOTNET_CLI_HOME = Join-Path $RepositoryRoot 'artifacts\dotnet-home'
    $env:NUGET_PACKAGES = Join-Path $RepositoryRoot 'artifacts\nuget-packages'
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw '.NET SDK 6 is required for building and tests.' }
}
