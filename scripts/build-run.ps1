param([switch]$Rollback, [switch]$Install, [switch]$BuildOnly, [string]$ExpectedVersion = '')
$ErrorActionPreference = 'Stop'
# Keep native .NET output and PowerShell decoding on the same encoding.
$utf8 = New-Object System.Text.UTF8Encoding($false)
[Console]::InputEncoding = $utf8
[Console]::OutputEncoding = $utf8
$OutputEncoding = $utf8
$env:DOTNET_CLI_UI_LANGUAGE = 'en-US'
$env:VSLANG = '1033'
$sourceRoot = Split-Path -Parent $PSScriptRoot

. (Join-Path $PSScriptRoot 'safe-deploy.ps1')
$localState = Join-Path $sourceRoot '.build\launch-state.json'
$installState = Join-Path $env:LOCALAPPDATA 'PodsView\install-state.json'
$mutex = New-Object Threading.Mutex($false, 'Local\PodsView.SafeUpdate')
$owned = $false
$deploymentStarted = $false
$stage = 'preparation'
Push-Location $sourceRoot
try {
    try { $owned = $mutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $owned = $true }
    if (-not $owned) { throw 'Another PodsView update is already running.' }
    if ($Rollback) { $deploymentStarted = $true; $stage = 'rollback'; Undo-PodsViewDeployment -StatePath $localState; exit 0 }
    Write-Host 'DeskPods - build from source and verify' -ForegroundColor Cyan
    Write-Host ('Source folder: ' + $sourceRoot)
    $stage = 'launcher state-write regression tests'
    Write-Host '0/3: checking first and repeated state-file writes' -ForegroundColor Cyan
    & (Join-Path $PSScriptRoot 'test-safe-deploy.ps1') -TestRoot (Join-Path $sourceRoot '.build')
    $project = Join-Path $sourceRoot 'src\PodsView\PodsView.csproj'
    if (-not (Test-Path -LiteralPath $project)) { throw 'Extract the complete source archive first.' }
    [xml]$xml = Get-Content -LiteralPath $project -Raw
    $version = [string]$xml.Project.PropertyGroup.Version
    if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Invalid project version.' }
    if ($ExpectedVersion -and $version -ne $ExpectedVersion) { throw "Expected $ExpectedVersion, but this folder contains $version. Extract into a new folder." }
    $dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
    if (-not $dotnetCommand) { throw 'Install the .NET 8 SDK using START.cmd option 5, then run this launcher again. The old app has not been stopped.' }
    $script:dotnet = $dotnetCommand.Source
    $sdks = @(& $script:dotnet --list-sdks)
    if ($LASTEXITCODE -ne 0 -or -not @($sdks | Where-Object { $_ -match '^([8-9]|[1-9][0-9]+)\.' }).Count) { throw '.NET SDK 8 or newer is required.' }
    foreach ($process in @(Get-PodsViewProcesses)) {
        if ($process.Path -and $process.Path.StartsWith((Join-Path $sourceRoot 'src\'), [StringComparison]::OrdinalIgnoreCase)) {
            throw 'The running app uses this source folder. Extract the update into a NEW folder so the old build remains intact.'
        }
    }
    function Invoke-DotnetChecked {
        param([string[]]$Arguments, [string]$ExpectedMarker = '')
        $seenExpectedMarker = [string]::IsNullOrEmpty($ExpectedMarker)
        & $script:dotnet @Arguments | ForEach-Object {
            $line = [string]$_
            Write-Host $line
            if ($ExpectedMarker -and $line.Contains($ExpectedMarker)) { $seenExpectedMarker = $true }
        }
        if ($LASTEXITCODE -ne 0) { throw "dotnet failed with exit code $LASTEXITCODE. Nothing has replaced the running app." }
        if (-not $seenExpectedMarker) { throw 'The expected test revision did not execute. The running app has not been replaced.' }
    }
    $id = $version + '-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8)
    $candidate = Join-Path $sourceRoot ('.build\candidates\' + $id)
    $qa = Join-Path $sourceRoot ('.build\qa\' + $id)
    [IO.Directory]::CreateDirectory($candidate) | Out-Null
    # Each attempt has fresh output directories. Force compilation even when an
    # extracted source file has an older timestamp than a previous local build.
    # Execute the exact output DLL, not dotnet run's inferred/cached launch target.
    $testRoot = Join-Path $sourceRoot ('.build\test-runs\' + $id)
    $coreOutput = Join-Path $testRoot 'core'
    $uiOutput = Join-Path $testRoot 'ui'
    $coreProject = Join-Path $sourceRoot 'tests\PodsView.ParserSmoke\PodsView.ParserSmoke.csproj'
    $uiProject = Join-Path $sourceRoot 'tests\PodsView.UiSmoke\PodsView.UiSmoke.csproj'
    $stage = 'clean build of core tests'
    Write-Host '1/3: rebuilding and running core tests' -ForegroundColor Cyan
    Invoke-DotnetChecked -Arguments @('build',$coreProject,'-c','Release','--no-incremental','--output',$coreOutput)
    $coreDll = Join-Path $coreOutput 'PodsView.ParserSmoke.dll'
    if (-not (Test-Path -LiteralPath $coreDll -PathType Leaf)) { throw 'The fresh core test binary was not produced.' }
    $stage = 'core tests'
    Invoke-DotnetChecked -Arguments @($coreDll) -ExpectedMarker 'PodsView parser tests: 0.8.41 / in-case-r1'
    $runtime = if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'win-arm64' } else { 'win-x64' }
    $stage = 'clean build of WPF tests'
    Write-Host '2/3: rebuilding and running WPF tests' -ForegroundColor Cyan
    # Both executable projects are self-contained. Do not force a conflicting
    # project-reference override; the SDK's own compatibility check stays active.
    # PublishSingleFile remains each project's own setting: false for the harness,
    # true for the application. No bundle is produced by this build-only stage.
    Invoke-DotnetChecked -Arguments @('build',$uiProject,'-c','Release','-r',$runtime,'--self-contained','true','--no-incremental','--output',$uiOutput)
    $uiDll = Join-Path $uiOutput 'PodsView.UiSmoke.dll'
    if (-not (Test-Path -LiteralPath $uiDll -PathType Leaf)) { throw 'The fresh WPF test binary was not produced.' }
    $stage = 'WPF tests'
    Invoke-DotnetChecked -Arguments @($uiDll,$qa) -ExpectedMarker 'DeskPods tray r4: approved logo and WPF resources verified'
    $stage = 'application publish'
    Write-Host '3/3: publishing the candidate' -ForegroundColor Cyan
    Invoke-DotnetChecked -Arguments @('publish',$project,'-c','Release','-r',$runtime,'--self-contained','true','-p:PublishSingleFile=true','-p:IncludeNativeLibrariesForSelfExtract=true','-p:EnableCompressionInSingleFile=true','-p:PublishTrimmed=false','-o',$candidate)
    $stage = 'published executable verification'
    $exe = Join-Path $candidate 'PodsView.exe'
    Test-PodsViewBinary -Path $exe
    $actual = [Diagnostics.FileVersionInfo]::GetVersionInfo($exe).ProductVersion
    if (-not $actual -or -not ($actual -eq $version -or $actual.StartsWith($version + '+') -or $actual -eq ($version + '.0'))) { throw "The built executable reports unexpected version '$actual'." }
    $stage = 'release packaging'
    Copy-Item -LiteralPath (Join-Path $sourceRoot 'src\PodsView\Assets\deskpods.ico') -Destination (Join-Path $candidate ('deskpods-' + $version + '.ico')) -Force
    [IO.Directory]::CreateDirectory((Join-Path $candidate 'scripts')) | Out-Null
    foreach ($file in @('START.cmd','README.md','LICENSE')) {
        Copy-Item -LiteralPath (Join-Path $sourceRoot $file) -Destination $candidate -Force
    }
    foreach ($file in @('uninstall.ps1','install-release.ps1','safe-deploy.ps1','launcher.ps1')) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot $file) -Destination (Join-Path $candidate 'scripts') -Force
    }
    $release = Join-Path $sourceRoot ('.build\releases\' + $id)
    [IO.Directory]::CreateDirectory((Split-Path -Parent $release)) | Out-Null
    Move-Item -LiteralPath $candidate -Destination $release
    $stage = 'saving verified release state'
    Write-PodsViewState -Path (Join-Path $sourceRoot '.build\last-verified.json') -Value @{ Version=$version; Directory=$release; Qa=$qa }
    if ($BuildOnly) { Write-Host "Verified build: $release"; exit 0 }
    $stage = 'application start or installation'
    $deploymentStarted = $true
    if ($Install) { Install-PodsViewCandidate -Source $release -InstallDir (Join-Path $env:LOCALAPPDATA 'Programs\PodsView') -StatePath $installState }
    else { Start-PodsViewCandidate -Exe (Join-Path $release 'PodsView.exe') -StatePath $localState }
    Write-Host 'Done. START.cmd: option 3 for local rollback, option 4 for installed rollback.' -ForegroundColor Green
    exit 0
} catch {
    Write-Host ''
    Write-Host ('STOPPED during ' + $stage + ': ' + $_.Exception.Message) -ForegroundColor Red
    if (-not $deploymentStarted) { Write-Host 'The running app has not been stopped or replaced.' -ForegroundColor Yellow }
    Write-Host 'This stage was NOT bypassed. Keep this window if reporting an error.' -ForegroundColor Yellow
    exit 1
} finally {
    Pop-Location
    if ($owned) { $mutex.ReleaseMutex() }
    $mutex.Dispose()
}
