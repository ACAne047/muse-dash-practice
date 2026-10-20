param(
    [string]$GameRoot = $env:MUSEDASH_GAME_ROOT,
    [string]$ModPath = (Join-Path $PSScriptRoot '..\artifacts\mod\MuseDashPracticePrototype.dll')
)
$ErrorActionPreference = 'Stop'
[void][Reflection.Assembly]::LoadFrom((Join-Path $GameRoot 'MelonLoader\net6\Mono.Cecil.dll'))
$built = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($ModPath)
try {
    $practice = $built.MainModule.Types | Where-Object Name -eq 'PracticeMod'
    function Method($name) {
        $method = $practice.Methods | Where-Object Name -eq $name
        if (-not $method -or -not $method.HasBody) { throw "Missing boundary method: $name" }
        $method
    }
    function Calls($name) {
        @((Method $name).Body.Instructions | Where-Object {$_.OpCode.Name -in @('call','callvirt')} | ForEach-Object {$_.Operand.Name})
    }
    if ('InstallLeadInProtection' -notin (Calls 'OnInitializeMelon')) { throw 'Lead-in hooks are not installed.' }
    $hookNames = @((Method 'InstallLeadInProtection').Body.Instructions | Where-Object {$_.OpCode.Name -eq 'ldstr'} | ForEach-Object Operand)
    foreach ($hook in @('Hurt','MissHardEffect','AddHp')) {
        if ($hook -notin $hookNames) { throw "Missing lead-in damage hook: $hook" }
    }
    if ('IsLeadInProtected' -notin (Calls 'AllowLeadInHurt') -or 'AllowLeadInHurt' -notin (Calls 'AllowLeadInHpChange')) {
        throw 'Damage guards do not share the scoped lead-in window.'
    }
    foreach ($call in @('get_instance','get_Pointer')) {
        if ($call -notin (Calls 'AllowLeadInHurt')) { throw 'Damage guard is not scoped to the live player.' }
    }
    $scope = @((Method 'IsLeadInProtected').Body.Instructions | Where-Object {$_.OpCode.Name -eq 'ldfld'} | ForEach-Object {$_.Operand.Name})
    foreach ($field in @('_ready','_enabled','_inBattle')) {
        if ($field -notin $scope) { throw "Lead-in guard lacks practice scope: $field" }
    }
    foreach ($call in @('get_CanRepeat','get_timeFromMusicStart','Contains')) {
        if ($call -notin (Calls 'IsLeadInProtected')) { throw "Lead-in guard lacks running/song-time boundary: $call" }
    }
    foreach ($name in @('StageStarted','OnSceneWasLoaded','MarkNewA','PauseForParameters','Arm','EnablePractice','RequestExit','OnDeinitializeMelon')) {
        if ('Cancel' -notin (Calls $name)) { throw "Old lead-in window is not cancelled by $name" }
    }
    foreach ($name in @('SeekAtFreshStart','RewindInScene')) {
        foreach ($call in @('PlanRewindBoundary','BeginLeadIn','RestoreBoundaryNotes')) {
            if ($call -notin (Calls $name)) { throw "Rewind boundary missing from $name : $call" }
        }
        $calls = Calls $name
        if ([Array]::IndexOf($calls,'BeginLeadIn') -gt [Array]::IndexOf($calls,'RestoreBoundaryNotes')) { throw 'Warm starts can hurt before protection begins.' }
    }
    $restore = Method 'RestoreBoundaryNotes'
    foreach ($call in @('GetPreLoadGameObject','get_activeSelf','CreateBattleEnemy')) {
        if ($call -notin (Calls 'RestoreBoundaryNotes')) { throw "Missing preloaded object reuse: $call" }
    }
    if ('Init' -in (Calls 'RestoreBoundaryNotes') -or 'InitWithoutRun' -in (Calls 'RestoreBoundaryNotes')) { throw 'Boundary restore reinitializes registered note controllers.' }
    $restoredCursors = @($restore.Body.Instructions | Where-Object {$_.OpCode.Name -in @('call','callvirt') -and $_.Operand.Name -in @('set_m_CurrentGenIdx','set_passIdx','set_prePassIdx')})
    if ($restoredCursors.Count -ne 3 -or -not @($restore.Body.ExceptionHandlers | Where-Object HandlerType -eq 'Finally').Count) {
        throw 'Native creation cursors must be restored in a finally block.'
    }
    $data = [IO.File]::ReadAllBytes((Join-Path $GameRoot 'GameAssembly.dll'))
    $pe = [BitConverter]::ToInt32($data, 0x3c)
    $count = [BitConverter]::ToUInt16($data, $pe + 6)
    $optional = [BitConverter]::ToUInt16($data, $pe + 20)
    function Offset([int]$rva) {
        for ($i=0; $i -lt $count; $i++) {
            $header=$pe+24+$optional+$i*40
            $start=[BitConverter]::ToInt32($data,$header+12)
            $size=[BitConverter]::ToInt32($data,$header+16)
            if ($rva -ge $start -and $rva+5 -le $start+$size) { return [BitConverter]::ToInt32($data,$header+20)+$rva-$start }
        }
        throw 'Native boundary callsite outside PE section.'
    }
    $branches=@(
        @(0x4766f6,0x214c600), @(0x214c872,0x214aa40), @(0x214c90a,0x214aa40),
        @(0x214981d,0x2149840), @(0x21498b9,0x1e5f1a0), @(0x21499f8,0x214a0c0),
        @(0x214a201,0x206b100), @(0x206b1aa,0x1e34760)
    )
    foreach ($branch in $branches) {
        $offset=Offset $branch[0]
        if ($data[$offset] -notin @(0xe8,0xe9) -or $branch[0]+5+[BitConverter]::ToInt32($data,$offset+1) -ne $branch[1]) {
            throw ('Native damage/warm-start route changed at RVA 0x{0:x}' -f $branch[0])
        }
    }
    [pscustomobject]@{Status='PASS (compiled and native boundary audit; not an in-game test)';DamageHooks=3;PlayerScoped=$true;UsesSongTimeAndRunningRange=$true;LifecycleCancels=8;SeekPaths=2;PreloadedControllersReused=$true;NativeCursorsRestored=$true;NativeBoundaryBranches=$branches.Count} | ConvertTo-Json
} finally { $built.Dispose() }
