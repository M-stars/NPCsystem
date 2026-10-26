param(
    [ValidateSet('EditMode', 'PlayMode', 'Build', 'Player', 'All')]
    [string]$Phase = 'All',
    [string]$ProjectPath = (Split-Path $PSScriptRoot -Parent),
    [string]$ReportTag = (Get-Date -Format 'yyyyMMdd-HHmmss'),
    [string]$TestFilter = ''
)

$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath($ProjectPath)
$reportRoot = Join-Path $projectRoot 'Docs/Validation'
if ($ReportTag -notmatch '^[A-Za-z0-9_-]+$') { throw 'ReportTag may contain only letters, digits, _ and -.' }
New-Item -ItemType Directory -Path $reportRoot -Force | Out-Null

function Invoke-EditorCommand([string[]]$CommandArguments) {
    $responseText = (& unity command @CommandArguments --caller plugin --skill unity-cli --project-path $projectRoot --format json) -join "`n"
    $exitCode = $LASTEXITCODE
    if ([string]::IsNullOrWhiteSpace($responseText)) { throw "Unity CLI returned no JSON (exit $exitCode)." }
    $response = $responseText | ConvertFrom-Json
    # CLI commands may return an envelope, or JSON serialized inside a JSON string.
    for ($i = 0; $i -lt 4 -and $response -is [string]; $i++) { $response = $response | ConvertFrom-Json }
    if ($exitCode -ne 0 -or ($response.PSObject.Properties['success'] -and -not $response.success)) {
        throw "Unity command failed: $($response | ConvertTo-Json -Depth 8 -Compress)"
    }
    if ($response.PSObject.Properties['data']) { $response = $response.data.result }
    for ($i = 0; $i -lt 4 -and $response -is [string]; $i++) { $response = $response | ConvertFrom-Json }
    if ($null -eq $response) { throw 'Unity command returned an empty result.' }
    return $response
}

function Save-Report([object]$Report, [string]$Kind) {
    $path = Join-Path $reportRoot "$Kind-$ReportTag.json"
    $Report | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $path -Encoding UTF8
    return $path
}

function Prepare-Editor {
    $refresh = Invoke-EditorCommand @('eval',
        # Quote-free C#: PowerShell 5.1 splits native arguments at embedded double quotes.
        'if (UnityEditor.EditorApplication.isPlayingOrWillChangePlaymode) throw new System.InvalidOperationException(nameof(UnityEditor.EditorApplication.isPlayingOrWillChangePlaymode)); UnityEditor.AssetDatabase.Refresh(UnityEditor.ImportAssetOptions.ForceSynchronousImport); return true;')
    if (!$refresh.success) { throw 'Could not refresh project assets before validation.' }
    $deadline = (Get-Date).AddMinutes(3)
    $readySamples = 0
    do {
        Start-Sleep -Seconds 2
        $state = $null
        try {
            $state = Invoke-EditorCommand @('eval',
                'return new { compiling = UnityEditor.EditorApplication.isCompiling, updating = UnityEditor.EditorApplication.isUpdating, failed = UnityEditor.EditorUtility.scriptCompilationFailed };')
        }
        catch { $readyError = $_.Exception.Message }
        if ($state -and $state.success -and !$state.result.compiling -and !$state.result.updating) {
            if ($state.result.failed) { throw 'Unity script compilation failed. Inspect the Console before running tests or builds.' }
            $readySamples++
            if ($readySamples -ge 2) { return }
        }
        else { $readySamples = 0 }
    } while ((Get-Date) -lt $deadline)
    throw "Editor compilation did not become ready within 3 minutes. $readyError"
}

function Reset-TestDomain {
    # With DisableDomainReload enabled, consecutive filtered PlayMode runs can reuse
    # Unity Test Framework's previous test tree and execute zero tests. Clear transient
    # scripting state without changing the project's Enter Play Mode settings.
    $reload = Invoke-EditorCommand @('eval',
        # The domain key uses nameof so the native command line contains no double quotes.
        'if (UnityEditor.EditorApplication.isPlayingOrWillChangePlaymode) throw new System.InvalidOperationException(nameof(UnityEditor.EditorApplication.isPlayingOrWillChangePlaymode)); System.AppDomain.CurrentDomain.SetData(nameof(SocialSimulation.Core.SimulationWorld), true); UnityEditor.EditorUtility.RequestScriptReload(); return true;')
    if (!$reload.success) { throw 'Could not request a fresh test domain.' }
    $deadline = (Get-Date).AddMinutes(2)
    do {
        Start-Sleep -Seconds 2
        try {
            $state = Invoke-EditorCommand @('eval',
                'return new { pending = System.AppDomain.CurrentDomain.GetData(nameof(SocialSimulation.Core.SimulationWorld)) != null, compiling = UnityEditor.EditorApplication.isCompiling, updating = UnityEditor.EditorApplication.isUpdating };')
            if ($state.success -and !$state.result.pending -and !$state.result.compiling -and !$state.result.updating) { return }
        }
        catch { $reloadError = $_.Exception.Message }
    } while ((Get-Date) -lt $deadline)
    throw "Editor test domain did not become ready within 2 minutes. $reloadError"
}

function Invoke-Tests([string]$Mode) {
    if ($Mode -eq 'PlayMode') { Reset-TestDomain }
    $modeArgument = if ($Mode -eq 'EditMode') { 'editor' } else { 'playmode' }
    $testArguments = @('run_tests', '--mode', $modeArgument, '--async_tests', 'true')
    if ($TestFilter) { $testArguments += @('--filter', $TestFilter) }
    $started = Invoke-EditorCommand $testArguments
    if ($started.result -ne 'running') { throw "Tests did not start: $($started | ConvertTo-Json -Compress)" }
    $deadline = (Get-Date).AddMinutes(20)
    do {
        Start-Sleep -Seconds 3
        $report = Invoke-EditorCommand @('test_status')
        if ($report.status -eq 'completed' -or $report.status -eq 'failed') {
            $path = Save-Report $report $Mode
            Write-Output "$Mode`: $($report.summary.passed)/$($report.summary.total) passed; $path"
            if ($report.status -ne 'completed' -or $report.summary.total -lt 1 -or
                $report.summary.passed -ne $report.summary.total -or
                $report.summary.failed -gt 0 -or $report.summary.inconclusive -gt 0) {
                throw "$Mode validation failed; see $path"
            }
            return
        }
    } while ((Get-Date) -lt $deadline)
    throw "$Mode exceeded 20 minutes. Inspect the running Editor before starting another test run."
}

function Invoke-PlayerBuild {
    # PowerShell 5.1 strips embedded quotes in native arguments; backslash-quote survives
    # Windows argv parsing, so the Editor-side JSON array binder receives real quotes.
    $scenePaths = '[\"Assets/SocialSimulation/Domains/Combined/CombinedSocialWorld.unity\",\"Assets/SocialSimulation/Domains/Combined/IndependentCommunity.unity\"]'
    $started = Invoke-EditorCommand @('build', '--confirm', 'true', '--target', 'StandaloneWindows64',
        '--outputPath', 'Builds/Windows/SocialSimulationDemo.exe', '--scenes', $scenePaths)
    if ($started.status -ne 'queued') { throw 'Player build was not queued.' }
    $deadline = (Get-Date).AddMinutes(15)
    do {
        Start-Sleep -Seconds 3
        $report = Invoke-EditorCommand @('build_status')
        if ($report.status -eq 'completed') {
            $path = Save-Report $report 'WindowsBuild'
            Write-Output "Windows build: $($report.result), errors=$($report.totalErrors), warnings=$($report.totalWarnings); $path"
            if ($report.buildId -ne $started.buildId -or $report.result -ne 'Succeeded' -or $report.totalErrors -ne 0) {
                throw "Player build failed or was replaced by another build; see $path"
            }
            return
        }
    } while ((Get-Date) -lt $deadline)
    throw 'Player build exceeded 15 minutes. Inspect the Editor before retrying.'
}

function Invoke-PlayerSmoke {
    $executable = Join-Path $projectRoot 'Builds/Windows/SocialSimulationDemo.exe'
    $logPath = Join-Path $reportRoot "WindowsPlayer-$ReportTag.log"
    $jsonPath = Join-Path $reportRoot "WindowsPlayer-$ReportTag.json"
    if (!(Test-Path -LiteralPath $executable)) { throw 'Build the Windows Player first.' }
    if ((Test-Path -LiteralPath $jsonPath) -or (Test-Path -LiteralPath $logPath)) {
        throw 'This Player report tag already exists. Use a new ReportTag to preserve earlier evidence.'
    }
    # Start-Process joins arguments into one native command line; quote paths for projects with spaces.
    $arguments = @('-batchmode', '-nographics', '-logFile', ('"' + $logPath + '"'),
        '-socialSmokeOutput', ('"' + $jsonPath + '"'))
    $player = Start-Process -FilePath $executable -ArgumentList $arguments -WindowStyle Hidden -PassThru
    $deadline = (Get-Date).AddMinutes(3)
    while (!$player.WaitForExit(1000)) {
        if ((Get-Date) -ge $deadline) {
            $player.Kill()
            throw "Player smoke exceeded 3 minutes; see $logPath"
        }
    }
    if (!(Test-Path -LiteralPath $jsonPath)) { throw "Player produced no report (exit $($player.ExitCode)); see $logPath" }
    $report = Get-Content -LiteralPath $jsonPath -Raw | ConvertFrom-Json
    if ($player.ExitCode -ne 0 -or !$report.Passed -or $report.RuntimeErrorCount -ne 0 -or $report.Scenes.Count -ne 2) {
        throw "Player smoke failed; see $jsonPath and $logPath"
    }
    foreach ($scene in $report.Scenes) {
        if (!$scene.SaveRestored -or $scene.TravelStatus -ne 'Succeeded' -or $scene.Npcs -lt 1 -or
            $scene.AgentsOnMesh -ne $scene.Npcs -or $scene.NavMeshVertices -lt 1) {
            throw "Player scene validation failed for $($scene.Scene); see $jsonPath"
        }
    }
    Write-Output "Player smoke: 2 scenes passed navigation and save/load with no runtime errors; $jsonPath"
}

Push-Location $projectRoot
try {
    if ($Phase -ne 'Player') { Prepare-Editor }
    if ($Phase -eq 'All' -or $Phase -eq 'EditMode') { Invoke-Tests 'EditMode' }
    if ($Phase -eq 'All' -or $Phase -eq 'PlayMode') { Invoke-Tests 'PlayMode' }
    if ($Phase -eq 'All' -or $Phase -eq 'Build') { Invoke-PlayerBuild }
    if ($Phase -eq 'All' -or $Phase -eq 'Player') { Invoke-PlayerSmoke }
}
finally { Pop-Location }
