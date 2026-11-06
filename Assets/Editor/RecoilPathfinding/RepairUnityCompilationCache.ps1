$ErrorActionPreference = 'Stop'
$projectPath = [IO.Path]::GetFullPath('D:\unity3d\rts-game')
$libraryPath = [IO.Path]::GetFullPath((Join-Path $projectPath 'Library'))

# Never move compiler outputs while this project's Editor/import workers are using them.
$openProcesses = Get-CimInstance Win32_Process -Filter "name = 'Unity.exe'" | Where-Object {
    $_.CommandLine -and (
        $_.CommandLine.IndexOf($projectPath, [StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $_.CommandLine.IndexOf($projectPath.Replace('\', '/'), [StringComparison]::OrdinalIgnoreCase) -ge 0)
}
if ($openProcesses) {
    throw 'Save your scene and close the rts-game Unity Editor first. No cache files were changed.'
}
if (-not (Test-Path -LiteralPath (Join-Path $projectPath 'Packages\manifest.json'))) {
    throw 'Expected project manifest was not found.'
}

$backupPath = [IO.Path]::GetFullPath((Join-Path $libraryPath ('CompilationCacheBackup-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))))
$moves = foreach ($name in @('Bee', 'APIUpdater', 'ScriptAssemblies')) {
    $source = [IO.Path]::GetFullPath((Join-Path $libraryPath $name))
    $destination = [IO.Path]::GetFullPath((Join-Path $backupPath $name))
    if (-not $source.StartsWith($libraryPath + '\', [StringComparison]::OrdinalIgnoreCase) -or
        -not $destination.StartsWith($backupPath + '\', [StringComparison]::OrdinalIgnoreCase) -or
        -not $backupPath.StartsWith($libraryPath + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Cache move escaped the intended Library directory.'
    }
    if (Test-Path -LiteralPath $source) {
        [pscustomobject]@{ Source = $source; Destination = $destination }
    }
}
if (-not $moves) { Write-Output 'No existing compiler cache to move.'; exit 0 }
New-Item -ItemType Directory -Path $backupPath | Out-Null
foreach ($move in $moves) {
    Move-Item -LiteralPath $move.Source -Destination $move.Destination
}
Write-Output ('Compiler caches backed up to: ' + $backupPath)
Write-Output 'Reopen rts-game in Unity and allow package resolution and script compilation to finish.'
