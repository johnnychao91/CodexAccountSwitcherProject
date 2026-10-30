$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$framework = Split-Path $compiler
$wpf = Join-Path $framework 'WPF'
$release = Join-Path $projectRoot 'dist-v1.5.4'
New-Item -ItemType Directory -Force -Path $release | Out-Null
$app = Join-Path $release 'CodexAccountSwitcher.exe'
$argsList = @('/nologo', '/target:winexe', '/main:CodexAccountSwitcher.Program', '/platform:x64', '/optimize+', '/codepage:65001',
    "/out:$app", "/resource:$(Join-Path $projectRoot 'MainWindow.xaml'),MainWindow.xaml",
    '/reference:System.dll', '/reference:System.Core.dll', '/reference:System.Security.dll', '/reference:System.Web.Extensions.dll', '/reference:System.Xml.dll', '/reference:System.Management.dll', '/reference:System.Net.Http.dll',
    "/reference:$(Join-Path $wpf 'PresentationFramework.dll')", "/reference:$(Join-Path $wpf 'PresentationCore.dll')", "/reference:$(Join-Path $wpf 'WindowsBase.dll')", '/reference:System.Xaml.dll',
    (Join-Path $projectRoot 'Core.cs'), (Join-Path $projectRoot 'DesktopLauncher.cs'), (Join-Path $projectRoot 'ProcessInspector.cs'), (Join-Path $projectRoot 'SwitchWorkflow.cs'), (Join-Path $projectRoot 'App.cs'), (Join-Path $projectRoot 'SelfTests.cs'), (Join-Path $projectRoot 'AccountOrderTests.cs'), (Join-Path $projectRoot 'AccountUiTests.cs'), (Join-Path $projectRoot 'ProcessInspectorTests.cs'), (Join-Path $projectRoot 'SwitchWorkflowTests.cs'), (Join-Path $projectRoot 'UsageModels.cs'), (Join-Path $projectRoot 'UsageModelTests.cs'), (Join-Path $projectRoot 'UsageSession.cs'), (Join-Path $projectRoot 'UsageSessionTests.cs'), (Join-Path $projectRoot 'AutoSwitchController.cs'), (Join-Path $projectRoot 'AutoSwitchTests.cs'), (Join-Path $projectRoot 'ManagedRuntimeClient.cs'), (Join-Path $projectRoot 'ManagedRuntimeClientTests.cs'))
$argsList += @((Join-Path $projectRoot 'ManagedBridge.cs'), (Join-Path $projectRoot 'ManagedBridgeTests.cs'))
$argsList += (Join-Path $projectRoot 'QuotaResetTests.cs')
$argsList += (Join-Path $projectRoot 'QuotaResetIntegrationTests.cs')
$desktopSources = @('DesktopNative.cs', 'DesktopLocalWorkPolicy.cs', 'DesktopWebSocket.cs', 'DesktopSession.cs', 'DesktopHostProgram.cs', 'ContinuationTicket.cs', 'ContinuationAdmission.cs', 'DesktopAppRelay.cs') | ForEach-Object { Join-Path $projectRoot $_ }
$argsList += $desktopSources
$argsList += @('DesktopNativeTests.cs', 'DesktopLocalWorkPolicyTests.cs', 'DesktopWebSocketTests.cs', 'DesktopSessionTests.cs', 'DesktopRuntimeCoordinator.cs', 'DesktopAutoStart.cs', 'DesktopAutoStartTests.cs') | ForEach-Object { Join-Path $projectRoot $_ }
$argsList += @('ContinuationTicketTests.cs', 'ContinuationAdmissionTests.cs', 'DesktopConversationClient.cs', 'DesktopConversationClientTests.cs', 'DesktopContinuationWorkflow.cs', 'DesktopContinuationWorkflowTests.cs', 'DesktopAppRelayTests.cs') | ForEach-Object { Join-Path $projectRoot $_ }
& $compiler @argsList
if ($LASTEXITCODE -ne 0) { throw 'Application compilation failed.' }
Copy-Item -LiteralPath (Join-Path $projectRoot 'App.config') -Destination "$app.config" -Force
$testBin = Join-Path $projectRoot 'tests\bin'
New-Item -ItemType Directory -Force -Path $testBin | Out-Null
Copy-Item -LiteralPath $app -Destination (Join-Path $testBin 'CodexAccountSwitcher.exe') -Force
Copy-Item -LiteralPath "$app.config" -Destination (Join-Path $testBin 'CodexAccountSwitcher.exe.config') -Force
& $compiler /nologo /target:exe /main:CodexAccountSwitcher.ManagedBridgeProgram /platform:x64 /optimize+ /codepage:65001 "/out:$(Join-Path $release 'ManagedBridge.exe')" /reference:System.dll /reference:System.Core.dll /reference:System.Security.dll /reference:System.Web.Extensions.dll (Join-Path $projectRoot 'ManagedBridge.cs') (Join-Path $projectRoot 'ManagedBridgeProgram.cs') (Join-Path $projectRoot 'ContinuationAdmission.cs')
if ($LASTEXITCODE -ne 0) { throw 'Integration helper compilation failed.' }
Copy-Item -LiteralPath (Join-Path $projectRoot 'App.config') -Destination (Join-Path $release 'ManagedBridge.exe.config') -Force
& $compiler /nologo /target:winexe /main:CodexAccountSwitcher.DesktopHostProgram /platform:x64 /optimize+ /codepage:65001 "/out:$(Join-Path $release 'DesktopRuntimeHost.exe')" /reference:System.dll /reference:System.Core.dll /reference:System.Security.dll /reference:System.Xml.dll /reference:System.Management.dll /reference:System.Net.Http.dll /reference:System.Web.Extensions.dll (Join-Path $projectRoot 'Core.cs') (Join-Path $projectRoot 'DesktopLauncher.cs') (Join-Path $projectRoot 'ProcessInspector.cs') (Join-Path $projectRoot 'ManagedBridge.cs') @desktopSources
if ($LASTEXITCODE -ne 0) { throw 'Desktop host compilation failed.' }
Copy-Item -LiteralPath (Join-Path $projectRoot 'App.config') -Destination (Join-Path $release 'DesktopRuntimeHost.exe.config') -Force
& $compiler /nologo /target:exe /main:CodexAccountSwitcher.DesktopAppRelayProgram /platform:x64 /optimize+ /codepage:65001 "/out:$(Join-Path $release 'DesktopAppRelay.exe')" /reference:System.dll /reference:System.Core.dll /reference:System.Security.dll /reference:System.Xml.dll /reference:System.Management.dll /reference:System.Net.Http.dll /reference:System.Web.Extensions.dll (Join-Path $projectRoot 'Core.cs') (Join-Path $projectRoot 'DesktopLauncher.cs') (Join-Path $projectRoot 'ProcessInspector.cs') (Join-Path $projectRoot 'ManagedBridge.cs') @desktopSources
if ($LASTEXITCODE -ne 0) { throw 'Desktop relay compilation failed.' }
Copy-Item -LiteralPath (Join-Path $projectRoot 'App.config') -Destination (Join-Path $release 'DesktopAppRelay.exe.config') -Force
Copy-Item -LiteralPath (Join-Path $projectRoot 'desktop-start.ps1') -Destination (Join-Path $release 'desktop-start.ps1') -Force
foreach ($name in @('DesktopRuntimeHost.exe', 'DesktopRuntimeHost.exe.config', 'DesktopAppRelay.exe', 'DesktopAppRelay.exe.config', 'desktop-start.ps1')) {
    Copy-Item -LiteralPath (Join-Path $release $name) -Destination (Join-Path $testBin $name) -Force
}
& $compiler /nologo /target:exe /platform:x64 /codepage:65001 "/out:$(Join-Path $testBin 'FakeManagedBackend.exe')" /reference:System.dll /reference:System.Core.dll /reference:System.Web.Extensions.dll (Join-Path $projectRoot 'tests\FakeManagedBackend.cs')
if ($LASTEXITCODE -ne 0) { throw 'Integration test helper compilation failed.' }
& $compiler /nologo /target:winexe /platform:x64 /codepage:65001 "/out:$(Join-Path $testBin 'FakeDesktopApp.exe')" /reference:System.dll /reference:System.Core.dll /reference:System.Net.Http.dll /reference:System.Web.Extensions.dll (Join-Path $projectRoot 'tests\FakeDesktopApp.cs')
if ($LASTEXITCODE -ne 0) { throw 'Desktop test helper compilation failed.' }
& $compiler /nologo /target:exe /platform:x64 /codepage:65001 "/out:$(Join-Path $testBin 'FakeCodex.exe')" "/reference:$app" (Join-Path $projectRoot 'tests\FakeCodex.cs')
if ($LASTEXITCODE -ne 0) { throw 'Test helper compilation failed.' }
& $compiler /nologo /target:exe /platform:x64 /codepage:65001 "/out:$(Join-Path $testBin 'FakeUsageCodex.exe')" "/reference:$app" (Join-Path $projectRoot 'tests\FakeUsageCodex.cs')
if ($LASTEXITCODE -ne 0) { throw 'Usage test helper compilation failed.' }
& $compiler /nologo /target:exe /platform:x64 /codepage:65001 "/out:$(Join-Path $testBin 'LaunchProbe.exe')" "/reference:$app" (Join-Path $projectRoot 'tests\LaunchProbe.cs')
if ($LASTEXITCODE -ne 0) { throw 'Launch probe compilation failed.' }
& $compiler /nologo /target:exe /platform:x64 /codepage:65001 "/out:$(Join-Path $testBin 'ProcessProbe.exe')" "/reference:$app" (Join-Path $projectRoot 'tests\ProcessProbe.cs')
if ($LASTEXITCODE -ne 0) { throw 'Process probe compilation failed.' }
& (Join-Path $projectRoot 'integrations\vscode\pack.ps1') -OutputPath (Join-Path $release 'codex-account-switcher-vscode.vsix')
Write-Output "Built: $app"
