[CmdletBinding()]
param([Parameter(Mandatory = $true)][string]$OutputPath)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$target = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputPath)
if ([IO.Path]::GetExtension($target) -ne '.vsix') { throw 'OutputPath must name a .vsix file.' }
$parent = Split-Path -Parent $target
if (-not (Test-Path -LiteralPath $parent -PathType Container)) { throw 'The output directory must already exist.' }
$entries = [ordered]@{
    '[Content_Types].xml' = '[Content_Types].xml'
    'extension.vsixmanifest' = 'extension.vsixmanifest'
    'extension/package.json' = 'package.json'
    'extension/extension.js' = 'extension.js'
    'extension/README.md' = 'README.md'
}
foreach ($entry in $entries.GetEnumerator()) {
    if (-not (Test-Path -LiteralPath (Join-Path $PSScriptRoot $entry.Value) -PathType Leaf)) { throw ('Missing package source: ' + $entry.Value) }
}
$stream = [IO.File]::Open($target, [IO.FileMode]::Create, [IO.FileAccess]::Write, [IO.FileShare]::None)
try {
    $archive = New-Object IO.Compression.ZipArchive($stream, [IO.Compression.ZipArchiveMode]::Create, $true)
    try {
        foreach ($entry in $entries.GetEnumerator()) {
            $null = [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, (Join-Path $PSScriptRoot $entry.Value), $entry.Key, [IO.Compression.CompressionLevel]::Optimal)
        }
    } finally { $archive.Dispose() }
} finally { $stream.Dispose() }
Write-Output ('Packaged: ' + $target)
