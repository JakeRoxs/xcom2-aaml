param(
    [Parameter(Mandatory = $true)][ValidateSet('win-x64', 'linux-x64')][string]$Rid,
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true)][string]$Repository,
    [Parameter(Mandatory = $true)][string]$Commit,
    [switch]$WindowsSingleFile,
    [switch]$VerifyTrackedSourceInputs
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src/AAML.Avalonia/AAML.Avalonia.csproj'
$singleFile = $Rid -eq 'win-x64' -and $WindowsSingleFile
if ($WindowsSingleFile -and $Rid -ne 'win-x64') { throw 'WindowsSingleFile is valid only for win-x64 staging.' }

if ($VerifyTrackedSourceInputs) {
    $sourceInputs = @(
        'eng/linux/aaml-proton-launch-option.sh',
        'eng/linux/io.github.jakeroxs.xcom2_aaml.desktop',
        'eng/linux/io.github.jakeroxs.xcom2_aaml.metainfo.xml',
        'eng/generate-brand-assets.ps1',
        'eng/test-brand-assets.ps1',
        'eng/package-manifests/aaml-linux-x64.json',
        'eng/package-manifests/aaml-win-x64.json',
        'eng/package-manifests/aaml-win-x64-single-file.json',
        'eng/generate-release-evidence.ps1',
        'eng/convert-single-file-evidence.ps1',
        'eng/dotnet-bundle.psm1',
        'eng/test-release-evidence.ps1',
        'eng/test-release-license-catalog.ps1',
        'eng/release-supply-chain-policy.json',
        'eng/stage-aaml-package.ps1',
        'eng/test-aaml-archive.ps1',
        'eng/test-release-workflow.ps1',
        'eng/validate-aaml-artifact.ps1',
        '.github/workflows/release.yml',
        'src/AAML.Avalonia/packages.lock.json',
        'assets/branding/aaml-icon.svg',
        'assets/branding/provenance.json',
        'assets/branding/generated/asset-manifest.json',
        'assets/branding/generated/aaml.ico',
        'src/AAML.Infrastructure.Steam/steamworks-manifest.json',
        'src/ThirdParty/Steamworks.NET/LICENSE.txt',
        'src/ThirdParty/redistributable_bin/linux64/libsteam_api.so',
        'src/ThirdParty/redistributable_bin/win64/steam_api64.dll',
        'tools/AAML.ProtonWrapper/packages.lock.json',
        'LICENSE'
    )
    $sourceInputs += @(Get-ChildItem -LiteralPath (Join-Path $root 'assets/branding/generated/png') -File | ForEach-Object { [System.IO.Path]::GetRelativePath($root, $_.FullName).Replace('\', '/') })
    $sourceInputs += @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'license-sources') -File -Recurse | ForEach-Object { [System.IO.Path]::GetRelativePath($root, $_.FullName).Replace('\', '/') })
    $sourceInputs += @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'license-texts') -File -Recurse | ForEach-Object { [System.IO.Path]::GetRelativePath($root, $_.FullName).Replace('\', '/') })
    foreach ($sourceInput in $sourceInputs) {
        git -C $root ls-files --error-unmatch -- $sourceInput 2>$null | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Package source input is not tracked by git: $sourceInput" }
    }
}

if (Test-Path -LiteralPath $OutputDirectory) {
    Remove-Item -LiteralPath $OutputDirectory -Recurse -Force
}
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$informationalVersion = if ($Version.Contains('+', [StringComparison]::Ordinal)) { "$Version.$Commit" } else { "$Version+$Commit" }

$evidenceArtifactDirectory = $OutputDirectory
if ($singleFile) {
    $evidenceArtifactDirectory = "$OutputDirectory-evidence-source"
    if (Test-Path -LiteralPath $evidenceArtifactDirectory) { Remove-Item -LiteralPath $evidenceArtifactDirectory -Recurse -Force }
    New-Item -ItemType Directory -Path $evidenceArtifactDirectory -Force | Out-Null
}

dotnet publish $project -c Release -r $Rid --self-contained true --no-restore `
    -p:PublishSingleFile=false -p:PublishTrimmed=false -p:PublishAot=false `
    -p:Version=$Version -p:InformationalVersion=$informationalVersion -p:ContinuousIntegrationBuild=true `
    -p:DebugType=None -p:DebugSymbols=false -o $evidenceArtifactDirectory
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $Rid" }

if ($singleFile) {
    dotnet publish $project -c Release -r $Rid --self-contained true --no-restore `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:IncludeAllContentForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true -p:PublishTrimmed=false -p:PublishAot=false `
        -p:Version=$Version -p:InformationalVersion=$informationalVersion -p:ContinuousIntegrationBuild=true `
        -p:DebugType=None -p:DebugSymbols=false -o $OutputDirectory
    if ($LASTEXITCODE -ne 0) { throw "Single-file publish failed for $Rid" }
    Import-Module (Join-Path $PSScriptRoot 'dotnet-bundle.psm1') -Force
    $bundlePath = Join-Path $OutputDirectory 'AAML.Avalonia.exe'
    $bundleEntries = @{}; foreach ($entry in Get-DotNetBundleEntries -BundlePath $bundlePath) { $bundleEntries[$entry.Path] = $entry }
    foreach ($graphFile in @('AAML.Avalonia.deps.json', 'AAML.Avalonia.runtimeconfig.json')) {
        if (-not $bundleEntries.ContainsKey($graphFile)) { throw "Single-file bundle has no embedded deployment graph: $graphFile" }
        Export-DotNetBundleEntry -BundlePath $bundlePath -Entry $bundleEntries[$graphFile] -DestinationPath (Join-Path $evidenceArtifactDirectory $graphFile)
    }
    Get-ChildItem -LiteralPath $evidenceArtifactDirectory -File | Where-Object {
        $_.Name -match '\.(?:dll|exe)$' -and $_.Name -ne 'AAML.Avalonia.exe' -and -not $bundleEntries.ContainsKey($_.Name)
    } | Remove-Item -Force
}

if ($Rid -eq 'linux-x64') {
    $wrapperOutput = Join-Path $OutputDirectory 'tools/proton-wrapper'
    dotnet publish (Join-Path $root 'tools/AAML.ProtonWrapper/AAML.ProtonWrapper.csproj') -c Release -r $Rid --self-contained true --no-restore `
        -p:PublishSingleFile=false -p:PublishTrimmed=false -p:PublishAot=false `
        -p:DebugType=None -p:DebugSymbols=false -o $wrapperOutput
    if ($LASTEXITCODE -ne 0) { throw "Proton wrapper publish failed for $Rid" }
    $setupDirectory = Join-Path $OutputDirectory 'tools/setup'
    New-Item -ItemType Directory -Path $setupDirectory -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $root 'eng/linux/aaml-proton-launch-option.sh') -Destination $setupDirectory
}

Get-ChildItem -LiteralPath $OutputDirectory -Recurse -Force | Where-Object {
    $_.Name -match '\.(?:pdb|mdb|dbg|dSYM)$'
} | Remove-Item -Recurse -Force
if ($evidenceArtifactDirectory -ne $OutputDirectory) {
    Get-ChildItem -LiteralPath $evidenceArtifactDirectory -Recurse -Force | Where-Object {
        $_.Name -match '\.(?:pdb|mdb|dbg|dSYM)$'
    } | Remove-Item -Recurse -Force
}

$licenses = Join-Path $OutputDirectory 'licenses'
New-Item -ItemType Directory -Path $licenses -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $root 'src/ThirdParty/Steamworks.NET/LICENSE.txt') -Destination (Join-Path $licenses 'Steamworks.NET-LICENSE.txt')
Copy-Item -LiteralPath (Join-Path $root 'LICENSE') -Destination (Join-Path $licenses 'AAML-GPL-3.0.txt')
Copy-Item -LiteralPath (Join-Path $root 'src/AAML.Infrastructure.Steam/steamworks-manifest.json') -Destination $OutputDirectory

$brandingDirectory = Join-Path $OutputDirectory 'branding'
New-Item -ItemType Directory -Path $brandingDirectory -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $root 'assets/branding/aaml-icon.svg') -Destination $brandingDirectory
Copy-Item -LiteralPath (Join-Path $root 'assets/branding/provenance.json') -Destination $brandingDirectory
Copy-Item -LiteralPath (Join-Path $root 'assets/branding/generated/asset-manifest.json') -Destination $brandingDirectory

if ($Rid -eq 'win-x64') {
    if ($singleFile) {
        $evidenceLicenses = Join-Path $evidenceArtifactDirectory 'licenses'
        New-Item -ItemType Directory -Path $evidenceLicenses -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $licenses 'Steamworks.NET-LICENSE.txt') -Destination $evidenceLicenses
        Copy-Item -LiteralPath (Join-Path $licenses 'AAML-GPL-3.0.txt') -Destination $evidenceLicenses
        Copy-Item -LiteralPath (Join-Path $root 'src/AAML.Infrastructure.Steam/steamworks-manifest.json') -Destination $evidenceArtifactDirectory
    }
    Copy-Item -LiteralPath (Join-Path $root 'src/ThirdParty/redistributable_bin/win64/steam_api64.dll') -Destination $evidenceArtifactDirectory
    Copy-Item -LiteralPath (Join-Path $root 'assets/branding/generated/aaml.ico') -Destination $brandingDirectory
} else {
    Copy-Item -LiteralPath (Join-Path $root 'src/ThirdParty/redistributable_bin/linux64/libsteam_api.so') -Destination $OutputDirectory
    $applicationsDirectory = Join-Path $OutputDirectory 'share/applications'
    $metainfoDirectory = Join-Path $OutputDirectory 'share/metainfo'
    New-Item -ItemType Directory -Path $applicationsDirectory -Force | Out-Null
    New-Item -ItemType Directory -Path $metainfoDirectory -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $root 'eng/linux/io.github.jakeroxs.xcom2_aaml.desktop') -Destination $applicationsDirectory
    Copy-Item -LiteralPath (Join-Path $root 'eng/linux/io.github.jakeroxs.xcom2_aaml.metainfo.xml') -Destination $metainfoDirectory
    foreach ($size in @(16, 32, 48, 64, 128, 256, 512)) {
        $iconDirectory = Join-Path $OutputDirectory "share/icons/hicolor/${size}x$size/apps"
        New-Item -ItemType Directory -Path $iconDirectory -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $root "assets/branding/generated/png/aaml-$size.png") -Destination (Join-Path $iconDirectory 'io.github.jakeroxs.xcom2_aaml.png')
    }
    $scalableDirectory = Join-Path $OutputDirectory 'share/icons/hicolor/scalable/apps'
    New-Item -ItemType Directory -Path $scalableDirectory -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $root 'assets/branding/aaml-icon.svg') -Destination (Join-Path $scalableDirectory 'io.github.jakeroxs.xcom2_aaml.svg')
    if (-not $IsWindows) {
        chmod +x (Join-Path $OutputDirectory 'AAML.Avalonia')
        chmod +x (Join-Path $OutputDirectory 'tools/proton-wrapper/AAML.ProtonWrapper')
        chmod +x (Join-Path $OutputDirectory 'tools/setup/aaml-proton-launch-option.sh')
    }
}

[ordered]@{
    schemaVersion = 1
    product = 'Avalonia Alternative Mod Launcher'
    shorthand = 'AAML'
    version = $Version
    rid = $Rid
    repository = $Repository
    commit = $Commit
    selfContained = $true
    singleFile = $singleFile
    trimmed = $false
    nativeAot = $false
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputDirectory 'release-metadata.json') -Encoding utf8

# Evidence must inventory the completed payload. Checksums are generated later by the validator.
& (Join-Path $PSScriptRoot 'generate-release-evidence.ps1') -Rid $Rid -ArtifactDirectory $evidenceArtifactDirectory -OutputDirectory $OutputDirectory -Version $Version -Repository $Repository -Commit $Commit
if (-not $?) { throw "Release evidence generation failed for $Rid" }

if ($singleFile) {
    $evidenceDirectory = Join-Path $OutputDirectory 'evidence'
    New-Item -ItemType Directory -Path $evidenceDirectory -Force | Out-Null
    $sourceDepsPath = Join-Path $evidenceArtifactDirectory 'AAML.Avalonia.deps.json'
    $packagedDepsPath = Join-Path $evidenceDirectory 'AAML.Avalonia.deps.json'
    $sourceRuntimeConfigPath = Join-Path $evidenceArtifactDirectory 'AAML.Avalonia.runtimeconfig.json'
    $packagedRuntimeConfigPath = Join-Path $evidenceDirectory 'AAML.Avalonia.runtimeconfig.json'
    Copy-Item -LiteralPath $sourceDepsPath -Destination $packagedDepsPath
    Copy-Item -LiteralPath $sourceRuntimeConfigPath -Destination $packagedRuntimeConfigPath
    & (Join-Path $PSScriptRoot 'convert-single-file-evidence.ps1') -ArtifactDirectory $OutputDirectory -SourceArtifactDirectory $evidenceArtifactDirectory -BundlePath (Join-Path $OutputDirectory 'AAML.Avalonia.exe') -SourceDepsPath $sourceDepsPath -PackagedDepsPath $packagedDepsPath -SourceRuntimeConfigPath $sourceRuntimeConfigPath -PackagedRuntimeConfigPath $packagedRuntimeConfigPath
    if (-not $?) { throw 'Single-file evidence conversion failed.' }
    Remove-Item -LiteralPath $evidenceArtifactDirectory -Recurse -Force
}
