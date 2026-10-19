# Runs only against an isolated fixture beneath artifacts; never installs into the real game.
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$fixture = Join-Path $repo ('artifacts\installer-test\' + [Guid]::NewGuid().ToString('N'))
$fixtureRepo = Join-Path $fixture 'repository'
$game = Join-Path $fixture 'game'
foreach ($path in @($fixtureRepo, $game, (Join-Path $game 'Mods'), (Join-Path $game 'MelonLoader\net6'))) {
    New-Item -ItemType Directory -Path $path -Force | Out-Null
}
Copy-Item -LiteralPath (Join-Path $repo 'scripts') -Destination $fixtureRepo -Recurse
$state = Get-Content -LiteralPath (Join-Path $repo 'maintenance-state.json') -Raw | ConvertFrom-Json
$release = Join-Path $repo $state.release_dll
$fixtureRelease = Join-Path $fixtureRepo 'release.dll'
Copy-Item -LiteralPath $release -Destination $fixtureRelease
foreach ($relative in @('MuseDash.exe', 'GameAssembly.dll', 'MelonLoader\net6\MelonLoader.dll')) {
    [IO.File]::WriteAllBytes((Join-Path $game $relative), [byte[]]@())
}
$state.gameassembly_sha256 = (Get-FileHash -LiteralPath (Join-Path $game 'GameAssembly.dll')).Hash
$state.release_dll = 'release.dll'
$state | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $fixtureRepo 'maintenance-state.json') -Encoding utf8
$target = Join-Path $game 'Mods\MuseDashPracticePrototype.dll'
[IO.File]::WriteAllText($target, 'previous-practice-dll-fixture')
$previousHash = (Get-FileHash -LiteralPath $target).Hash
$releaseHash = (Get-FileHash -LiteralPath $release).Hash
$unrelated = Join-Path $game 'Mods\UnrelatedMod.dll'
[IO.File]::WriteAllText($unrelated, 'unrelated-mod-fixture')
$unrelatedHash = (Get-FileHash -LiteralPath $unrelated).Hash
$install = Join-Path $fixtureRepo 'scripts\Install.ps1'
$disable = Join-Path $fixtureRepo 'scripts\Disable.ps1'
$backups = Join-Path $game 'UserData\MuseDashPracticeBackups'

& $install -GameRoot $game -WhatIf
if ((Get-FileHash -LiteralPath $target).Hash -ne $previousHash -or (Test-Path -LiteralPath $backups)) {
    throw 'Install -WhatIf changed the fixture.'
}
& $install -GameRoot $game
$saved = @(Get-ChildItem -LiteralPath $backups -Recurse -File)
if ($saved.Count -ne 1 -or (Get-FileHash -LiteralPath $saved[0].FullName).Hash -ne $previousHash) {
    throw 'The previous practice DLL was not backed up exactly.'
}
if ((Get-FileHash -LiteralPath $target).Hash -ne $releaseHash) { throw 'Install copy differs from release.' }
& $install -GameRoot $game
if (@(Get-ChildItem -LiteralPath $backups -Recurse -File).Count -ne 1) { throw 'Identical installation made another backup.' }
& $disable -GameRoot $game -WhatIf
if (-not (Test-Path -LiteralPath $target)) { throw 'Disable -WhatIf changed the fixture.' }
& $disable -GameRoot $game
if ((Test-Path -LiteralPath $target) -or (Get-FileHash -LiteralPath "$target.disabled").Hash -ne $releaseHash) {
    throw 'Disable did not preserve the practice DLL.'
}
& $disable -GameRoot $game
& $install -GameRoot $game -DllPath $fixtureRelease
if ((Get-FileHash -LiteralPath $target).Hash -ne $releaseHash) { throw 'Explicit DllPath installation failed.' }
if ((Get-FileHash -LiteralPath $unrelated).Hash -ne $unrelatedHash) { throw 'Another mod was modified.' }
Write-Output 'PASS: installer fixture (preview, exact backup, install, no-op, disable, explicit DLL, unrelated mod preserved).'
