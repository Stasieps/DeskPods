param([switch]$Rollback)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'safe-deploy.ps1')
$mutex = New-Object Threading.Mutex($false, 'Local\PodsView.SafeUpdate')
$owned = $false
try {
    try { $owned = $mutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $owned = $true }
    if (-not $owned) { throw 'Another PodsView update is running.' }
    $state = Join-Path $env:LOCALAPPDATA 'PodsView\install-state.json'
    if ($Rollback) { Undo-PodsViewDeployment -StatePath $state }
    else { Install-PodsViewCandidate -Source (Split-Path -Parent $PSScriptRoot) -InstallDir (Join-Path $env:LOCALAPPDATA 'Programs\PodsView') -StatePath $state }
    exit 0
} catch {
    Write-Host ('Installation stopped: ' + $_.Exception.Message) -ForegroundColor Red
    exit 1
} finally {
    if ($owned) { $mutex.ReleaseMutex() }
    $mutex.Dispose()
}
