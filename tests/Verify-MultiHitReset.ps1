param(
    [string]$GameRoot = $env:MUSEDASH_GAME_ROOT,
    [string]$ModPath = (Join-Path $PSScriptRoot '..\artifacts\mod\MuseDashPracticePrototype.dll')
)
$ErrorActionPreference = 'Stop'
[void][Reflection.Assembly]::LoadFrom((Join-Path $GameRoot 'MelonLoader\net6\Mono.Cecil.dll'))
$native = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $GameRoot 'MelonLoader\Dependencies\Il2CppAssemblyGenerator\Cpp2IL\cpp2il_out\Assembly-CSharp.dll'))
$proxy = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $GameRoot 'MelonLoader\Il2CppAssemblies\Assembly-CSharp.dll'))
$built = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($ModPath)
try {
    $nativeType = $native.MainModule.Types | Where-Object {$_.FullName -eq 'MultHitEnemyController'}
    $proxyType = $proxy.MainModule.Types | Where-Object {$_.FullName -eq 'Il2Cpp.MultHitEnemyController'}
    $snapshotType = $built.MainModule.Types | Where-Object {$_.FullName -eq 'MuseDashPracticePrototype.NoteResetSnapshot'}
    $capture = $snapshotType.Methods | Where-Object {$_.Name -eq 'CaptureComponent'}
    $restore = $snapshotType.Methods | Where-Object {$_.Name -eq 'Restore'}
    $capturedNames = @($capture.Body.Instructions | Where-Object {$_.OpCode.Name -eq 'ldstr'} | ForEach-Object {$_.Operand})
    $fields = @($nativeType.Fields | Where-Object {-not $_.IsStatic})
    if ($fields.Count -ne 8) {throw 'Native multi-hit field layout differs from the inspected version.'}
    foreach ($field in $fields) {
        if ($field.Name -notin $capturedNames) {throw "Uncaptured native multi-hit field: $($field.Name)"}
        $property = $proxyType.Properties | Where-Object {$_.Name -eq $field.Name}
        if (-not $property.GetMethod -or -not $property.SetMethod) {throw "Multi-hit field is not readable/writable: $($field.Name)"}
    }
    $restoreCalls = @($restore.Body.Instructions | Where-Object {$_.OpCode.Name -eq 'call'} | ForEach-Object {$_.Operand.FullName})
    foreach ($name in @('isMulHitEnding', 'isBanEmptyAction', 'hitCount', 'isHitting', 'm_HittingCameraSequence')) {
        $expected = "Il2Cpp.MultHitEnemyController::set_$name("
        if (-not ($restoreCalls | Where-Object {$_.Contains($expected)})) {throw "Missing shared multi-hit reset: $name"}
    }
    $manager = $proxy.MainModule.Types | Where-Object {$_.Name -eq 'CoroutineManager'}
    $delays = @($manager.Methods | Where-Object {$_.Name -eq 'Delay' -and $_.Parameters[0].ParameterType.FullName -eq 'Il2CppSystem.Action'})
    if ($delays.Count -ne 3) {throw 'CoroutineManager.Delay overloads changed.'}
    $practice = $built.MainModule.Types | Where-Object {$_.FullName -eq 'MuseDashPracticePrototype.PracticeMod'}
    $track = $practice.Methods | Where-Object {$_.Name -eq 'TrackMultiHitDelay'}
    $ownerCast = @($track.Body.Instructions | Where-Object {$_.OpCode.Name -eq 'call' -and $_.Operand.FullName -like '*TryCast<Il2Cpp.MultHitEnemyController/__c>*'})
    if ($ownerCast.Count -ne 1) {throw 'Delay tracking is not restricted to native multi-hit callbacks.'}
    $initialize = $practice.Methods | Where-Object {$_.Name -eq 'OnInitializeMelon'}
    $guards = @($initialize.Body.Instructions | Where-Object {$_.OpCode.Name -eq 'call' -and $_.Operand.Name -eq 'PatchGuard'}).Count
    if ($guards -ne 7) {throw 'Persistence guard calls changed.'}
    [pscustomobject]@{
        Status = 'PASS (static metadata audit, not an in-game test)'
        NativeInstanceFieldsCovered = $fields.Count
        SharedModeFlagsReset = 4
        DelayOverloads = $delays.Count
        NativeMultiHitCallbackOwnerVerified = $true
        PersistenceGuards = $guards
    } | ConvertTo-Json
} finally {
    $native.Dispose()
    $proxy.Dispose()
    $built.Dispose()
}
