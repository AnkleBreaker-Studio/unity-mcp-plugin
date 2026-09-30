param(
    [Parameter(Mandatory = $true)][string]$EditorPath,
    [Parameter(Mandatory = $true)][string]$ProjectPath,
    [ValidateSet('Workflows', 'Unavailable')][string]$Suite = 'Workflows'
)
$ErrorActionPreference = 'Stop'
if (!(Test-Path -LiteralPath $EditorPath -PathType Leaf)) { throw "Unity executable not found: $EditorPath" }
$projectRoot = (Resolve-Path -LiteralPath $ProjectPath).Path
if (!(Test-Path -LiteralPath (Join-Path $projectRoot '.unity-mcp-validation') -PathType Leaf)) {
    throw 'Use a disposable project marked with .unity-mcp-validation, with UMA imported and UMA_INSTALLED enabled.'
}
if (Test-Path -LiteralPath (Join-Path $projectRoot 'Temp/UnityLockfile')) { throw 'Close the validation editor first.' }
$runnerDirectory = Join-Path $projectRoot 'Assets/Editor'
New-Item -ItemType Directory -Force -Path $runnerDirectory | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'McpUmaValidation.cs') -Destination (Join-Path $runnerDirectory 'McpUmaValidation.cs')
$logPath = Join-Path $projectRoot 'uma-validation.log'
$reportName = if ($Suite -eq 'Unavailable') { 'McpUmaUnavailableValidation' } else { 'McpUmaValidation' }
$entryMethod = if ($Suite -eq 'Unavailable') { 'RunUnavailable' } else { 'Run' }
$reportPath = Join-Path $projectRoot "Library/$reportName.json"
if (Test-Path -LiteralPath $reportPath) { Remove-Item -LiteralPath $reportPath }
$arguments = @('-batchmode', '-nographics', '-projectPath', ('"{0}"' -f $projectRoot), '-executeMethod', "McpUmaValidation.$entryMethod", '-logFile', ('"{0}"' -f $logPath))
$editorProcess = Start-Process -FilePath $EditorPath -ArgumentList $arguments -WindowStyle Hidden -PassThru
Write-Output "UMA validation PID: $($editorProcess.Id), log: $logPath"
while (!$editorProcess.WaitForExit(10000)) { Write-Output "UMA validation running (PID $($editorProcess.Id))" }
if ($editorProcess.ExitCode -ne 0 -or !(Test-Path -LiteralPath $reportPath)) {
    Get-Content -LiteralPath $logPath -Tail 35
    throw "UMA validation failed (exit $($editorProcess.ExitCode)); report: $reportPath"
}
$report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
if (!$report.passed) { throw "UMA validation failed: $($report.error) $($report.failures -join ', ')" }
Write-Output "UMA validation passed; report: $reportPath"
