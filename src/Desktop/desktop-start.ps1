param([Parameter(Mandatory = $true)][string]$Specification)
$ErrorActionPreference = 'Stop'
# Select the Windows PowerShell modules explicitly. A parent PowerShell 7 process
# can leave a PSModulePath whose auto-loading manifests are incompatible with 5.1.
foreach ($moduleName in @('Microsoft.PowerShell.Management', 'Microsoft.PowerShell.Utility', 'Microsoft.PowerShell.Security', 'Appx')) {
    Import-Module ([IO.Path]::Combine($PSHOME, 'Modules', $moduleName, ($moduleName + '.psd1'))) -ErrorAction Stop
}
$specPath = [IO.Path]::GetFullPath($Specification)
$specFile = Get-Item -LiteralPath $specPath
if (($specFile.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or $specFile.Length -gt 131072) { throw 'Invalid desktop launch metadata.' }
for ($parentDirectory = $specFile.Directory; $null -ne $parentDirectory; $parentDirectory = $parentDirectory.Parent) {
    if (($parentDirectory.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Redirected desktop launch metadata is not supported.' }
}
$metadataOwner = (Get-Acl -LiteralPath $specPath).Owner
$currentIdentity = [Security.Principal.WindowsIdentity]::GetCurrent()
if ((New-Object Security.Principal.NTAccount($metadataOwner)).Translate([Security.Principal.SecurityIdentifier]).Value -ne $currentIdentity.User.Value) { throw 'Desktop metadata owner changed.' }
# The native writer emits UTF-8 without a BOM. Windows PowerShell 5.1 otherwise
# decodes using the computer's legacy code page, corrupting Unicode user paths.
$specificationData = Get-Content -LiteralPath $specPath -Raw -Encoding UTF8 | ConvertFrom-Json
if ($specificationData.ProtocolVersion -ne 1 -or [IO.Path]::GetFileName($specPath) -ne 'launch.json') { throw 'Invalid desktop launch specification.' }
$installedPackage = Get-AppxPackage -Name OpenAI.Codex | Where-Object { $_.PackageFullName -eq $specificationData.PackageFullName -and $_.PackageFamilyName -eq $specificationData.PackageFamilyName }
if ($null -eq $installedPackage) { throw 'The installed Codex package changed.' }
$expectedHost = Join-Path $PSScriptRoot 'DesktopRuntimeHost.exe'
if (![String]::Equals([IO.Path]::GetFullPath($specificationData.HostExecutable), [IO.Path]::GetFullPath($expectedHost), [StringComparison]::OrdinalIgnoreCase)) { throw 'Desktop host path changed.' }
if ($specificationData.AppId -ne 'App') { throw 'Unexpected desktop package application.' }
# Only structured -File arguments and the package cmdlet are used; no user text becomes -Command code.
Invoke-CommandInDesktopPackage -PackageFamilyName $installedPackage.PackageFamilyName -AppId $specificationData.AppId -Command $expectedHost -Args ('--inside-package "' + $specPath + '"') -PreventBreakaway
