$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$application = Join-Path $repo 'artifacts/publish/AutoStart.exe'
$fixture = Join-Path $repo 'artifacts/test-fixture/AutoStart.Tests.exe'
$fixtureHash = (Get-FileHash -LiteralPath $fixture -Algorithm SHA256).Hash

foreach ($scenario in @('success', 'missing-staged-file', 'invalid-staged-file')) {
    $directory = Join-Path $repo ('artifacts/installer-e2e/' + $scenario + '-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    $target = Join-Path $directory "Önal & test's app.exe"
    $helper = Join-Path $directory 'AutoStart_update_helper.exe'
    $staged = Join-Path $directory 'AutoStart_new.exe'
    $backup = Join-Path $directory 'AutoStart_backup.exe'
    Copy-Item -LiteralPath $fixture -Destination $target
    Copy-Item -LiteralPath $application -Destination $helper
    if ($scenario -eq 'success') { Copy-Item -LiteralPath $fixture -Destination $staged }
    # The helper validates corrupt staging before replacement; it must never pass these bytes to Windows.
    if ($scenario -eq 'invalid-staged-file') { [IO.File]::WriteAllBytes($staged, [byte[]]@(0x4d, 0x5a, 0)) }

    $parent = Start-Process -FilePath $target -ArgumentList '--installer-parent' -WindowStyle Hidden -PassThru
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    while (-not (Test-Path -LiteralPath (Join-Path $directory 'parent-ready.txt'))) {
        if ([DateTime]::UtcNow -gt $deadline) { throw 'Synthetic parent did not start' }
        Start-Sleep -Milliseconds 50
    }
    $installer = Start-Process -FilePath $helper -ArgumentList @('--install-update', ('"' + $target + '"'), $parent.Id) -WindowStyle Hidden -PassThru
    if (-not $installer.WaitForExit(30000)) { throw 'Installer timed out' }
    $expectedExit = if ($scenario -eq 'success') { 0 } else { 1 }
    if ($installer.ExitCode -ne $expectedExit) { throw "Unexpected installer exit: $($installer.ExitCode)" }
    if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $fixtureHash) { throw 'Installed/restored target bytes differ' }
    if ($scenario -eq 'success' -and (Get-FileHash -LiteralPath $backup -Algorithm SHA256).Hash -ne $fixtureHash) { throw 'Backup bytes differ' }
    if ($scenario -ne 'success' -and (Test-Path -LiteralPath $backup)) { throw 'Preflight failure changed backup state' }
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    while (-not (Test-Path -LiteralPath (Join-Path $directory 'restarted.txt'))) {
        if ([DateTime]::UtcNow -gt $deadline) { throw 'Synthetic target was not restarted' }
        Start-Sleep -Milliseconds 50
    }
    if ($scenario -eq 'success' -and (Test-Path -LiteralPath $staged)) { throw 'Successful installation did not clean staging file' }
    if ($scenario -eq 'invalid-staged-file' -and -not (Test-Path -LiteralPath $staged)) { throw 'Failed staging material was discarded' }
    Write-Output "PASS actual published installer: $scenario, parent exit, unchanged/verified target, Unicode path and restart"
}
