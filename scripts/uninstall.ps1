$ErrorActionPreference = 'Stop'
try {
    Get-Process -Name 'PodsView' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 400

    $runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    Remove-ItemProperty -Path $runKey -Name 'PodsView' -ErrorAction SilentlyContinue

    $desktopShortcut = Join-Path ([Environment]::GetFolderPath('Desktop')) 'PodsView.lnk'
    $startMenuDir = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\PodsView'
    Remove-Item -Force $desktopShortcut -ErrorAction SilentlyContinue
    Remove-Item -Recurse -Force $startMenuDir -ErrorAction SilentlyContinue

    $installDir = Join-Path $env:LOCALAPPDATA 'Programs\PodsView'
    Remove-Item -Recurse -Force $installDir -ErrorAction SilentlyContinue

    Remove-Item -LiteralPath (Join-Path ([Environment]::GetFolderPath('Desktop')) 'DeskPods.lnk') -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\DeskPods') -Recurse -Force -ErrorAction SilentlyContinue

    Write-Host 'DeskPods was removed. Settings and logs were kept in LocalAppData\PodsView.' -ForegroundColor Green
    exit 0
}
catch {
    Write-Host $_.Exception.Message -ForegroundColor Red
    exit 1
}
