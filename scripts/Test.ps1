param(
    [string]$GameRoot = $env:MUSEDASH_GAME_ROOT,
    [switch]$SkipBuild
)
. (Join-Path $PSScriptRoot 'Common.ps1')
$game = Get-GameDirectory $GameRoot
Initialize-DotNet
Assert-GameFingerprint $game
if (-not $SkipBuild) { & (Join-Path $PSScriptRoot 'Build.ps1') -GameRoot $game }
$dll = Join-Path $RepositoryRoot 'artifacts\mod\MuseDashPracticePrototype.dll'
if (-not (Test-Path -LiteralPath $dll)) { throw 'Build output is missing. Run Build.ps1 first.' }
Push-Location $RepositoryRoot
try {
    foreach ($relative in @('tests\InputRouteRegression.csproj', 'tests\RangeControls\RangeControls.csproj', 'tests\Presets\Presets.csproj')) {
        $project = Join-Path $RepositoryRoot $relative
        & dotnet restore $project --configfile (Join-Path $RepositoryRoot 'NuGet.Config') --nologo
        if ($LASTEXITCODE -ne 0) { throw "Restore failed: $relative" }
        & dotnet run --project $project -c Release --no-restore
        if ($LASTEXITCODE -ne 0) { throw "Regression failed: $relative" }
    }
    & (Join-Path $RepositoryRoot 'tests\Verify-MultiHitReset.ps1') -GameRoot $game -ModPath $dll
    & (Join-Path $RepositoryRoot 'tests\Verify-BurstPresentationReset.ps1') -GameRoot $game -ModPath $dll
    & (Join-Path $RepositoryRoot 'tests\Verify-Presets.ps1') -GameRoot $game -ModPath $dll
    & (Join-Path $RepositoryRoot 'tests\Verify-InstallScripts.ps1')
} finally { Pop-Location }
