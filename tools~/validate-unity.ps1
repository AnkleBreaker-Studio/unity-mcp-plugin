param(
    [Parameter(Mandatory = $true)][string]$EditorPath,
    [Parameter(Mandatory = $true)][string]$ProjectPath,
    [ValidateSet('Queue', 'QueueAdmission', 'ResultRetention', 'Health', 'Monitoring', 'Execution', 'Dashboard', 'Packages', 'Testing', 'TestResults', 'TestPersistence', 'RequestShutdown', 'Serialization', 'Undo', 'RequestInput', 'RequestBody', 'HttpDiagnostics', 'EditorCapture', 'GraphicsCapture', 'AssetPreview', 'MeshMetadata')][string]$Suite = 'Queue'
)
$ErrorActionPreference = 'Stop'
if (!(Test-Path -LiteralPath $EditorPath -PathType Leaf)) { throw "Unity executable not found: $EditorPath" }
$pluginRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path.Replace('\', '/')
$projectRoot = [System.IO.Path]::GetFullPath($ProjectPath)
if (Test-Path -LiteralPath (Join-Path $projectRoot 'Assets')) {
    if (!(Test-Path -LiteralPath (Join-Path $projectRoot '.unity-mcp-validation'))) {
        throw 'Use a new validation directory; an existing user project must not be overwritten.'
    }
}
New-Item -ItemType Directory -Force -Path (Join-Path $projectRoot 'Assets/Editor'), (Join-Path $projectRoot 'Packages'), (Join-Path $projectRoot 'ProjectSettings') | Out-Null
Set-Content -LiteralPath (Join-Path $projectRoot '.unity-mcp-validation') -Value $pluginRoot
$manifest = @{ dependencies = @{ 'com.anklebreaker.unity-mcp' = "file:$pluginRoot" } } | ConvertTo-Json
[System.IO.File]::WriteAllText((Join-Path $projectRoot 'Packages/manifest.json'), $manifest)
$runnerFile = switch ($Suite) { 'RequestBody' { 'RequestBodyValidation.cs' } 'EditorCapture' { 'EditorCaptureValidation.cs' } 'HttpDiagnostics' { 'HttpDiagnosticsValidation.cs' } 'RequestInput' { 'RequestInputValidation.cs' } 'Undo' { 'UndoValidation.cs' } 'Serialization' { 'SerializationValidation.cs' } 'RequestShutdown' { 'RequestShutdownValidation.cs' } 'TestPersistence' { 'TestPersistenceValidation.cs' } 'TestResults' { 'TestResultsValidation.cs' } 'Testing' { 'TestRunnerValidation.cs' } 'Packages' { 'PackageManagerValidation.cs' } 'Dashboard' { 'DashboardValidation.cs' } 'Execution' { 'ExecutionValidation.cs' } 'Health' { 'QueueHealthValidation.cs' } 'Monitoring' { 'MonitoringValidation.cs' } default { 'ValidationRunner.cs' } }
$runnerClass = switch ($Suite) { 'RequestBody' { 'UnityMcpRequestBodyValidation' } 'EditorCapture' { 'UnityMcpCaptureFixture.UnityMcpEditorCaptureValidation' } 'HttpDiagnostics' { 'UnityMcpHttpDiagnosticsValidation' } 'RequestInput' { 'UnityMcpRequestInputValidation' } 'Undo' { 'UnityMcpUndoValidation' } 'Serialization' { 'UnityMcpSerializationValidation' } 'RequestShutdown' { 'UnityMcpRequestShutdownValidation' } 'TestPersistence' { 'UnityMcpTestPersistenceValidation' } 'TestResults' { 'UnityMcpTestResultsValidation' } 'Testing' { 'UnityMcpTestRunnerValidation' } 'Packages' { 'UnityMcpPackageManagerValidation' } 'Dashboard' { 'UnityMcpDashboardValidation' } 'Execution' { 'UnityMcpExecutionValidation' } 'Health' { 'UnityMcpQueueHealthValidation' } 'Monitoring' { 'UnityMcpMonitoringValidation' } default { 'UnityMcpValidation' } }
if ($Suite -eq 'GraphicsCapture') { $runnerFile = 'GraphicsCaptureValidation.cs'; $runnerClass = 'UnityMcpGraphicsCaptureValidation' }
if ($Suite -eq 'AssetPreview') { $runnerFile = 'AssetPreviewValidation.cs'; $runnerClass = 'UnityMcpAssetPreviewValidation' }
if ($Suite -eq 'MeshMetadata') { $runnerFile = 'MeshMetadataValidation.cs'; $runnerClass = 'UnityMcpMeshMetadataValidation' }
if ($Suite -eq 'QueueAdmission') { $runnerFile = 'QueueAdmissionValidation.cs'; $runnerClass = 'UnityMcpQueueAdmissionValidation' }
if ($Suite -eq 'ResultRetention') { $runnerFile = 'ResultRetentionValidation.cs'; $runnerClass = 'UnityMcpResultRetentionValidation' }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot $runnerFile) -Destination (Join-Path $projectRoot "Assets/Editor/$runnerFile")
$logPath = Join-Path $projectRoot 'validation.log'
$reportName = $runnerClass.Split('.')[-1]
$reportPath = Join-Path $projectRoot "Library/$reportName.json"
if (Test-Path -LiteralPath $reportPath) { Remove-Item -LiteralPath $reportPath }
$arguments = @('-batchmode', '-nographics', '-projectPath', ('"{0}"' -f $projectRoot), '-executeMethod', "$runnerClass.Run", '-logFile', ('"{0}"' -f $logPath))
if ($Suite -in @('GraphicsCapture', 'AssetPreview')) { $arguments = $arguments | Where-Object { $_ -ne '-nographics' } }
$previousTemp = $env:TEMP
$previousTmp = $env:TMP
try {
    if ($Suite -eq 'Execution') {
        $executionTemp = Join-Path $projectRoot 'ExecutionTemp'
        New-Item -ItemType Directory -Force -Path $executionTemp | Out-Null
        $env:TEMP = $executionTemp
        $env:TMP = $executionTemp
    }
    $editorProcess = Start-Process -FilePath $EditorPath -ArgumentList $arguments -WindowStyle Hidden -PassThru
} finally { $env:TEMP = $previousTemp; $env:TMP = $previousTmp }
Write-Output "Unity validation PID: $($editorProcess.Id), log: $logPath"
while (!$editorProcess.WaitForExit(10000)) {
    Write-Output "Unity validation running (PID $($editorProcess.Id))"
}
if ($editorProcess.ExitCode -ne 0 -or !(Test-Path -LiteralPath $reportPath)) {
    Get-Content -LiteralPath $logPath -Tail 40
    throw "Unity validation failed (exit $($editorProcess.ExitCode))"
}
$report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
if (!$report.passed) { throw $report.error }
Get-Content -LiteralPath $reportPath
