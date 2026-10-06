Set-StrictMode -Version Latest

function Test-PodsViewBinary {
    param([Parameter(Mandatory=$true)][string]$Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "No executable: $Path" }
    $stream = [IO.File]::OpenRead($Path)
    try {
        if ($stream.Length -lt 1024 -or $stream.ReadByte() -ne 77 -or $stream.ReadByte() -ne 90) { throw "Invalid Windows executable: $Path" }
    } finally { $stream.Dispose() }
}
function Write-PodsViewState {
    param([Parameter(Mandatory=$true)][ValidateNotNullOrEmpty()][string]$Path, $Value)
    if ([string]::IsNullOrWhiteSpace($Path)) { throw 'State path must not be blank.' }
    $fullPath = [IO.Path]::GetFullPath($Path)
    if ([IO.Directory]::Exists($fullPath)) { throw "State path points to a directory: $fullPath" }
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($fullPath)) | Out-Null
    $temporary = $fullPath + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
    # Windows PowerShell can bind $null to String.Empty for a string parameter.
    # File.Replace(source,destination,"") fails with "path is not of a legal form".
    # Give Replace a REAL same-directory backup path, never a null/empty argument.
    $backup = $temporary + '.previous'
    $committed = $false
    try {
        $json = $Value | ConvertTo-Json -Depth 5
        [IO.File]::WriteAllText($temporary, $json, (New-Object Text.UTF8Encoding($false)))
        if ([IO.File]::Exists($fullPath)) {
            [IO.File]::Replace($temporary, $fullPath, $backup)
        } else {
            [IO.File]::Move($temporary, $fullPath)
        }
        $committed = $true
    } catch {
        throw "Could not save launcher state '$fullPath': $($_.Exception.Message)"
    } finally {
        # Never delete the old destination to make a failed write appear successful.
        # Keep a backup after a failed replacement for manual recovery.
        # Cleanup failures after a successful commit must not undo a running app.
        if ([IO.File]::Exists($temporary)) {
            try { [IO.File]::Delete($temporary) } catch { Write-Verbose 'Temporary state cleanup deferred.' }
        }
        if ($committed -and [IO.File]::Exists($backup)) {
            try { [IO.File]::Delete($backup) } catch { Write-Verbose 'Old state backup cleanup deferred.' }
        }
    }
}
function Get-PodsViewProcesses {
    @(Get-Process -Name PodsView -ErrorAction SilentlyContinue)
}
function Stop-PodsViewProcesses {
    foreach ($process in @(Get-PodsViewProcesses)) {
        Stop-Process -Id $process.Id -Force -ErrorAction Stop
        if (-not $process.WaitForExit(5000)) { throw 'The previous process did not exit. Files were not replaced.' }
    }
}
function Start-PodsViewChecked {
    param([string]$Exe)
    Test-PodsViewBinary -Path $Exe
    $process = Start-Process -FilePath $Exe -WorkingDirectory (Split-Path -Parent $Exe) -PassThru
    Start-Sleep -Milliseconds 1500
    if ($process.HasExited) { throw "PodsView exited immediately (code $($process.ExitCode))." }
    return $process
}
function Start-PodsViewCandidate {
    param([string]$Exe, [string]$StatePath)
    Test-PodsViewBinary -Path $Exe
    $old = $null
    $running = @(Get-PodsViewProcesses)
    if ($running.Count -gt 0) { $old = $running[0].Path }
    if (-not $old -and (Test-Path -LiteralPath $StatePath)) {
        $state = Get-Content -LiteralPath $StatePath -Raw | ConvertFrom-Json
        $old = $state.Active
    }
    $started = $null
    try {
        # No running process is stopped until build, parser tests and WPF tests have passed.
        Stop-PodsViewProcesses
        $started = Start-PodsViewChecked -Exe $Exe
        Write-PodsViewState -Path $StatePath -Value @{ Kind='local'; Active=$Exe; Previous=$old }
    } catch {
        if ($started -and -not $started.HasExited) { Stop-Process -Id $started.Id -Force -ErrorAction SilentlyContinue }
        if ($old -and (Test-Path -LiteralPath $old)) {
            Start-Process -FilePath $old -WorkingDirectory (Split-Path -Parent $old)
            Write-Host 'The previous executable has been restarted.' -ForegroundColor Yellow
        }
        throw
    }
}
function Install-PodsViewCandidate {
    param([string]$Source, [string]$InstallDir, [string]$StatePath)
    $sourceFull = [IO.Path]::GetFullPath($Source).TrimEnd('\')
    $destFull = [IO.Path]::GetFullPath($InstallDir).TrimEnd('\')
    if ($sourceFull.Equals($destFull, [StringComparison]::OrdinalIgnoreCase) -or
        $sourceFull.StartsWith($destFull + '\', [StringComparison]::OrdinalIgnoreCase) -or
        $destFull.StartsWith($sourceFull + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Source and installation folders overlap. Extract the download into a separate folder. Nothing was changed.'
    }
    Test-PodsViewBinary -Path (Join-Path $sourceFull 'PodsView.exe')
    $id = (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8)
    $parent = Split-Path -Parent $destFull
    [IO.Directory]::CreateDirectory($parent) | Out-Null
    $stage = Join-Path $parent ('.PodsView-stage-' + $id)
    $backup = Join-Path $parent ('PodsView.rollback-' + $id)
    $oldMoved = $false; $newMoved = $false; $started = $null; $oldRunning = $null
    $processes = @(Get-PodsViewProcesses)
    if ($processes.Count -gt 0) { $oldRunning = $processes[0].Path }
    try {
        [IO.Directory]::CreateDirectory($stage) | Out-Null
        Copy-Item -Path (Join-Path $sourceFull '*') -Destination $stage -Recurse -Force
        Test-PodsViewBinary -Path (Join-Path $stage 'PodsView.exe')
        if ((Get-FileHash -LiteralPath (Join-Path $stage 'PodsView.exe')).Hash -ne (Get-FileHash -LiteralPath (Join-Path $sourceFull 'PodsView.exe')).Hash) { throw 'Staged copy checksum mismatch.' }
        Stop-PodsViewProcesses
        if (Test-Path -LiteralPath $destFull) { Move-Item -LiteralPath $destFull -Destination $backup; $oldMoved = $true }
        Move-Item -LiteralPath $stage -Destination $destFull; $newMoved = $true
        $exe = Join-Path $destFull 'PodsView.exe'
        $started = Start-PodsViewChecked -Exe $exe
        Write-PodsViewState -Path $StatePath -Value @{ Kind='install'; Active=$exe; Previous= $(if ($oldMoved) { Join-Path $backup 'PodsView.exe' } else { $null }) }
    } catch {
        if ($started -and -not $started.HasExited) { Stop-Process -Id $started.Id -Force -ErrorAction SilentlyContinue }
        if ($newMoved -and (Test-Path -LiteralPath $destFull)) { Move-Item -LiteralPath $destFull -Destination ($destFull + '.failed-' + $id) }
        if ($oldMoved -and (Test-Path -LiteralPath $backup)) { Move-Item -LiteralPath $backup -Destination $destFull }
        if ($oldRunning -and (Test-Path -LiteralPath $oldRunning)) { Start-Process -FilePath $oldRunning -WorkingDirectory (Split-Path -Parent $oldRunning) }
        throw
    } finally {
        # Only this attempt's temporary staging folder may be deleted. Previous builds remain intact.
        if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
    }
    # Optional shell integration must not invalidate a successful, running installation.
    try {
        $shell = New-Object -ComObject WScript.Shell
        $menu = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\DeskPods'
        [IO.Directory]::CreateDirectory($menu) | Out-Null
        foreach ($target in @((Join-Path ([Environment]::GetFolderPath('Desktop')) 'DeskPods.lnk'), (Join-Path $menu 'DeskPods.lnk'))) {
            $shortcut = $shell.CreateShortcut($target)
            $shortcut.TargetPath = (Join-Path $destFull 'PodsView.exe')
            $shortcut.WorkingDirectory = $destFull
            # 0.8.40: build-run.ps1 names the icon after the version it built, so a hard-coded
            # filename points at a file that is not there. Use the versioned icon that was
            # installed, and fall back to the icon inside the executable.
            $icon = Get-ChildItem -LiteralPath $destFull -Filter 'deskpods-*.ico' -File -ErrorAction SilentlyContinue | Sort-Object Name -Descending | Select-Object -First 1
            $iconPath = if ($icon) { $icon.FullName } else { Join-Path $destFull 'PodsView.exe' }
            $shortcut.IconLocation = $iconPath + ',0'
            $shortcut.Save()
        }
    } catch { Write-Warning ('Installed successfully; shortcut creation failed: ' + $_.Exception.Message) }
    Write-Host "Installed to $destFull. Previous installation retained at $backup" -ForegroundColor Green
}
function Undo-PodsViewDeployment {
    param([string]$StatePath)
    if (-not (Test-Path -LiteralPath $StatePath)) { throw 'No saved previous build for this launcher.' }
    $state = Get-Content -LiteralPath $StatePath -Raw | ConvertFrom-Json
    if (-not $state.Previous) { throw 'There was no previous executable to restore.' }
    Test-PodsViewBinary -Path $state.Previous
    if ($state.Kind -eq 'local') {
        Stop-PodsViewProcesses
        try { $null = Start-PodsViewChecked -Exe $state.Previous }
        catch { if (Test-Path -LiteralPath $state.Active) { Start-Process -FilePath $state.Active -WorkingDirectory (Split-Path -Parent $state.Active) }; throw }
        Write-PodsViewState -Path $StatePath -Value @{ Kind='local'; Active=$state.Previous; Previous=$state.Active }
    } elseif ($state.Kind -eq 'install') {
        $install = Split-Path -Parent $state.Active
        $previous = Split-Path -Parent $state.Previous
        $parked = $install + '.rollback-' + [Guid]::NewGuid().ToString('N').Substring(0,8)
        Stop-PodsViewProcesses
        $movedCurrent = $false; $movedPrevious = $false
        try {
            Move-Item -LiteralPath $install -Destination $parked; $movedCurrent = $true
            Move-Item -LiteralPath $previous -Destination $install; $movedPrevious = $true
            $null = Start-PodsViewChecked -Exe (Join-Path $install 'PodsView.exe')
            Write-PodsViewState -Path $StatePath -Value @{ Kind='install'; Active=(Join-Path $install 'PodsView.exe'); Previous=(Join-Path $parked 'PodsView.exe') }
        } catch {
            Stop-PodsViewProcesses
            if ($movedPrevious) { Move-Item -LiteralPath $install -Destination $previous }
            if ($movedCurrent) { Move-Item -LiteralPath $parked -Destination $install }
            if (Test-Path -LiteralPath $state.Active) { Start-Process -FilePath $state.Active -WorkingDirectory $install }
            throw
        }
    } else { throw 'Unknown rollback record. No files changed.' }
    Write-Host 'Previous build restored. Settings were not overwritten.' -ForegroundColor Green
}
