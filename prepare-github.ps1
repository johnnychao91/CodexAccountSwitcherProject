[CmdletBinding()]
param([string]$Destination)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$sourceRoot = [IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\', '/')
if ([string]::IsNullOrWhiteSpace($Destination)) {
    $Destination = Join-Path (Split-Path -Parent $sourceRoot) 'codex-account-switcher-github'
}
$targetRoot = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Destination).TrimEnd('\', '/')
$files = @(
    '.gitignore', 'App.config', 'App.cs', 'build.ps1', 'Codex帳號切換器.cmd',
    'Core.cs', 'DesktopLauncher.cs', 'MainWindow.xaml', 'ProcessInspector.cs',
    'ProcessInspectorTests.cs', 'README.md', 'SelfTests.cs', 'SwitchWorkflow.cs',
    'SwitchWorkflowTests.cs', 'tests/FakeCodex.cs', 'tests/LaunchProbe.cs',
    'tests/ProcessProbe.cs', 'prepare-github.ps1', 'UsageModels.cs', 'UsageModelTests.cs',
    'UsageSession.cs', 'UsageSessionTests.cs', 'tests/FakeUsageCodex.cs'
)

function Assert-NoReparsePoints([string]$Path) {
    $cursor = [IO.Path]::GetFullPath($Path)
    while ($cursor) {
        try {
            $item = Get-Item -LiteralPath $cursor -Force -ErrorAction Stop
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "Refusing a symbolic link or reparse point in path: $cursor"
            }
        } catch [System.Management.Automation.ItemNotFoundException] {
            # A new destination can have ancestors that do not exist yet.
        }
        $parent = [IO.Directory]::GetParent($cursor)
        if ($null -eq $parent) { break }
        $cursor = $parent.FullName
    }
}

Write-Warning 'This tool checks only the listed privacy patterns; it cannot guarantee detection of every secret. Review the exported files before publishing.'
if ($targetRoot.Equals($sourceRoot, [StringComparison]::OrdinalIgnoreCase) -or
    $targetRoot.StartsWith($sourceRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Destination must be outside the source project.'
}
Assert-NoReparsePoints $sourceRoot
Assert-NoReparsePoints $targetRoot
if (Test-Path -LiteralPath $targetRoot) {
    throw 'Destination already exists. Choose a new directory; nothing will be overwritten.'
}

$patterns = [ordered]@{
    'JWT' = 'eyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}'
    'OpenAI credential' = '\bsk-[A-Za-z0-9_-]{20,}\b'
    'GitHub credential' = '\b(?:gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,})\b'
    'AWS access key' = '\b(?:AKIA|ASIA)[A-Z0-9]{16}\b'
    'Private key' = '-{5}BEGIN (?:[A-Z0-9]+ )?PRIVATE KEY-{5}'
    'Windows absolute path' = '(?i)(?<![A-Z0-9_])[A-Z]:[\\/]'
    'Network share path' = '(?<!\\)\\{2,}[A-Za-z0-9][A-Za-z0-9._-]*\\+[A-Za-z0-9_$][A-Za-z0-9_$.-]*'
    'User home path' = '/(?:home|Users)/[A-Za-z0-9_.-]+'
    'Windows user SID' = '\bS-1-5-21(?:-\d+){3,4}\b'
    'Windows host name' = '(?i)\b(?:DESKTOP|LAPTOP|WIN)-[A-Z0-9]{4,}\b'
}
if (-not [string]::IsNullOrWhiteSpace($env:COMPUTERNAME) -and $env:COMPUTERNAME.Length -ge 5) {
    $patterns['Current host name'] = '(?i)(?<![A-Za-z0-9_-])' + [regex]::Escape($env:COMPUTERNAME) + '(?![A-Za-z0-9_-])'
}
$emailPattern = '(?i)\b[A-Z0-9._%+-]+@([A-Z0-9.-]+\.[A-Z]{2,})\b'
$exampleDomain = '(?i)(^|\.)example\.(com|org|net)$|\.test$'
$issues = New-Object 'System.Collections.Generic.List[string]'
$hashes = @{}
foreach ($relative in $files) {
    $sourceFile = Join-Path $sourceRoot $relative
    Assert-NoReparsePoints $sourceFile
    if (-not (Test-Path -LiteralPath $sourceFile -PathType Leaf)) {
        throw "Required source file is missing: $relative"
    }
    $hashBefore = (Get-FileHash -LiteralPath $sourceFile -Algorithm SHA256).Hash
    $lines = [IO.File]::ReadAllLines($sourceFile)
    for ($index = 0; $index -lt $lines.Length; $index++) {
        foreach ($match in [regex]::Matches($lines[$index], $emailPattern)) {
            if ($match.Groups[1].Value -notmatch $exampleDomain) {
                $issues.Add(('{0}:{1}: Email address' -f $relative, ($index + 1)))
            }
        }
        foreach ($pattern in $patterns.GetEnumerator()) {
            if ([regex]::IsMatch($lines[$index], $pattern.Value)) {
                $issues.Add(('{0}:{1}: {2}' -f $relative, ($index + 1), $pattern.Key))
            }
        }
    }
    $hashes[$relative] = (Get-FileHash -LiteralPath $sourceFile -Algorithm SHA256).Hash
    if ($hashBefore -ne $hashes[$relative]) {
        throw "Source changed during scanning; retry after editing is finished: $relative"
    }
}
if ($issues.Count -gt 0) {
    $issues | Sort-Object -Unique | ForEach-Object { Write-Warning $_ }
    throw 'Privacy scan failed. No export directory was created. Resolve the reported locations and retry.'
}

# No -Force: an existing destination must never be reused or overwritten.
Assert-NoReparsePoints $targetRoot
$null = New-Item -ItemType Directory -Path $targetRoot -ErrorAction Stop
try {
    foreach ($relative in $files) {
        $sourceFile = Join-Path $sourceRoot $relative
        $targetFile = Join-Path $targetRoot $relative
        Assert-NoReparsePoints $sourceFile
        Assert-NoReparsePoints $targetFile
        $targetParent = Split-Path -Parent $targetFile
        if (-not (Test-Path -LiteralPath $targetParent)) {
            $null = New-Item -ItemType Directory -Path $targetParent -ErrorAction Stop
        }
        [IO.File]::Copy($sourceFile, $targetFile, $false)
        if ((Get-FileHash -LiteralPath $targetFile -Algorithm SHA256).Hash -ne $hashes[$relative]) {
            throw "Hash verification failed (source may have changed): $relative"
        }
    }
} catch {
    Write-Warning 'Export is incomplete. The new directory was retained for inspection; do not publish it.'
    throw
}
Write-Output ('Export passed: {0} allowlisted files copied and SHA256 verified.' -f $files.Count)
Write-Output ('Destination: {0}' -f $targetRoot)
Write-Output 'No Git repository was initialized and nothing was uploaded.'
