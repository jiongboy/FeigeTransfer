$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
# $task is supplied as JSON data by the signed application, never as executable text from a release.
function Save-Result([string]$stage, [int]$code) {
    $result = @{ version=$task.expectedVersion; build=$task.expectedBuild; stage=$stage; code=$code }
    $temporary = $task.resultPath + '.tmp'
    [IO.File]::WriteAllText($temporary, ($result | ConvertTo-Json -Compress), (New-Object Text.UTF8Encoding($false)))
    Move-Item -LiteralPath $temporary -Destination $task.resultPath -Force
}
function Verify-Package {
    if (!$task.publishers -or $task.publishers.Count -lt 1 -or $task.publishers.Count -gt 8 -or
        @($task.publishers | Where-Object { $_ -notmatch '^[A-Fa-f0-9]{64}$' }).Count -gt 0) { throw 'Invalid publisher pin configuration' }
    $file = Get-Item -LiteralPath $task.packagePath
    if ($file.Length -ne $task.size -or (Get-FileHash -LiteralPath $task.packagePath -Algorithm SHA256).Hash -ne $task.sha256) { throw 'Package changed' }
    . $task.verifierPath
    Test-FeigePinnedAuthenticode -Path $task.packagePath -Pins $task.publishers | Out-Null
}
# After the installer finishes, the new executable can still be briefly locked. Retry,
# then fall back to shell activation so the user gets a running window back.
function Start-FeigeApp([string]$Path) {
    $directory = [IO.Path]::GetDirectoryName($Path)
    for ($attempt = 0; $attempt -lt 10; $attempt++) {
        try {
            $process = Start-Process -FilePath $Path -WorkingDirectory $directory -PassThru -ErrorAction Stop
            if ($process) { return $true }
        } catch {
            Start-Sleep -Milliseconds 600
        }
        Start-Sleep -Milliseconds 400
    }
    try { Start-Process -FilePath 'explorer.exe' -ArgumentList ('"' + $Path + '"') -ErrorAction Stop; return $true } catch { return $false }
}
$installed = $false
# Startup marker: distinguishes "helper never ran" from "verification failed".
try { [IO.File]::WriteAllText($task.readyPath + '.started', 'started') } catch { }
try {
    Verify-Package
    [IO.File]::WriteAllText($task.readyPath, 'ready')
    $deadline = [DateTime]::UtcNow.AddSeconds(90)
    do {
        if (Test-Path -LiteralPath $task.cancelPath) { Save-Result 'cancelled' 0; exit 0 }
        $old = Get-Process -Id $task.processId -ErrorAction SilentlyContinue
        if (!$old -or $old.StartTime.ToUniversalTime().Ticks -ne $task.processStartTicks) { break }
        if ([DateTime]::UtcNow -ge $deadline) { Save-Result 'exit-timeout' 1; exit 1 }
        Start-Sleep -Milliseconds 250
    } while ($true)
    if (Test-Path -LiteralPath $task.cancelPath) { Save-Result 'cancelled' 0; exit 0 }
    if (!(Test-Path -LiteralPath $task.commitPath)) { Save-Result 'exit-not-committed' 1; exit 1 }
    Verify-Package
    Save-Result 'installing' 0
    $arguments = @('/SP-', '/SILENT', '/NORESTART', '/NOCLOSEAPPLICATIONS', '/NOFORCECLOSEAPPLICATIONS', '/NORESTARTAPPLICATIONS')
    $installer = Start-Process -FilePath $task.packagePath -ArgumentList $arguments -Wait -PassThru
    if ($installer.ExitCode -ne 0) {
        Save-Result 'installation-failed' $installer.ExitCode
        if (!(Start-FeigeApp $task.applicationPath)) { Save-Result 'installation-failed-restart-failed' $installer.ExitCode }
        exit 1
    }
    $installed = $true
    Save-Result 'installed-awaiting-restart' 0
    if (!(Start-FeigeApp $task.applicationPath)) { Save-Result 'installed-restart-failed' 0 }
    exit 0
}
catch {
    # Never overwrite a confirmed installation with a later launch failure.
    if ($installed) { Save-Result 'installed-awaiting-restart' 0; exit 0 }
    try { [IO.File]::WriteAllText($task.readyPath + '.error', $_.Exception.GetType().Name + ': ' + $_.Exception.Message) } catch { }
    Save-Result 'handoff-failed' 1
    $old = Get-Process -Id $task.processId -ErrorAction SilentlyContinue
    if (!$old) { Start-FeigeApp $task.applicationPath | Out-Null }
    exit 1
}
