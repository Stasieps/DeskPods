param(
    [ValidateSet('start','install','rollback','rollback-install','sdk','uninstall')][string]$Action = 'start',
    [string]$ExpectedVersion = '0.8.41'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$source = Test-Path -LiteralPath (Join-Path $root 'src\PodsView\PodsView.csproj') -PathType Leaf
try {
    switch ($Action) {
        'start' {
            if ($source) { & (Join-Path $PSScriptRoot 'build-run.ps1') -ExpectedVersion $ExpectedVersion }
            else {
                $exe = Join-Path $root 'PodsView.exe'
                if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw 'Incomplete package: no source project or executable.' }
                Start-Process -FilePath $exe -WorkingDirectory $root | Out-Null
                exit 0
            }
        }
        'install' {
            if ($source) { & (Join-Path $PSScriptRoot 'build-run.ps1') -Install -ExpectedVersion $ExpectedVersion }
            else { & (Join-Path $PSScriptRoot 'install-release.ps1') }
        }
        'rollback' {
            if (-not $source) { throw 'For a local rollback, open START.cmd in the original source folder. Installed rollback is option 4.' }
            & (Join-Path $PSScriptRoot 'build-run.ps1') -Rollback
        }
        'rollback-install' { & (Join-Path $PSScriptRoot 'install-release.ps1') -Rollback }
        'sdk' {
            if (Get-Command dotnet -ErrorAction SilentlyContinue) {
                $sdks = @(& dotnet --list-sdks)
                if ($LASTEXITCODE -eq 0 -and @($sdks | Where-Object { $_ -match '^8\.' }).Count) {
                    Write-Host '.NET 8 SDK is already installed.'; exit 0
                }
            }
            if (-not (Get-Command winget -ErrorAction SilentlyContinue)) { throw 'winget is unavailable. Install .NET 8 SDK from https://dotnet.microsoft.com/download/dotnet/8.0 and reopen START.cmd.' }
            & winget install --id Microsoft.DotNet.SDK.8 -e --accept-source-agreements --accept-package-agreements
        }
        'uninstall' { & (Join-Path $PSScriptRoot 'uninstall.ps1') }
    }
    if ($null -ne $LASTEXITCODE) { exit $LASTEXITCODE }
    exit 0
} catch {
    Write-Host ('STOPPED: ' + $_.Exception.Message) -ForegroundColor Red
    exit 1
}
