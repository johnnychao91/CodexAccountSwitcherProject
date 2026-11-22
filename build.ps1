param([string]$OutputRoot = $PSScriptRoot)
$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
if (-not [Environment]::Is64BitOperatingSystem) {
    throw 'This application requires 64-bit Windows and .NET Framework 4.8.'
}
if ([string]::IsNullOrWhiteSpace($env:WINDIR)) { throw 'WINDIR is not set; cannot locate the .NET Framework compiler.' }
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) {
    throw "The .NET Framework compiler was not found at $compiler. Install or repair .NET Framework 4.8."
}
$framework = Split-Path $compiler
$wpf = Join-Path $framework 'WPF'
foreach ($assembly in @('PresentationFramework.dll', 'PresentationCore.dll', 'WindowsBase.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $wpf $assembly) -PathType Leaf)) {
        throw "The .NET Framework WPF assembly $assembly was not found in $wpf. Install or repair .NET Framework 4.8."
    }
}
$release = Join-Path $OutputRoot 'release\v1.6.19'
New-Item -ItemType Directory -Force -Path $release | Out-Null
$app = Join-Path $release 'CodexAccountSwitcher.exe'
$argsList = @('/nologo', '/target:winexe', '/main:CodexAccountSwitcher.Program', '/platform:x64', '/optimize+', '/codepage:65001',
    "/out:$app", "/resource:$(Join-Path $projectRoot 'src\Roster\MainWindow.xaml'),MainWindow.xaml",
    "/resource:$(Join-Path $projectRoot 'assets\codex-roster-transparent.png'),RosterLogo.png",
    "/resource:$(Join-Path $projectRoot 'assets\RosterIcon.ico'),RosterIcon.ico",
    "/win32icon:$(Join-Path $projectRoot 'assets\RosterIcon.ico')",
    '/reference:System.dll', '/reference:System.Core.dll', '/reference:System.Security.dll', '/reference:System.Web.Extensions.dll', '/reference:System.Xml.dll', '/reference:System.Management.dll', '/reference:System.Net.Http.dll',
    "/reference:$(Join-Path $wpf 'PresentationFramework.dll')", "/reference:$(Join-Path $wpf 'PresentationCore.dll')", "/reference:$(Join-Path $wpf 'WindowsBase.dll')", '/reference:System.Xaml.dll'
    )
$accountSources = @('Core.cs', 'SwitchWorkflow.cs', 'UsageModels.cs', 'UsageSession.cs', 'AutoSwitchController.cs') | ForEach-Object { Join-Path $projectRoot "src\Accounts\$_" }
$runtimeSources = @('ProcessInspector.cs', 'ManagedRuntimeClient.cs', 'ManagedBridge.cs') | ForEach-Object { Join-Path $projectRoot "src\Runtime\$_" }
$desktopSources = @('DesktopNative.cs', 'DesktopLocalWorkPolicy.cs', 'DesktopArchiveCompatibility.cs', 'DesktopWebSocket.cs', 'DesktopSession.cs', 'DesktopHostProgram.cs', 'ContinuationTicket.cs', 'ContinuationAdmission.cs', 'DesktopAppRelay.cs') | ForEach-Object { Join-Path $projectRoot "src\Desktop\$_" }
$desktopSources += (Join-Path $projectRoot 'src\Accounts\ProjectSidebar.cs')
$appDesktopSources = @('DesktopLauncher.cs', 'DesktopRuntimeCoordinator.cs', 'DesktopAutoStart.cs', 'DesktopConversationClient.cs', 'DesktopContinuationWorkflow.cs') | ForEach-Object { Join-Path $projectRoot "src\Desktop\$_" }
$testSources = @('SelfTests.cs', 'AccountOrderTests.cs', 'AccountUiTests.cs', 'ProcessInspectorTests.cs', 'SwitchWorkflowTests.cs', 'UsageModelTests.cs', 'UsageSessionTests.cs', 'AutoSwitchTests.cs', 'ManagedRuntimeClientTests.cs', 'ManagedBridgeTests.cs', 'ProjectSidebarTests.cs', 'QuotaResetTests.cs', 'QuotaResetIntegrationTests.cs', 'DesktopNativeTests.cs', 'DesktopLocalWorkPolicyTests.cs', 'DesktopWebSocketTests.cs', 'DesktopSessionTests.cs', 'DesktopAutoStartTests.cs', 'ContinuationTicketTests.cs', 'ContinuationAdmissionTests.cs', 'DesktopConversationClientTests.cs', 'DesktopContinuationWorkflowTests.cs', 'DesktopAppRelayTests.cs') | ForEach-Object { Join-Path $projectRoot "tests\suites\$_" }
$argsList += $accountSources
$argsList += $runtimeSources
$argsList += $desktopSources
$argsList += $appDesktopSources
$argsList += (Join-Path $projectRoot 'src\Roster\App.cs')
$argsList += $testSources
& $compiler @argsList
if ($LASTEXITCODE -ne 0) { throw 'Application compilation failed.' }
Copy-Item -LiteralPath (Join-Path $projectRoot 'src\Roster\App.config') -Destination "$app.config" -Force
$testBin = Join-Path $OutputRoot 'tests\bin'
New-Item -ItemType Directory -Force -Path $testBin | Out-Null
Copy-Item -LiteralPath $app -Destination (Join-Path $testBin 'CodexAccountSwitcher.exe') -Force
Copy-Item -LiteralPath "$app.config" -Destination (Join-Path $testBin 'CodexAccountSwitcher.exe.config') -Force
& $compiler /nologo /target:exe /main:CodexAccountSwitcher.ManagedBridgeProgram /platform:x64 /optimize+ /codepage:65001 "/out:$(Join-Path $release 'ManagedBridge.exe')" /reference:System.dll /reference:System.Core.dll /reference:System.Security.dll /reference:System.Web.Extensions.dll (Join-Path $projectRoot 'src\Runtime\ManagedBridge.cs') (Join-Path $projectRoot 'src\Runtime\ManagedBridgeProgram.cs') (Join-Path $projectRoot 'src\Desktop\ContinuationAdmission.cs')
if ($LASTEXITCODE -ne 0) { throw 'Integration helper compilation failed.' }
Copy-Item -LiteralPath (Join-Path $projectRoot 'src\Roster\App.config') -Destination (Join-Path $release 'ManagedBridge.exe.config') -Force
& $compiler /nologo /target:winexe /main:CodexAccountSwitcher.DesktopHostProgram /platform:x64 /optimize+ /codepage:65001 "/out:$(Join-Path $release 'DesktopRuntimeHost.exe')" /reference:System.dll /reference:System.Core.dll /reference:System.Security.dll /reference:System.Xml.dll /reference:System.Management.dll /reference:System.Net.Http.dll /reference:System.Web.Extensions.dll (Join-Path $projectRoot 'src\Accounts\Core.cs') (Join-Path $projectRoot 'src\Desktop\DesktopLauncher.cs') (Join-Path $projectRoot 'src\Runtime\ProcessInspector.cs') (Join-Path $projectRoot 'src\Runtime\ManagedBridge.cs') @desktopSources
if ($LASTEXITCODE -ne 0) { throw 'Desktop host compilation failed.' }
Copy-Item -LiteralPath (Join-Path $projectRoot 'src\Roster\App.config') -Destination (Join-Path $release 'DesktopRuntimeHost.exe.config') -Force
& $compiler /nologo /target:exe /main:CodexAccountSwitcher.DesktopAppRelayProgram /platform:x64 /optimize+ /codepage:65001 "/out:$(Join-Path $release 'DesktopAppRelay.exe')" /reference:System.dll /reference:System.Core.dll /reference:System.Security.dll /reference:System.Xml.dll /reference:System.Management.dll /reference:System.Net.Http.dll /reference:System.Web.Extensions.dll (Join-Path $projectRoot 'src\Accounts\Core.cs') (Join-Path $projectRoot 'src\Desktop\DesktopLauncher.cs') (Join-Path $projectRoot 'src\Runtime\ProcessInspector.cs') (Join-Path $projectRoot 'src\Runtime\ManagedBridge.cs') @desktopSources
if ($LASTEXITCODE -ne 0) { throw 'Desktop relay compilation failed.' }
Copy-Item -LiteralPath (Join-Path $projectRoot 'src\Roster\App.config') -Destination (Join-Path $release 'DesktopAppRelay.exe.config') -Force
Copy-Item -LiteralPath (Join-Path $projectRoot 'src\Desktop\desktop-start.ps1') -Destination (Join-Path $release 'desktop-start.ps1') -Force
foreach ($name in @('DesktopRuntimeHost.exe', 'DesktopRuntimeHost.exe.config', 'DesktopAppRelay.exe', 'DesktopAppRelay.exe.config', 'desktop-start.ps1')) {
    Copy-Item -LiteralPath (Join-Path $release $name) -Destination (Join-Path $testBin $name) -Force
}
& $compiler /nologo /target:exe /platform:x64 /codepage:65001 "/out:$(Join-Path $testBin 'FakeManagedBackend.exe')" /reference:System.dll /reference:System.Core.dll /reference:System.Web.Extensions.dll (Join-Path $projectRoot 'tests\fixtures\FakeManagedBackend.cs')
if ($LASTEXITCODE -ne 0) { throw 'Integration test helper compilation failed.' }
& $compiler /nologo /target:winexe /platform:x64 /codepage:65001 "/out:$(Join-Path $testBin 'FakeDesktopApp.exe')" /reference:System.dll /reference:System.Core.dll /reference:System.Net.Http.dll /reference:System.Web.Extensions.dll (Join-Path $projectRoot 'tests\fixtures\FakeDesktopApp.cs')
if ($LASTEXITCODE -ne 0) { throw 'Desktop test helper compilation failed.' }
& $compiler /nologo /target:exe /platform:x64 /codepage:65001 "/out:$(Join-Path $testBin 'FakeCodex.exe')" "/reference:$app" (Join-Path $projectRoot 'tests\fixtures\FakeCodex.cs')
if ($LASTEXITCODE -ne 0) { throw 'Test helper compilation failed.' }
& $compiler /nologo /target:exe /platform:x64 /codepage:65001 "/out:$(Join-Path $testBin 'FakeUsageCodex.exe')" "/reference:$app" (Join-Path $projectRoot 'tests\fixtures\FakeUsageCodex.cs')
if ($LASTEXITCODE -ne 0) { throw 'Usage test helper compilation failed.' }
& $compiler /nologo /target:exe /platform:x64 /codepage:65001 "/out:$(Join-Path $testBin 'LaunchProbe.exe')" "/reference:$app" (Join-Path $projectRoot 'tests\fixtures\LaunchProbe.cs')
if ($LASTEXITCODE -ne 0) { throw 'Launch probe compilation failed.' }
& $compiler /nologo /target:exe /platform:x64 /codepage:65001 "/out:$(Join-Path $testBin 'ProcessProbe.exe')" "/reference:$app" (Join-Path $projectRoot 'tests\fixtures\ProcessProbe.cs')
if ($LASTEXITCODE -ne 0) { throw 'Process probe compilation failed.' }
& (Join-Path $projectRoot 'integrations\vscode\pack.ps1') -OutputPath (Join-Path $release 'codex-account-switcher-vscode.vsix')
Write-Output "Built: $app"
