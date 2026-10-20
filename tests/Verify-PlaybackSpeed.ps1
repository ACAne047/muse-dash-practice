param(
    [string]$GameRoot = $env:MUSEDASH_GAME_ROOT,
    [string]$ModPath = (Join-Path $PSScriptRoot '..\artifacts\mod\MuseDashPracticePrototype.dll')
)
$ErrorActionPreference = 'Stop'
[void][Reflection.Assembly]::LoadFrom((Join-Path $GameRoot 'MelonLoader\net6\Mono.Cecil.dll'))
$built = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($ModPath)
try {
    $practice = $built.MainModule.Types | Where-Object Name -eq 'PracticeMod'
    function Calls($name) {
        $method = $practice.Methods | Where-Object Name -eq $name
        if (-not $method -or -not $method.HasBody) { throw "Missing speed method: $name" }
        @($method.Body.Instructions | Where-Object {$_.OpCode.Name -in @('call','callvirt')} | ForEach-Object {$_.Operand.Name})
    }
    foreach ($name in @('SeekAtFreshStart','RewindInScene')) {
        if ('RebasePlaybackClock' -notin (Calls $name)) { throw "Seek does not rebase speed: $name" }
    }
    if ('RefreshSpeedPresentation' -notin (Calls 'RebasePlaybackClock')) { throw 'Seek does not reapply speed before audio resumes.' }
    foreach ($name in @('OnSceneWasLoaded','StageStarted','RequestExit','OnDeinitializeMelon')) {
        if ('RestorePlaybackSpeed' -notin (Calls $name)) { throw "Speed is not restored on lifecycle path: $name" }
    }
    $scope = $practice.Methods | Where-Object Name -eq 'ScaleSongStopwatchTicks'
    $scopeFields = @($scope.Body.Instructions | Where-Object {$_.OpCode.Name -eq 'ldfld'} | ForEach-Object {$_.Operand.Name})
    foreach ($field in @('_speedClockActive','_speedWatchPointer')) {
        if ($field -notin $scopeFields) { throw "Clock hook is not scoped: $field" }
    }
    if ('get_ElapsedTicks' -in (Calls 'ScaleSongStopwatchTicks')) { throw 'Clock hook recursively reads itself.' }
    $animation = $practice.Methods | Where-Object Name -eq 'ScaleAnimationTimerDelta'
    if (-not $animation -or -not $animation.HasBody) { throw 'Independent Spine animation timer scaling is missing.' }
    $animationFields = @($animation.Body.Instructions | Where-Object {$_.OpCode.Name -eq 'ldfld'} | ForEach-Object {$_.Operand.Name})
    foreach ($field in @('_enabled','_inBattle','_speedClockActive')) {
        if ($field -notin $animationFields) { throw "Animation timer hook is not limited to active practice: $field" }
    }
    foreach ($call in @('get_isInGame','get_isPause','ScaleAnimationDelta')) {
        if ($call -notin (Calls 'ScaleAnimationTimerDelta')) { throw "Animation timer pause/rate guard missing: $call" }
    }
    $hooks = $practice.Methods | Where-Object Name -eq 'InstallSpeedHooks'
    $hookNames = @($hooks.Body.Instructions | Where-Object {$_.OpCode.Name -eq 'ldstr'} | ForEach-Object Operand)
    if ('get_deltaTime' -notin $hookNames -or 'Update' -in $hookNames) { throw 'Scale the wall timer getter; leave explicit skeleton Update(songDelta) calls intact.' }
    foreach ($call in @('set_pitch','set_timeScale','set_fixedDeltaTime')) {
        if ($call -notin (Calls 'RefreshSpeedPresentation') -or $call -notin (Calls 'RestorePlaybackSpeed')) {
            throw "Missing speed setting or restoration: $call"
        }
    }
    # Native callsites were decoded against this GameAssembly. Validate the exact
    # relative branches, not proxy IL stubs, so the shared-clock route is tested.
    $data = [IO.File]::ReadAllBytes((Join-Path $GameRoot 'GameAssembly.dll'))
    $pe = [BitConverter]::ToInt32($data, 0x3c)
    $count = [BitConverter]::ToUInt16($data, $pe + 6)
    $optional = [BitConverter]::ToUInt16($data, $pe + 20)
    function Offset([int]$rva) {
        for ($i = 0; $i -lt $count; $i++) {
            $header = $pe + 24 + $optional + $i * 40
            $start = [BitConverter]::ToInt32($data, $header + 12)
            $size = [BitConverter]::ToInt32($data, $header + 16)
            if ($rva -ge $start -and $rva + 5 -le $start + $size) {
                return [BitConverter]::ToInt32($data, $header + 20) + $rva - $start
            }
        }
        throw 'Native callsite outside PE section data.'
    }
    $branches = @(
        @(0x1d7749c,0xeb92c0), @(0x1d7739d,0xeb92c0), @(0x1d77300,0xeb92c0),
        @(0xeb936b,0xeb94d0), @(0xeb93a3,0xeb94d0), @(0xeb945f,0xeb94d0),
        @(0xeb959d,0xeb94d0), @(0xeb95d7,0xeb94d0),
        @(0x9ef2d4,0x1d2c060), @(0x1d2be5a,0xeb94d0), @(0x1d2be72,0xeb94d0)
    )
    foreach ($branch in $branches) {
        $offset = Offset $branch[0]
        if ($data[$offset] -notin @(0xe8,0xe9) -or $branch[0] + 5 + [BitConverter]::ToInt32($data, $offset + 1) -ne $branch[1]) {
            throw ('Native song-clock call route changed at RVA 0x{0:x}' -f $branch[0])
        }
    }
    [pscustomobject]@{Status='PASS (compiled and native clock audit; not an in-game test)';NativeClockBranches=$branches.Count;ClockHookScopedToSongWatch=$true;IndependentAnimationTimerScaled=$true;ExplicitSkeletonUpdatesUnchanged=$true;AnimationScalingBypassedWhilePaused=$true;SeekRebases=2;LifecycleRestorePaths=4;PitchTimeScaleAndFixedDeltaRestored=$true} | ConvertTo-Json
} finally { $built.Dispose() }
