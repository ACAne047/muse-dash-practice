param([string]$GameRoot = 'D:\Lilywhite\Steam\steamapps\common\Muse Dash')
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$project = Join-Path $PSScriptRoot 'MuseDashPracticePrototype.csproj'
& dotnet build $project -c Release "-p:GameRoot=$GameRoot" --configfile (Join-Path $PSScriptRoot 'NuGet.Config') -o (Join-Path $PSScriptRoot '..\build') --nologo
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
