param([string]$GameRoot = $env:MUSEDASH_GAME_ROOT)
. (Join-Path $PSScriptRoot 'Common.ps1')
$game = Get-GameDirectory $GameRoot
Initialize-DotNet
$project = Join-Path $RepositoryRoot 'src\MuseDashPracticePrototype.csproj'
$output = Join-Path $RepositoryRoot 'artifacts\mod'
Push-Location $RepositoryRoot
try {
    & dotnet build $project -c Release "-p:GameRoot=$game" --configfile (Join-Path $RepositoryRoot 'NuGet.Config') -o $output --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    Get-FileHash -LiteralPath (Join-Path $output 'MuseDashPracticePrototype.dll') -Algorithm SHA256
} finally { Pop-Location }
