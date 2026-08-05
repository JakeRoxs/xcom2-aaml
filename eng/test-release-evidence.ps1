Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$generator = Join-Path $PSScriptRoot 'generate-release-evidence.ps1'
$scratch = Join-Path ([System.IO.Path]::GetTempPath()) "aaml-release-evidence-$([guid]::NewGuid().ToString('N'))"
$repository = 'JakeRoxs/xcom2-dark-launcher'
$commit = '0000000000000000000000000000000000000000'

function Write-Bytes {
    param([string]$Path, [byte[]]$Bytes)
    $parent = Split-Path -Parent $Path
    New-Item -ItemType Directory -Path $parent -Force | Out-Null
    [System.IO.File]::WriteAllBytes($Path, $Bytes)
}

function Add-FixturePackage {
    param([string]$NuGetRoot, [string]$Name, [string]$Version, [hashtable]$Files)
    $directory = Join-Path (Join-Path $NuGetRoot $Name.ToLowerInvariant()) $Version
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    foreach ($entry in $Files.GetEnumerator()) { Write-Bytes (Join-Path $directory $entry.Key) $entry.Value }
    'fixture-content-hash' | Set-Content -LiteralPath (Join-Path $directory "$($Name.ToLowerInvariant()).$Version.nupkg.sha512") -Encoding ascii
}

function New-DepsLibrary {
    param([string]$Type, [hashtable]$Runtime, [hashtable]$Native, [hashtable]$Resources, [hashtable]$RuntimeTargets)
    $result = [ordered]@{}
    if ($Runtime) { $result.runtime = $Runtime }
    if ($Native) { $result.native = $Native }
    if ($Resources) { $result.resources = $Resources }
    if ($RuntimeTargets) { $result.runtimeTargets = $RuntimeTargets }
    return $result
}

function Add-FixtureApp {
    param([string]$Artifact, [string]$RelativeDirectory, [string]$Name, [string]$Rid, [hashtable]$TargetLibraries, [hashtable]$Libraries)
    $directory = Join-Path $Artifact $RelativeDirectory
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    Write-Bytes (Join-Path $directory $Name) ([byte[]](0x7f, 0x45, 0x4c, 0x46, 1, 2, 3, 4))
    $deps = [ordered]@{
        runtimeTarget = [ordered]@{ name = ".NETCoreApp,Version=v10.0/$Rid" }
        targets = [ordered]@{ ".NETCoreApp,Version=v10.0/$Rid" = $TargetLibraries }
        libraries = $Libraries
    }
    $deps | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $directory "$Name.deps.json") -Encoding utf8
}

function New-Fixture {
    param([string]$Name)
    $root = Join-Path $scratch $Name
    $artifact = Join-Path $root 'artifact'
    $nuget = Join-Path $root 'nuget'
    New-Item -ItemType Directory -Path (Join-Path $artifact 'licenses') -Force | Out-Null
    'MIT' | Set-Content -LiteralPath (Join-Path $artifact 'licenses/Steamworks.NET-LICENSE.txt') -Encoding ascii
    $managed = [byte[]](1, 2, 3, 4)
    $native = [byte[]](5, 6, 7, 8)
    $resource = [byte[]](9, 10, 11, 12)
    Add-FixturePackage $nuget 'Fixture.Runtime' '1.0.0' @{
        'lib/net10.0/Fixture.Runtime.dll' = $managed
        'runtimes/linux-x64/native/libfixture.so' = $native
        'lib/net10.0/fr/Fixture.Runtime.resources.dll' = $resource
    }
    Add-FixturePackage $nuget 'Fixture.Tool' '2.0.0' @{ 'lib/net10.0/Fixture.Tool.dll' = $managed }
    $rootTargets = [ordered]@{
        'AAML.Avalonia/1.0.0' = (New-DepsLibrary project @{ 'AAML.Avalonia.dll' = @{} } @{} @{} @{})
        'Fixture.Runtime/1.0.0' = (New-DepsLibrary package @{ 'lib/net10.0/Fixture.Runtime.dll' = @{} } @{} @{ 'lib/net10.0/fr/Fixture.Runtime.resources.dll' = @{} } @{ 'runtimes/linux-x64/native/libfixture.so' = @{ rid = 'linux-x64'; assetType = 'native' }; 'runtimes/win-x64/native/ignored.dll' = @{ rid = 'win-x64'; assetType = 'native' } })
        'Fixture.BuildOnly/9.0.0' = (New-DepsLibrary package @{} @{} @{} @{})
    }
    $rootLibraries = [ordered]@{
        'AAML.Avalonia/1.0.0' = @{ type = 'project'; serviceable = $false; sha512 = '' }
        'Fixture.Runtime/1.0.0' = @{ type = 'package'; serviceable = $true; sha512 = 'fixture-content-hash' }
        'Fixture.BuildOnly/9.0.0' = @{ type = 'package'; serviceable = $true; sha512 = 'not-restored' }
    }
    Add-FixtureApp $artifact '' 'AAML.Avalonia' 'linux-x64' $rootTargets $rootLibraries
    Write-Bytes (Join-Path $artifact 'AAML.Avalonia.dll') $managed
    Write-Bytes (Join-Path $artifact 'Fixture.Runtime.dll') $managed
    Write-Bytes (Join-Path $artifact 'libfixture.so') $native
    Write-Bytes (Join-Path $artifact 'fr/Fixture.Runtime.resources.dll') $resource
    $toolTargets = [ordered]@{
        'AAML.SteamProbe/1.0.0' = (New-DepsLibrary project @{ 'AAML.SteamProbe.dll' = @{} } @{} @{} @{})
        'Fixture.Tool/2.0.0' = (New-DepsLibrary package @{ 'lib/net10.0/Fixture.Tool.dll' = @{} } @{} @{} @{})
    }
    $toolLibraries = [ordered]@{
        'AAML.SteamProbe/1.0.0' = @{ type = 'project'; serviceable = $false; sha512 = '' }
        'Fixture.Tool/2.0.0' = @{ type = 'package'; serviceable = $true; sha512 = 'fixture-content-hash' }
    }
    Add-FixtureApp $artifact 'tools/steam-probe' 'AAML.SteamProbe' 'linux-x64' $toolTargets $toolLibraries
    Write-Bytes (Join-Path $artifact 'tools/steam-probe/AAML.SteamProbe.dll') $managed
    Write-Bytes (Join-Path $artifact 'tools/steam-probe/Fixture.Tool.dll') $managed
    $wrapperTargets = [ordered]@{
        'AAML.ProtonWrapper/1.0.0' = (New-DepsLibrary project @{ 'AAML.ProtonWrapper.dll' = @{} } @{} @{} @{})
        'Fixture.Tool/2.0.0' = (New-DepsLibrary package @{ 'lib/net10.0/Fixture.Tool.dll' = @{} } @{} @{} @{})
    }
    $wrapperLibraries = [ordered]@{
        'AAML.ProtonWrapper/1.0.0' = @{ type = 'project'; serviceable = $false; sha512 = '' }
        'Fixture.Tool/2.0.0' = @{ type = 'package'; serviceable = $true; sha512 = 'fixture-content-hash' }
    }
    Add-FixtureApp $artifact 'tools/proton-wrapper' 'AAML.ProtonWrapper' 'linux-x64' $wrapperTargets $wrapperLibraries
    Write-Bytes (Join-Path $artifact 'tools/proton-wrapper/AAML.ProtonWrapper.dll') $managed
    Write-Bytes (Join-Path $artifact 'tools/proton-wrapper/Fixture.Tool.dll') $managed
    $valve = [byte[]](13, 14, 15, 16)
    Write-Bytes (Join-Path $artifact 'libsteam_api.so') $valve
    $valveHash = (Get-FileHash -LiteralPath (Join-Path $artifact 'libsteam_api.so') -Algorithm SHA256).Hash.ToLowerInvariant()
    [ordered]@{
        steamworksSdkVersion = 'fixture'
        steamworksNetCommit = 'fixture'
        nativeAssets = [ordered]@{ 'linux-x64' = [ordered]@{ file = 'libsteam_api.so'; size = 4; sha256 = $valveHash } }
        redistribution = 'fixture terms'
    } | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $artifact 'steamworks-manifest.json') -Encoding utf8
    return [pscustomobject]@{ Root = $root; Artifact = $artifact; NuGet = $nuget }
}

function Invoke-FixtureGeneration {
    param([object]$Fixture)
    & $generator -Rid linux-x64 -ArtifactDirectory $Fixture.Artifact -OutputDirectory $Fixture.Artifact -NuGetPackagesDirectory $Fixture.NuGet -Version 1.0.0-test -Repository $repository -Commit $commit | Out-Null
}

function Assert-Failure {
    param([string]$Name, [scriptblock]$Mutation, [string]$Expected)
    $fixture = New-Fixture $Name
    & $Mutation $fixture
    $failed = $false
    try { Invoke-FixtureGeneration $fixture } catch {
        $failed = $true
        if ($_.Exception.Message -notmatch $Expected) { throw "Test '$Name' returned unexpected failure: $($_.Exception.Message)" }
    }
    if (-not $failed) { throw "Test '$Name' expected generation to fail." }
}

try {
    New-Item -ItemType Directory -Path $scratch | Out-Null
    $valid = New-Fixture 'valid'
    Invoke-FixtureGeneration $valid
    $sbom = Get-Content -LiteralPath (Join-Path $valid.Artifact 'sbom.cdx.json') -Raw | ConvertFrom-Json
    $names = @($sbom.components.name)
    foreach ($expected in @('AAML.Avalonia', 'AAML.ProtonWrapper', 'AAML.SteamProbe', 'Fixture.Runtime', 'Fixture.Tool', 'Steamworks.NET', 'libsteam_api.so')) {
        if ($names -notcontains $expected) { throw "Valid fixture omitted component: $expected" }
    }
    if ($names -contains 'Fixture.BuildOnly') { throw 'Build-only package entered the shipped closure.' }
    $runtime = @($sbom.components | Where-Object name -eq 'Fixture.Runtime')[0]
    $assets = @($runtime.properties | Where-Object name -eq 'aaml:shipped-asset' | ForEach-Object value)
    $assetText = $assets -join "`n"
    if ($assetText -notmatch 'fr/Fixture.Runtime.resources.dll' -or $assetText -notmatch 'libfixture.so') { throw 'Resource/native/flattened fixture evidence is incomplete.' }

    Assert-Failure 'wrong-rid' { param($f) (Get-Content (Join-Path $f.Artifact 'AAML.Avalonia.deps.json') -Raw).Replace('linux-x64', 'win-x64') | Set-Content (Join-Path $f.Artifact 'AAML.Avalonia.deps.json') } 'does not match staged RID'
    Assert-Failure 'missing' { param($f) Remove-Item (Join-Path $f.Artifact 'Fixture.Runtime.dll') } 'asset is missing'
    Assert-Failure 'unattributed' { param($f) Write-Bytes (Join-Path $f.Artifact 'unknown.dll') ([byte[]](1)) } 'unattributed'
    Assert-Failure 'tampered' { param($f) (Get-Content (Join-Path $f.Artifact 'AAML.Avalonia.deps.json') -Raw).Replace('fixture-content-hash', 'tampered') | Set-Content (Join-Path $f.Artifact 'AAML.Avalonia.deps.json') } 'content hash is tampered'
    Assert-Failure 'collision' { param($f)
        $path = Join-Path $f.Artifact 'AAML.Avalonia.deps.json'; $deps = Get-Content $path -Raw | ConvertFrom-Json
        @($deps.targets.PSObject.Properties)[0].Value | Add-Member -NotePropertyName 'Fixture.Other/1.0.0' -NotePropertyValue @{ runtime = @{ 'lib/net10.0/Fixture.Runtime.dll' = @{} } }
        $deps.libraries | Add-Member -NotePropertyName 'Fixture.Other/1.0.0' -NotePropertyValue @{ type = 'project'; sha512 = '' }
        $deps | ConvertTo-Json -Depth 20 | Set-Content $path
    } 'multiply attributed'
    Assert-Failure 'version-conflict' { param($f)
        Add-FixturePackage $f.NuGet 'Fixture.Runtime' '2.0.0' @{ 'lib/net10.0/Fixture.Runtime.dll' = ([byte[]](1, 2, 3, 4)) }
        $path = Join-Path $f.Artifact 'tools/steam-probe/AAML.SteamProbe.deps.json'; $deps = Get-Content $path -Raw | ConvertFrom-Json
        @($deps.targets.PSObject.Properties)[0].Value | Add-Member -NotePropertyName 'Fixture.Runtime/2.0.0' -NotePropertyValue @{ runtime = @{ 'lib/net10.0/Fixture.Runtime.dll' = @{} } }
        $deps.libraries | Add-Member -NotePropertyName 'Fixture.Runtime/2.0.0' -NotePropertyValue @{ type = 'package'; sha512 = 'fixture-content-hash' }
        Write-Bytes (Join-Path $f.Artifact 'tools/steam-probe/Fixture.Runtime.dll') ([byte[]](1, 2, 3, 4))
        $deps | ConvertTo-Json -Depth 20 | Set-Content $path
    } 'Conflicting package versions'
    'Validated staged closure fixtures: multi-app, RID filtering, build exclusion, native/resource/flattened assets, and fail-closed attribution/provenance cases.'
}
finally {
    if (Test-Path -LiteralPath $scratch) { Remove-Item -LiteralPath $scratch -Recurse -Force }
}
