param([string]$TestRoot = '')
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'safe-deploy.ps1')
$script:checks = 0
function Assert-StateTest {
    param([bool]$Condition, [string]$Message)
    $script:checks++
    if (-not $Condition) { throw ('State regression: ' + $Message) }
}
if ([string]::IsNullOrWhiteSpace($TestRoot)) { $TestRoot = [IO.Path]::GetTempPath() }
$base = [IO.Path]::GetFullPath($TestRoot)
# Include spaces, brackets and a non-ASCII character without requiring a script BOM.
$name = 'state checks [1] ' + [char]0x0406 + '-' + [Guid]::NewGuid().ToString('N')
$folder = Join-Path $base $name
$statePath = Join-Path (Join-Path $folder 'nested folder') 'launch-state.json'
try {
    Write-PodsViewState -Path $statePath -Value @{ Iteration=0; Active='first'; Previous=$null }
    $record = Get-Content -LiteralPath $statePath -Raw -Encoding UTF8 | ConvertFrom-Json
    Assert-StateTest ([IO.File]::Exists($statePath)) 'first write did not create state'
    Assert-StateTest ($record.Active -eq 'first') 'first record is not valid JSON'
    # This is the path missing from the old C#/WPF smoke tests: overwrite an EXISTING file.
    foreach ($i in 1..5) {
        Write-PodsViewState -Path $statePath -Value @{ Iteration=$i; Active=('run-' + $i); Previous=('run-' + ($i-1)) }
        $record = Get-Content -LiteralPath $statePath -Raw -Encoding UTF8 | ConvertFrom-Json
        Assert-StateTest ($record.Iteration -eq $i) 'existing state was not atomically replaced'
        Assert-StateTest ($record.Previous -eq ('run-' + ($i-1))) 'rollback metadata was lost'
    }
    $before = [Convert]::ToBase64String([IO.File]::ReadAllBytes($statePath))
    $rejected = $false
    try { Write-PodsViewState -Path '   ' -Value @{ Wrong=$true } } catch { $rejected = $true }
    Assert-StateTest $rejected 'blank path accepted'
    $rejected = $false
    try { Write-PodsViewState -Path (Split-Path -Parent $statePath) -Value @{ Wrong=$true } } catch { $rejected = $true }
    Assert-StateTest $rejected 'directory accepted as a state file'
    Assert-StateTest ([Convert]::ToBase64String([IO.File]::ReadAllBytes($statePath)) -eq $before) 'invalid writes changed the valid record'
    if ($env:OS -eq 'Windows_NT') {
        $locked = [IO.File]::Open($statePath, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
        $rejected = $false
        try {
            try { Write-PodsViewState -Path $statePath -Value @{ Iteration=999 } } catch { $rejected = $true }
        } finally { $locked.Dispose() }
        Assert-StateTest $rejected 'locked destination was silently accepted'
        Assert-StateTest ([Convert]::ToBase64String([IO.File]::ReadAllBytes($statePath)) -eq $before) 'failed replacement damaged the old record'
    }
    Assert-StateTest (@(Get-ChildItem -LiteralPath (Split-Path -Parent $statePath) -Filter '*.tmp' -File).Count -eq 0) 'temporary state files leaked'
    Assert-StateTest (@(Get-ChildItem -LiteralPath (Split-Path -Parent $statePath) -Filter '*.previous' -File).Count -eq 0) 'successful replacement backups leaked'
    Write-Host ('PodsView state-write tests: PASS (' + $script:checks + ' checks; actual PowerShell/File.Replace)') -ForegroundColor Green
} finally {
    # Only this isolated test directory is removed; no real app state is touched.
    if (Test-Path -LiteralPath $folder) { Remove-Item -LiteralPath $folder -Recurse -Force }
}
