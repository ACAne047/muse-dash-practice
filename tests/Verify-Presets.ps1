param(
    [string]$GameRoot = $env:MUSEDASH_GAME_ROOT,
    [string]$ModPath = (Join-Path $PSScriptRoot '..\artifacts\mod\MuseDashPracticePrototype.dll')
)
$ErrorActionPreference = 'Stop'
[void][Reflection.Assembly]::LoadFrom((Join-Path $GameRoot 'MelonLoader\net6\Mono.Cecil.dll'))
$built = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($ModPath)
try {
    $practice = $built.MainModule.Types | Where-Object FullName -eq 'MuseDashPracticePrototype.PracticeMod'
    function Calls($method) {
        @($method.Body.Instructions | Where-Object {$_.OpCode.Name -in @('call','callvirt')} | ForEach-Object {$_.Operand.Name})
    }
    $fill = $practice.Methods | Where-Object Name -eq 'FillPreset'
    $fillCalls = Calls $fill
    if ('Suspend' -notin $fillCalls) { throw 'Preset selection does not suspend the old range.' }
    foreach ($name in @('Arm','EnablePractice','Resume','Pause','RewindInScene','SaveNew','Update')) {
        if ($name -in $fillCalls) { throw "Preset selection performs an unexpected operation: $name" }
    }
    $stores = @($fill.Body.Instructions | Where-Object {$_.OpCode.Name -eq 'stfld'})
    foreach ($flag in @('_restartPending','_seekPending','_reloadLoopPending')) {
        $write = @($stores | Where-Object {$_.Operand.Name -eq $flag})
        if ($write.Count -ne 1 -or $write[0].Previous.OpCode.Name -ne 'ldc.i4.0') { throw "Preset selection does not cancel $flag" }
    }
    foreach ($field in @('_aText','_bText','_leadText')) {
        if (-not ($stores | Where-Object {$_.Operand.Name -eq $field})) { throw "Preset value is not copied: $field" }
    }
    if ($fill.Body.Instructions | Where-Object {$_.OpCode.Name -eq 'stsfld' -and $_.Operand.Name -eq 'Tainted'}) {
        throw 'Preset selection changes score-protection state.'
    }
    $start = $practice.Methods | Where-Object Name -eq 'StageStarted'
    $update = $practice.Methods | Where-Object Name -eq 'OnUpdate'
    if ('BeginPresetChart' -notin (Calls $start) -or 'PreparePresetChart' -notin (Calls $update)) { throw 'Chart lifecycle wiring missing.' }
    $prepare = $practice.Methods | Where-Object Name -eq 'PreparePresetChart'
    $prepareCalls = Calls $prepare
    foreach ($getter in @('get_tick','get_configData','get_pathway','get_note_uid','get_length','Compute','RefreshPresets')) {
        if ($getter -notin $prepareCalls) { throw "Missing native fingerprint input: $getter" }
    }
    foreach ($getter in @('get_showTick','get_objId','get_isLongPressing','get_m_Score')) {
        if ($getter -in $prepareCalls) { throw "Fingerprint includes volatile or character-dependent data: $getter" }
    }
    $chart = $built.MainModule.Types | Where-Object Name -eq 'PresetChart'
    foreach ($method in $chart.Methods | Where-Object Name -in @('SameSong','SameRevision')) {
        if ('get_LastSeenUid' -in (Calls $method)) { throw 'Preset identity depends on a temporary registration UID.' }
    }
    $save = $practice.Methods | Where-Object Name -eq 'SavePreset'
    foreach ($name in @('Arm','EnablePractice','Resume','RewindInScene')) {
        if ($name -in (Calls $save)) { throw "Saving a preset unexpectedly performs $name" }
    }
    [pscustomobject]@{
        Status = 'PASS (compiled integration audit, not an in-game test)'
        SelectingPresetOnlyFillsAndSuspends = $true
        QueuedRepeatsCancelled = 3
        ChartLifecycleConnected = $true
        ImmutableNativeFingerprintInputs = $true
        TemporaryUidExcludedFromMatching = $true
        SavingDoesNotStartPractice = $true
    } | ConvertTo-Json
} finally { $built.Dispose() }
