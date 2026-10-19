param(
    [string]$GameRoot = $env:MUSEDASH_GAME_ROOT,
    [string]$ModPath = (Join-Path $PSScriptRoot '..\artifacts\mod\MuseDashPracticePrototype.dll')
)
$ErrorActionPreference = 'Stop'
[void][Reflection.Assembly]::LoadFrom((Join-Path $GameRoot 'MelonLoader\net6\Mono.Cecil.dll'))
$built = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($ModPath)
try {
    $type = $built.MainModule.Types | Where-Object {$_.Name -eq 'BurstPresentationReset'}
    $restore = $type.Methods | Where-Object {$_.Name -eq 'Restore'}
    $instructions = @($restore.Body.Instructions)
    $calls = @($instructions | Where-Object {$_.OpCode.Name -in @('call', 'callvirt')})
    foreach ($method in @('GetEventType', 'get_on_multi_hit_changed', 'IsValid')) {
        if (-not ($calls | Where-Object {$_.Operand.Name -eq $method})) {throw "Missing counter scope filter: $method"}
    }
    $zeroCalls = @($calls | Where-Object {$_.Operand.Name -eq 'OnCountChanged'})
    if ($zeroCalls.Count -ne 2) {throw 'Counter must exit and settle its end transition.'}
    foreach ($call in $zeroCalls) {
        if ($call.Previous.OpCode.Name -ne 'ldc.i4.0') {throw 'Counter reset is not using zero.'}
    }
    $kills = @($calls | Where-Object {$_.Operand.Name -eq 'DOKill'})
    if ($kills.Count -ne 2) {throw 'Both camera and transform tweens must be stopped.'}
    $zoom = $calls | Where-Object {$_.Operand.Name -eq 'set_orthographicSize'}
    if (-not $zoom -or ($kills | Where-Object {$_.Offset -ge $zoom.Offset})) {throw 'Stop stale tweens before restoring zoom.'}
    foreach ($method in @('set_localPosition', 'set_localRotation')) {
        if (-not ($calls | Where-Object {$_.Operand.Name -eq $method})) {throw "Missing camera pose restore: $method"}
    }
    $constructor = $type.Methods | Where-Object {$_.Name -eq '.ctor'}
    if (-not ($constructor.Body.Instructions | Where-Object {$_.OpCode.Name -eq 'call' -and $_.Operand.Name -eq 'get_m_OriginSize'})) {
        throw 'Camera baseline does not use the native pre-burst size.'
    }
    $snapshot = $built.MainModule.Types | Where-Object {$_.Name -eq 'NoteResetSnapshot'}
    $noteRestore = $snapshot.Methods | Where-Object {$_.Name -eq 'Restore'}
    if (-not ($noteRestore.Body.Instructions | Where-Object {$_.OpCode.Name -eq 'call' -and $_.Operand.DeclaringType.Name -eq 'BurstPresentationReset' -and $_.Operand.Name -eq 'Restore'})) {
        throw 'Presentation cleanup is not connected to note reset.'
    }
    [pscustomobject]@{
        Status = 'PASS (compiled-call audit, not an in-game test)'
        CounterZeroTransitions = $zeroCalls.Count
        CounterScope = 'Live scene instances, multi-hit event only'
        CameraTweenTargetsStopped = $kills.Count
        NativeCameraSizeAndPoseRestored = $true
        ConnectedToRewind = $true
    } | ConvertTo-Json
} finally { $built.Dispose() }
