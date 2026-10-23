$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$framework = Split-Path $compiler
$wpf = Join-Path $framework 'WPF'
$release = Join-Path $projectRoot 'dist-v1.1.0'
New-Item -ItemType Directory -Force -Path $release | Out-Null
$app = Join-Path $release 'CodexAccountSwitcher.exe'
$argsList = @('/nologo', '/target:winexe', '/platform:x64', '/optimize+', '/codepage:65001',
    "/out:$app", "/resource:$(Join-Path $projectRoot 'MainWindow.xaml'),MainWindow.xaml",
    '/reference:System.dll', '/reference:System.Core.dll', '/reference:System.Security.dll', '/reference:System.Web.Extensions.dll', '/reference:System.Xml.dll', '/reference:System.Management.dll',
    "/reference:$(Join-Path $wpf 'PresentationFramework.dll')", "/reference:$(Join-Path $wpf 'PresentationCore.dll')", "/reference:$(Join-Path $wpf 'WindowsBase.dll')", '/reference:System.Xaml.dll',
    (Join-Path $projectRoot 'Core.cs'), (Join-Path $projectRoot 'DesktopLauncher.cs'), (Join-Path $projectRoot 'ProcessInspector.cs'), (Join-Path $projectRoot 'SwitchWorkflow.cs'), (Join-Path $projectRoot 'App.cs'), (Join-Path $projectRoot 'SelfTests.cs'), (Join-Path $projectRoot 'ProcessInspectorTests.cs'), (Join-Path $projectRoot 'SwitchWorkflowTests.cs'), (Join-Path $projectRoot 'UsageModels.cs'), (Join-Path $projectRoot 'UsageModelTests.cs'), (Join-Path $projectRoot 'UsageSession.cs'), (Join-Path $projectRoot 'UsageSessionTests.cs'))
& $compiler @argsList
if ($LASTEXITCODE -ne 0) { throw 'Application compilation failed.' }
Copy-Item -LiteralPath (Join-Path $projectRoot 'App.config') -Destination "$app.config" -Force
$testBin = Join-Path $projectRoot 'tests\bin'
New-Item -ItemType Directory -Force -Path $testBin | Out-Null
Copy-Item -LiteralPath $app -Destination (Join-Path $testBin 'CodexAccountSwitcher.exe') -Force
Copy-Item -LiteralPath "$app.config" -Destination (Join-Path $testBin 'CodexAccountSwitcher.exe.config') -Force
& $compiler /nologo /target:exe /platform:x64 /codepage:65001 "/out:$(Join-Path $testBin 'FakeCodex.exe')" "/reference:$app" (Join-Path $projectRoot 'tests\FakeCodex.cs')
if ($LASTEXITCODE -ne 0) { throw 'Test helper compilation failed.' }
& $compiler /nologo /target:exe /platform:x64 /codepage:65001 "/out:$(Join-Path $testBin 'FakeUsageCodex.exe')" "/reference:$app" (Join-Path $projectRoot 'tests\FakeUsageCodex.cs')
if ($LASTEXITCODE -ne 0) { throw 'Usage test helper compilation failed.' }
& $compiler /nologo /target:exe /platform:x64 /codepage:65001 "/out:$(Join-Path $testBin 'LaunchProbe.exe')" "/reference:$app" (Join-Path $projectRoot 'tests\LaunchProbe.cs')
if ($LASTEXITCODE -ne 0) { throw 'Launch probe compilation failed.' }
& $compiler /nologo /target:exe /platform:x64 /codepage:65001 "/out:$(Join-Path $testBin 'ProcessProbe.exe')" "/reference:$app" (Join-Path $projectRoot 'tests\ProcessProbe.cs')
if ($LASTEXITCODE -ne 0) { throw 'Process probe compilation failed.' }
Write-Output "Built: $app"
