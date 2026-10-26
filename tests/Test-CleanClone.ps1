[CmdletBinding()]
param([string]$WorkRoot)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent $PSScriptRoot
$gitCommand = Get-Command git.exe -ErrorAction SilentlyContinue
if (-not $gitCommand) { throw 'Install Git for Windows before running this test.' }
if ([string]::IsNullOrWhiteSpace($WorkRoot)) {
    $WorkRoot = Join-Path ([IO.Path]::GetTempPath()) ('switcher-clean-clone-' + [Guid]::NewGuid().ToString('N'))
}
$work = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($WorkRoot)
if (Test-Path -LiteralPath $work) { throw 'WorkRoot must be a new directory. Existing files will not be changed.' }
New-Item -ItemType Directory -Path $work | Out-Null
$source = Join-Path $work 'source'
$clone = Join-Path $work '中文 clone with spaces'
$unrelated = Join-Path $work 'unrelated working directory'
$emptyHooks = Join-Path $work 'empty-hooks'
$emptyConfig = Join-Path $work 'empty-git-config'
New-Item -ItemType Directory -Path $unrelated, $emptyHooks | Out-Null
[IO.File]::WriteAllText($emptyConfig, '')

function Quote-Argument([string]$Value) {
    # These arguments are paths or fixed test strings; none contain embedded quotes.
    if ($Value.Contains('"') -or $Value.Contains("`r") -or $Value.Contains("`n")) { throw 'Unsupported test argument.' }
    return '"' + $Value.TrimEnd('\') + '"'
}

function Invoke-Child([string]$Executable, [string]$Arguments, [string]$Directory, [bool]$IsGit = $false) {
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName = $Executable
    $start.Arguments = $Arguments
    $start.WorkingDirectory = $Directory
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    if ($IsGit) {
        # Keep an inherited GIT_DIR, index, hooks, or signing configuration out of the fixture.
        foreach ($name in @($start.EnvironmentVariables.Keys)) {
            if ($name -like 'GIT_*') { $start.EnvironmentVariables.Remove($name) }
        }
        $start.EnvironmentVariables['GIT_CONFIG_NOSYSTEM'] = '1'
        $start.EnvironmentVariables['GIT_CONFIG_GLOBAL'] = $emptyConfig
        $start.EnvironmentVariables['GIT_CONFIG_SYSTEM'] = $emptyConfig
        $start.EnvironmentVariables['GIT_TERMINAL_PROMPT'] = '0'
    }
    $child = New-Object Diagnostics.Process
    $child.StartInfo = $start
    try {
        if (-not $child.Start()) { throw 'Test process did not start.' }
        $stdout = $child.StandardOutput.ReadToEndAsync()
        $stderr = $child.StandardError.ReadToEndAsync()
        if (-not $child.WaitForExit(180000)) { throw 'Test process timed out; inspect the retained test directory.' }
        $output = $stdout.Result + $stderr.Result
        return [pscustomobject]@{ ExitCode = $child.ExitCode; Output = $output }
    } finally { $child.Dispose() }
}

function Invoke-Git([string[]]$Arguments) {
    $all = @('-c', ('core.hooksPath=' + $emptyHooks), '-c', 'commit.gpgsign=false') + $Arguments
    $line = ($all | ForEach-Object { Quote-Argument $_ }) -join ' '
    $result = Invoke-Child $gitCommand.Source $line $work $true
    if ($result.ExitCode -ne 0) { throw ('Fixture Git operation failed: ' + $result.Output) }
    return $result.Output
}

function Invoke-Launcher([string]$Argument = '--build-only', [int]$ExpectedCode = 0, [string]$LauncherRoot = $clone) {
    $launcher = Join-Path $LauncherRoot 'Codex帳號切換器.cmd'
    $launcherText = [IO.File]::ReadAllText($launcher)
    foreach ($required in @('set "SWITCHER_BUILD_ONLY=1"', 'if defined SWITCHER_BUILD_ONLY exit /b 0',
        'if defined SWITCHER_BUILD_ONLY exit /b 1', ':invalid_arguments', 'exit /b 2')) {
        if (-not $launcherText.Contains($required)) { throw 'Launcher lacks the required non-GUI test mode; nothing was launched.' }
    }
    $line = '/d /v:off /s /c ""' + $launcher + '" ' + $Argument + '"'
    $result = Invoke-Child $env:ComSpec $line $unrelated
    if ($result.ExitCode -ne $ExpectedCode) { throw ('Launcher verification failed: ' + $result.Output) }
}

$releaseNames = @('CodexAccountSwitcher.exe', 'CodexAccountSwitcher.exe.config',
    'ManagedBridge.exe', 'ManagedBridge.exe.config', 'DesktopRuntimeHost.exe', 'DesktopRuntimeHost.exe.config',
    'DesktopAppRelay.exe', 'DesktopAppRelay.exe.config', 'desktop-start.ps1', 'codex-account-switcher-vscode.vsix')

function Assert-Release {
    foreach ($name in $releaseNames) {
        $file = Join-Path $release $name
        if (-not (Test-Path -LiteralPath $file -PathType Leaf) -or (Get-Item -LiteralPath $file).Length -eq 0) {
            throw ('Missing release dependency: ' + $name)
        }
    }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead((Join-Path $release 'codex-account-switcher-vscode.vsix'))
    try {
        $expected = @('[Content_Types].xml', 'extension.vsixmanifest', 'extension/package.json', 'extension/extension.js', 'extension/README.md')
        $actual = @($archive.Entries | ForEach-Object { $_.FullName })
        if ($actual.Count -ne 5 -or @(Compare-Object $expected $actual).Count -ne 0 -or
            @($archive.Entries | Where-Object { $_.Length -eq 0 }).Count -ne 0) { throw 'VSIX contents are incomplete.' }
    } finally { $archive.Dispose() }
}

& (Join-Path $project 'prepare-github.ps1') -Destination $source
Invoke-Git @('init', ('--template=' + $emptyHooks), $source) | Out-Null
Invoke-Git @('-C', $source, 'add', '--', '.') | Out-Null
Invoke-Git @('-C', $source, '-c', 'user.name=Clean Clone Test', '-c', 'user.email=clone@example.test',
    'commit', '-m', 'Synthetic source-only clone fixture') | Out-Null
Invoke-Git @('clone', '--no-local', '--', $source, $clone) | Out-Null

$releases = @(Get-ChildItem -LiteralPath $clone -Directory -Filter 'dist-*')
if ($releases.Count -ne 0 -or (Test-Path -LiteralPath (Join-Path $clone 'tests\bin'))) { throw 'Clone contains build artifacts.' }
Invoke-Launcher '--unknown-test-option' 2
Invoke-Launcher '--build-only extra' 2
$failureFixture = Join-Path $work 'failed build fixture'
New-Item -ItemType Directory -Path $failureFixture | Out-Null
Copy-Item -LiteralPath (Join-Path $clone 'Codex帳號切換器.cmd') -Destination $failureFixture
[IO.File]::WriteAllText((Join-Path $failureFixture 'build.ps1'), 'exit 3')
Invoke-Launcher '--build-only' 1 $failureFixture
Invoke-Launcher
$releases = @(Get-ChildItem -LiteralPath $clone -Directory -Filter 'dist-*')
if ($releases.Count -ne 1) { throw 'Expected one newly built release.' }
$release = $releases[0].FullName
Assert-Release

$timestamps = @{}
foreach ($name in $releaseNames) { $timestamps[$name] = (Get-Item -LiteralPath (Join-Path $release $name)).LastWriteTimeUtc.Ticks }
Invoke-Launcher
foreach ($name in $releaseNames) {
    if ((Get-Item -LiteralPath (Join-Path $release $name)).LastWriteTimeUtc.Ticks -ne $timestamps[$name]) {
        throw 'The complete cached release was unnecessarily rebuilt.'
    }
}

# Delete only one generated file in the disposable clone to exercise automatic repair.
Remove-Item -LiteralPath (Join-Path $release 'DesktopAppRelay.exe.config')
Invoke-Launcher
Assert-Release
if (-not [string]::IsNullOrWhiteSpace((Invoke-Git @('-C', $clone, 'status', '--porcelain', '--untracked-files=all')))) {
    throw 'Building changed the source checkout or produced non-ignored files.'
}
$summary = [pscustomobject]@{ Passed = $true; CloneRoot = $clone; ReleaseFiles = $releaseNames.Count; VsixEntries = 5;
    FreshCloneBuilt = $true; CachedReleasePreserved = $true; MissingDependencyRebuilt = $true;
    UnknownArgumentsRejected = $true; BuildFailureReturnsWithoutPause = $true;
    CodexAppLaunched = $false; RealCredentialsRead = $false }
$summary | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $work 'result.json') -Encoding UTF8
$summary | ConvertTo-Json
