#!/usr/bin/env pwsh
# Remove PDBs and other non-essential files from single-file publish output
param(
    [string]$PublishDir
)

$ErrorActionPreference = 'Stop'

Write-Host "Cleaning PDBs from: $PublishDir"

$pdbFiles = Get-ChildItem -LiteralPath $PublishDir -Filter '*.pdb' -File -ErrorAction SilentlyContinue
foreach ($file in $pdbFiles) {
    Write-Host "Removing: $($file.Name)"
    Remove-Item -LiteralPath $file.FullName -Force
}

Write-Host "Files remaining:"
Get-ChildItem -LiteralPath $PublishDir -File | Select-Object Name, Length | Format-Table -AutoSize

Write-Host "Done."
