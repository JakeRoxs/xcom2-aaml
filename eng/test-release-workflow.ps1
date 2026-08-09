Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$workflowPath = Join-Path (Split-Path -Parent $PSScriptRoot) '.github/workflows/release.yml'
$workflow = Get-Content -LiteralPath $workflowPath -Raw
$validator = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'validate-aaml-artifact.ps1') -Raw
$policy = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'release-supply-chain-policy.json') -Raw

function Assert-Match([string]$Text, [string]$Pattern, [string]$Message) {
    if ($Text -notmatch $Pattern) { throw $Message }
}
function Assert-NotMatch([string]$Text, [string]$Pattern, [string]$Message) {
    if ($Text -match $Pattern) { throw $Message }
}

Assert-Match $workflow '(?m)^\s+tags:\s*\r?\n\s+- ''v\*''' 'Release workflow must trigger on v* tags.'
Assert-Match $workflow '(?m)^\s+workflow_dispatch:' 'Release workflow must support manual dispatch.'
Assert-NotMatch $workflow '(?m)^\s+pull_request(?:_target)?:' 'Release workflow must never run for pull requests.'
Assert-Match $workflow 'github\.repository.*-ne.*JakeRoxs/xcom2-aaml' 'Canonical repository guard is missing.'
Assert-Match $workflow 'git diff --quiet' 'Clean tracked commit provenance check is missing.'
Assert-Match $workflow 'rid:\s*\[win-x64, linux-x64\]' 'Release staging must use the exact RID matrix.'
Assert-NotMatch $workflow 'release-windows-signing|AAML_WINDOWS_SIGNING|sign-aaml-windows|signtool' 'Unsigned release workflow must not require signing environments, secrets, or tools.'
Assert-Match $workflow 'permissions:\s*\r?\n\s+contents:\s*read\s*\r?\n\s+id-token:\s*write' 'Finalization jobs must use read plus id-token permissions.'
Assert-Match $workflow 'actions/attest-build-provenance@' 'Final archives must receive build provenance attestations.'
Assert-Match $workflow '-OfficialRelease' 'Both final artifacts must run official validation.'
Assert-Match $workflow 'test-aaml-archive\.ps1' 'Final archives must be extracted and revalidated.'
Assert-Match $workflow 'test-brand-assets\.ps1' 'Release staging must verify deterministic first-party brand assets.'
Assert-Match $workflow 'WindowsSingleFile:' 'Official Windows staging must enable the clean single-file package.'
Assert-Match $workflow 'aaml-win-x64-single-file\.json' 'Official Windows finalization must validate the clean single-file manifest.'
Assert-Match $workflow "tar -C '\$\{\{ runner\.temp \}\}/AAML Stage' -czf '\$\{\{ runner\.temp \}\}/AAML-stage-linux-x64\.tar\.gz' linux-x64" 'Linux stage transport must preserve permissions in a tar archive.'
Assert-Match $workflow 'archive:\s*false' 'Linux stage tar must be uploaded without a permission-losing wrapper archive.'
Assert-Match $workflow 'name:\s*AAML-stage-linux-x64\.tar\.gz' 'Linux finalization must download the filename-derived unwrapped artifact name.'
Assert-Match $workflow "tar -C '\$\{\{ runner\.temp \}\}/AAML Stage' -xzf '\$\{\{ runner\.temp \}\}/AAML Transport/AAML-stage-linux-x64\.tar\.gz'" 'Linux finalization must extract the permission-preserving stage archive.'
Assert-Match $policy '"requiredForPublicRelease"\s*:\s*false' 'Windows signing policy must explicitly permit unsigned public releases.'
Assert-Match $policy '"status"\s*:\s*"not-required"' 'Windows signing policy must be not-required.'
Assert-NotMatch $validator 'Get-AuthenticodeSignature' 'Official validation must not require Authenticode.'

$checksumIndex = $workflow.IndexOf('-GenerateChecksums -OfficialRelease', [StringComparison]::Ordinal)
$archiveIndex = $workflow.IndexOf('Compress-Archive', $checksumIndex, [StringComparison]::Ordinal)
$attestIndex = $workflow.IndexOf('actions/attest-build-provenance@', $archiveIndex, [StringComparison]::Ordinal)
if ($checksumIndex -lt 0 -or $archiveIndex -lt $checksumIndex -or $attestIndex -lt $archiveIndex) { throw 'Windows release order must be official checksums/validation, archive, attest.' }

'Validated fail-closed unsigned official release workflow and provenance policy.'
