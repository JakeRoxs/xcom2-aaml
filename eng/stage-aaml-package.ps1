param(
    [Parameter(Mandatory = $true)][ValidateSet('win-x64', 'linux-x64')][string]$Rid,
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true)][string]$Repository,
    [Parameter(Mandatory = $true)][string]$Commit,
    [switch]$VerifyTrackedSourceInputs
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src/AAML.Avalonia/AAML.Avalonia.csproj'

if ($VerifyTrackedSourceInputs) {
    $sourceInputs = @(
        'eng/linux/aaml-proton-launch-option.sh',
        'eng/package-manifests/aaml-linux-x64.json',
        'eng/package-manifests/aaml-win-x64.json',
        'eng/generate-release-evidence.ps1',
        'eng/test-release-evidence.ps1',
        'eng/test-release-license-catalog.ps1',
        'eng/release-supply-chain-policy.json',
        'eng/stage-aaml-package.ps1',
        'eng/test-aaml-archive.ps1',
        'eng/test-release-workflow.ps1',
        'eng/validate-aaml-artifact.ps1',
        '.github/workflows/release.yml',
        'src/AAML.Avalonia/packages.lock.json',
        'src/AAML.Infrastructure.Steam/steamworks-manifest.json',
        'src/ThirdParty/Steamworks.NET/LICENSE.txt',
        'src/ThirdParty/redistributable_bin/linux64/libsteam_api.so',
        'src/ThirdParty/redistributable_bin/win64/steam_api64.dll',
        'tools/AAML.ProtonWrapper/packages.lock.json',
        'tools/AAML.SteamProbe/packages.lock.json',
        'LICENSE'
    )
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

dotnet publish $project -c Release -r $Rid --self-contained true --no-restore `
    -p:PublishSingleFile=false -p:PublishTrimmed=false -p:PublishAot=false `
    -p:DebugType=None -p:DebugSymbols=false -o $OutputDirectory
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $Rid" }

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

if ($Rid -eq 'linux-x64') {
    $probeOutput = Join-Path $OutputDirectory 'tools/steam-probe'
    dotnet publish (Join-Path $root 'tools/AAML.SteamProbe/AAML.SteamProbe.csproj') -c Release -r $Rid --self-contained true --no-restore `
        -p:PublishSingleFile=false -p:PublishTrimmed=false -p:PublishAot=false `
        -p:DebugType=None -p:DebugSymbols=false -o $probeOutput
    if ($LASTEXITCODE -ne 0) { throw "Steam probe publish failed for $Rid" }
}

Get-ChildItem -LiteralPath $OutputDirectory -Recurse -Force | Where-Object {
    $_.Name -match '\.(?:pdb|mdb|dbg|dSYM)$'
} | Remove-Item -Recurse -Force

$licenses = Join-Path $OutputDirectory 'licenses'
New-Item -ItemType Directory -Path $licenses -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $root 'src/ThirdParty/Steamworks.NET/LICENSE.txt') -Destination (Join-Path $licenses 'Steamworks.NET-LICENSE.txt')
Copy-Item -LiteralPath (Join-Path $root 'LICENSE') -Destination (Join-Path $licenses 'AAML-GPL-3.0.txt')
Copy-Item -LiteralPath (Join-Path $root 'src/AAML.Infrastructure.Steam/steamworks-manifest.json') -Destination $OutputDirectory

if ($Rid -eq 'win-x64') {
    Copy-Item -LiteralPath (Join-Path $root 'src/ThirdParty/redistributable_bin/win64/steam_api64.dll') -Destination $OutputDirectory
} else {
    Copy-Item -LiteralPath (Join-Path $root 'src/ThirdParty/redistributable_bin/linux64/libsteam_api.so') -Destination $OutputDirectory
    if (-not $IsWindows) {
        chmod +x (Join-Path $OutputDirectory 'AAML.Avalonia')
        chmod +x (Join-Path $OutputDirectory 'tools/proton-wrapper/AAML.ProtonWrapper')
        chmod +x (Join-Path $OutputDirectory 'tools/steam-probe/AAML.SteamProbe')
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
    singleFile = $false
    trimmed = $false
    nativeAot = $false
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputDirectory 'release-metadata.json') -Encoding utf8

# Evidence must inventory the completed payload. Checksums are generated later by the validator.
& (Join-Path $PSScriptRoot 'generate-release-evidence.ps1') -Rid $Rid -ArtifactDirectory $OutputDirectory -OutputDirectory $OutputDirectory -Version $Version -Repository $Repository -Commit $Commit
if (-not $?) { throw "Release evidence generation failed for $Rid" }
