param(
    [Parameter(Mandatory = $true)][string]$ArtifactDirectory,
    [Parameter(Mandatory = $true)][string]$EvidenceDirectory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if (-not $IsWindows) { throw 'Read-only installation qualification requires Windows.' }
$artifact = (Resolve-Path -LiteralPath $ArtifactDirectory).Path
$evidence = [System.IO.Path]::GetFullPath($EvidenceDirectory)
if ($artifact -notmatch '[^\x00-\x7f]') { throw 'Read-only installation qualification requires a non-ASCII installation path.' }
if ($evidence.StartsWith($artifact + [System.IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase))
    { throw 'Evidence must be outside the read-only installation.' }
New-Item -ItemType Directory -Path $evidence -Force | Out-Null

$identity = [System.Security.Principal.WindowsIdentity]::GetCurrent().User
if ($null -eq $identity) { throw 'The current Windows identity has no SID.' }
$acl = Get-Acl -LiteralPath $artifact
$originalSddl = $acl.GetSecurityDescriptorSddlForm([System.Security.AccessControl.AccessControlSections]::All)
$files = @(Get-ChildItem -LiteralPath $artifact -File -Recurse | ForEach-Object { [pscustomobject]@{ File = $_; WasReadOnly = $_.IsReadOnly } })
$rights = [System.Security.AccessControl.FileSystemRights]::Write -bor
    [System.Security.AccessControl.FileSystemRights]::Delete -bor
    [System.Security.AccessControl.FileSystemRights]::DeleteSubdirectoriesAndFiles
$rule = [System.Security.AccessControl.FileSystemAccessRule]::new(
    $identity, $rights,
    [System.Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit',
    [System.Security.AccessControl.PropagationFlags]::None,
    [System.Security.AccessControl.AccessControlType]::Deny)
$acl.AddAccessRule($rule) | Out-Null

try {
    foreach ($entry in $files) { $entry.File.IsReadOnly = $true }
    Set-Acl -LiteralPath $artifact -AclObject $acl
    $probe = Join-Path $artifact 'write-probe.tmp'
    $writeRejected = $false
    try { [System.IO.File]::WriteAllText($probe, 'must fail') }
    catch [System.UnauthorizedAccessException] { $writeRejected = $true }
    finally { if (Test-Path -LiteralPath $probe) { Remove-Item -LiteralPath $probe -Force } }
    if (-not $writeRejected) { throw 'Installation ACL still permits file creation.' }

    & (Join-Path $PSScriptRoot 'run-windows-desktop-smoke.ps1') -ArtifactDirectory $artifact -EvidenceDirectory (Join-Path $evidence 'desktop-smoke')
    if (-not $?) { throw 'Desktop smoke failed from the read-only installation.' }
    [ordered]@{
        schemaVersion = 1
        artifactDirectory = $artifact
        containsNonAsciiPath = $artifact -match '[^\x00-\x7f]'
        effectiveSddl = (Get-Acl -LiteralPath $artifact).GetSecurityDescriptorSddlForm([System.Security.AccessControl.AccessControlSections]::All)
        fileCount = $files.Count
        completedAtUtc = [DateTimeOffset]::UtcNow
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $evidence 'readonly-install-evidence.json') -Encoding utf8
}
finally {
    $restored = [System.Security.AccessControl.DirectorySecurity]::new()
    $restored.SetSecurityDescriptorSddlForm($originalSddl)
    Set-Acl -LiteralPath $artifact -AclObject $restored
    foreach ($entry in $files) { $entry.File.IsReadOnly = $entry.WasReadOnly }
}

'Windows read-only Unicode installation smoke passed.'
