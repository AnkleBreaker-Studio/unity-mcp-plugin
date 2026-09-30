param(
    [Parameter(Mandatory = $true)][string]$EditorPath,
    [Parameter(Mandatory = $true)][string]$PackageAssemblyPath,
    [Parameter(Mandatory = $true)][string]$OutputPath
)
$ErrorActionPreference = 'Stop'
$editorFile = Get-Item -LiteralPath $EditorPath
$version = $editorFile.VersionInfo.ProductVersion.Split('_')[0]
if ($version -ne '2021.3.18f1') { throw "This check targets the declared minimum 2021.3.18f1, received $version" }
$editorData = Join-Path $editorFile.DirectoryName 'Data'
$packageAssemblyRoot = (Resolve-Path -LiteralPath $PackageAssemblyPath).Path
$outputRoot = [System.IO.Path]::GetFullPath($OutputPath)
$pluginRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null
$references = @()
foreach ($folder in @('Managed/UnityEngine', 'NetStandard/ref/2.1.0', 'NetStandard/compat/2.1.0/shims/netfx', 'NetStandard/compat/2.1.0/shims/netstandard', 'NetStandard/Extensions/2.0.0')) {
    $references += Get-ChildItem -LiteralPath (Join-Path $editorData $folder) -Filter '*.dll' -File
}
$packages = @()
foreach ($name in @('UnityEngine.UI.dll', 'UnityEngine.TestRunner.dll', 'UnityEditor.TestRunner.dll')) {
    $assembly = Get-Item -LiteralPath (Join-Path $packageAssemblyRoot $name)
    $references += $assembly
    $packages += @{ file = $assembly.FullName; sha256 = (Get-FileHash -LiteralPath $assembly.FullName -Algorithm SHA256).Hash }
}
$sources = @(Get-ChildItem -LiteralPath (Join-Path $pluginRoot 'Editor') -Filter '*.cs' -File -Recurse)
$assemblyOutput = Join-Path $outputRoot 'AnkleBreaker.UnityMCP.Editor.dll'
if (Test-Path -LiteralPath $assemblyOutput) { Remove-Item -LiteralPath $assemblyOutput }
$response = @('-target:library', '-nologo', '-nostdlib+', '-langversion:9', '/preferreduilang:en-US', ('-out:"{0}"' -f $assemblyOutput))
$response += '-define:UNITY_EDITOR;UNITY_EDITOR_WIN;UNITY_EDITOR_64;UNITY_STANDALONE_WIN;NET_STANDARD_2_1;NETSTANDARD2_1;UNITY_2021_3;UNITY_2021_3_18;UNITY_2021_3_OR_NEWER;UNITY_2021_2_OR_NEWER;UNITY_2021_1_OR_NEWER;UNITY_2020_3_OR_NEWER;UNITY_2019_4_OR_NEWER;UNITY_2018_4_OR_NEWER;UNITY_2017_4_OR_NEWER;UNITY_5_3_OR_NEWER'
$response += $references | ForEach-Object { '-r:"{0}"' -f $_.FullName }
$response += $sources | ForEach-Object { '"{0}"' -f $_.FullName }
$responsePath = Join-Path $outputRoot 'compile.rsp'
[System.IO.File]::WriteAllLines($responsePath, $response, [System.Text.UTF8Encoding]::new($false))
$logPath = Join-Path $outputRoot 'compiler.log'
& (Join-Path $editorData 'NetCoreRuntime/dotnet.exe') (Join-Path $editorData 'DotNetSdkRoslyn/csc.dll') "@$responsePath" > $logPath
$compilerExit = $LASTEXITCODE
$report = @{
    unityVersion = $version
    check = 'C# compilation against installed Unity assemblies; no editor execution or package import'
    sourceCount = $sources.Count
    referenceCount = $references.Count
    packageAssemblies = $packages
    exitCode = $compilerExit
    passed = $compilerExit -eq 0 -and (Test-Path -LiteralPath $assemblyOutput)
    excludedIntegrations = @('UMA', 'ProBuilder')
    log = $logPath
}
$report | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $outputRoot 'report.json') -Encoding UTF8
if (!$report.passed) {
    Get-Content -LiteralPath $logPath
    throw "Unity API compilation failed (exit $compilerExit)"
}
$report | ConvertTo-Json -Depth 5
