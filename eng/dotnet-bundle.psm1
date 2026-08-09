Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-SafeBundlePath {
    param([Parameter(Mandatory = $true)][string]$Path)
    $normalized = $Path.Replace('\', '/')
    $segments = @($normalized.Split('/'))
    if ($Path.Contains('\') -or [System.IO.Path]::IsPathRooted($Path) -or $segments.Count -eq 0 -or @($segments | Where-Object { [string]::IsNullOrWhiteSpace($_) -or $_ -in @('.', '..') }).Count -gt 0) { throw "Unsafe .NET bundle entry path: $Path" }
    return $normalized
}

function Get-DotNetBundleEntries {
    param([Parameter(Mandatory = $true)][string]$BundlePath)

    $bundle = (Resolve-Path -LiteralPath $BundlePath).Path
    $dotnetRoot = Split-Path -Parent (Get-Command dotnet).Source
    $sdkVersion = (& dotnet --version).Trim()
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($sdkVersion)) { throw 'Unable to identify the active .NET SDK.' }
    $hostModelPath = Join-Path $dotnetRoot "sdk/$sdkVersion/Microsoft.NET.HostModel.dll"
    if (-not (Test-Path -LiteralPath $hostModelPath -PathType Leaf)) { throw "The active SDK has no host model assembly: $hostModelPath" }
    $hostModel = [System.Reflection.Assembly]::LoadFrom($hostModelPath)
    $bundlerType = $hostModel.GetType('Microsoft.NET.HostModel.Bundle.Bundler', $true)
    $isBundle = $bundlerType.GetMethod('IsBundle', [System.Reflection.BindingFlags]'Public,Static')
    if ($null -eq $isBundle) { throw 'The active SDK does not expose the required bundle identification API.' }
    $arguments = @($bundle, [long]0)
    if (-not $isBundle.Invoke($null, $arguments)) { throw "Executable is not a valid .NET single-file bundle: $bundle" }
    $headerOffset = [long]$arguments[1]

    $stream = [System.IO.File]::Open($bundle, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::Read)
    $reader = [System.IO.BinaryReader]::new($stream, [System.Text.Encoding]::UTF8, $true)
    try {
        if ($headerOffset -le 0 -or $headerOffset -ge $stream.Length) { throw "Invalid .NET bundle header offset: $headerOffset" }
        $stream.Position = $headerOffset
        $major = $reader.ReadUInt32()
        $minor = $reader.ReadUInt32()
        if ($major -ne 6) { throw "Unsupported .NET bundle manifest version: $major.$minor" }
        $count = $reader.ReadInt32()
        if ($count -le 0 -or $count -gt 10000) { throw "Invalid .NET bundle entry count: $count" }
        $bundleId = $reader.ReadString()
        if ([string]::IsNullOrWhiteSpace($bundleId)) { throw 'The .NET bundle ID is empty.' }
        $null = $reader.ReadInt64(); $null = $reader.ReadInt64()
        $null = $reader.ReadInt64(); $null = $reader.ReadInt64()
        $null = $reader.ReadUInt64()

        $seen = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
        $entries = [System.Collections.Generic.List[object]]::new()
        [long]$totalSize = 0
        for ($index = 0; $index -lt $count; $index++) {
            $offset = $reader.ReadInt64()
            $size = $reader.ReadInt64()
            $compressedSize = $reader.ReadInt64()
            $type = $reader.ReadByte()
            $relativePath = Assert-SafeBundlePath $reader.ReadString()
            $storedSize = if ($compressedSize -gt 0) { $compressedSize } else { $size }
            if ($offset -lt 0 -or $size -lt 0 -or $size -gt 512MB -or $compressedSize -lt 0 -or $storedSize -gt [int]::MaxValue -or $offset + $storedSize -gt $headerOffset) {
                throw "Invalid .NET bundle entry bounds: $relativePath"
            }
            $totalSize += $size
            if ($totalSize -gt 2GB) { throw 'The .NET bundle uncompressed payload exceeds the validation limit.' }
            if (-not $seen.Add($relativePath)) { throw "Duplicate .NET bundle entry path: $relativePath" }
            $entries.Add([pscustomobject]@{ BundlePath = $bundle; BundleId = $bundleId; Major = $major; Minor = $minor; Path = $relativePath; Offset = $offset; Size = $size; CompressedSize = $compressedSize; Type = $type })
        }
        return @($entries)
    }
    finally {
        $reader.Dispose()
        $stream.Dispose()
    }
}

function Get-DotNetBundleEntryBytes {
    param(
        [Parameter(Mandatory = $true)][string]$BundlePath,
        [Parameter(Mandatory = $true)][object]$Entry
    )

    $stream = [System.IO.File]::Open((Resolve-Path -LiteralPath $BundlePath).Path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::Read)
    try {
        $stream.Position = [long]$Entry.Offset
        $storedSize = if ([long]$Entry.CompressedSize -gt 0) { [long]$Entry.CompressedSize } else { [long]$Entry.Size }
        if ($storedSize -gt [int]::MaxValue) { throw "Bundle entry is too large to validate: $($Entry.Path)" }
        $bytes = [byte[]]::new([int]$storedSize)
        $read = 0
        while ($read -lt $bytes.Length) {
            $count = $stream.Read($bytes, $read, $bytes.Length - $read)
            if ($count -eq 0) { throw "Unexpected end of bundle while reading: $($Entry.Path)" }
            $read += $count
        }
        if ([long]$Entry.CompressedSize -gt 0) {
            $compressed = [System.IO.MemoryStream]::new($bytes, $false)
            $decompressed = [System.IO.MemoryStream]::new()
            try {
                $deflate = [System.IO.Compression.DeflateStream]::new($compressed, [System.IO.Compression.CompressionMode]::Decompress, $true)
                try { $deflate.CopyTo($decompressed) } finally { $deflate.Dispose() }
                if ($decompressed.Length -ne [long]$Entry.Size) { throw "Decompressed bundle entry size mismatch: $($Entry.Path)" }
                return ,$decompressed.ToArray()
            }
            finally {
                $decompressed.Dispose()
                $compressed.Dispose()
            }
        }
        if ($bytes.LongLength -ne [long]$Entry.Size) { throw "Bundle entry size mismatch: $($Entry.Path)" }
        return ,$bytes
    }
    finally { $stream.Dispose() }
}

function Get-DotNetBundleEntryHash {
    param(
        [Parameter(Mandatory = $true)][string]$BundlePath,
        [Parameter(Mandatory = $true)][object]$Entry
    )
    $bytes = Get-DotNetBundleEntryBytes -BundlePath $BundlePath -Entry $Entry
    return [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
}

function Export-DotNetBundleEntry {
    param(
        [Parameter(Mandatory = $true)][string]$BundlePath,
        [Parameter(Mandatory = $true)][object]$Entry,
        [Parameter(Mandatory = $true)][string]$DestinationPath
    )
    $bytes = Get-DotNetBundleEntryBytes -BundlePath $BundlePath -Entry $Entry
    [System.IO.File]::WriteAllBytes($DestinationPath, $bytes)
}

Export-ModuleMember -Function Get-DotNetBundleEntries, Get-DotNetBundleEntryBytes, Get-DotNetBundleEntryHash, Export-DotNetBundleEntry
