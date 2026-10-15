# Exact CLI archive identity reviewed for the pinned SDK 1.0.22 (CLI 1.0.98).
function Get-CopilotRuntimeOrigins([string]$CacheDirectory, [string]$PayloadRoot,
    [string]$ExpectedSha256 = '10516FEC874797519E2ECE05E90BCD627327C42C688AA21F44B7D13FA1130036') {
    $archivePath = Join-Path $CacheDirectory 'copilot.tgz'
    if ((Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash -ne $ExpectedSha256) { throw 'Copilot archive SHA-256 mismatch.' }
    $entries = @{}
    $stream = [IO.File]::OpenRead($archivePath)
    $gzip = [IO.Compression.GZipStream]::new($stream, [IO.Compression.CompressionMode]::Decompress)
    $tar = [System.Formats.Tar.TarReader]::new($gzip)
    try {
        while ($null -ne ($entry = $tar.GetNextEntry())) {
            if ($null -eq $entry.DataStream) { continue }
            $name = $entry.Name.Replace('\', '/')
            if (-not $name.StartsWith('package/') -or $name.Split('/') -contains '..') { throw 'Unsafe Copilot archive path.' }
            $relative = $name.Substring(8)
            if ($entries.ContainsKey($relative)) { throw 'Duplicate Copilot archive path.' }
            $entries[$relative] = @{ path = $name; sha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($entry.DataStream)) }
        }
    } finally { $tar.Dispose(); $gzip.Dispose(); $stream.Dispose() }
    if (-not $entries.ContainsKey('LICENSE.md') -or
        (Get-FileHash -LiteralPath (Join-Path $CacheDirectory 'LICENSE.md')).Hash -ne $entries['LICENSE.md'].sha256) {
        throw 'Copilot cached licence differs from the verified archive.'
    }
    $native = Join-Path $PayloadRoot 'runtimes/win-x64/native'
    foreach ($required in @('copilot-runtime.exe', 'runtime.node')) {
        if (-not (Test-Path -LiteralPath (Join-Path $native $required) -PathType Leaf)) { throw "Missing Copilot runtime payload: $required" }
    }
    $origins = @{}
    foreach ($file in (Get-ChildItem -LiteralPath $native -Recurse -File)) {
        $relative = [IO.Path]::GetRelativePath($native, $file.FullName).Replace('\', '/')
        # The SDK flattens this platform's prebuilds beside the stdio wrapper and aliases runtime.node.
        $source = if ($relative -eq 'copilot_runtime.dll') { 'prebuilds/win32-x64/runtime.node' }
            elseif ($relative -eq 'copilot.exe') { 'prebuilds/win32-x64/copilot-runtime.exe' }
            elseif ($entries.ContainsKey("prebuilds/win32-x64/$relative")) { "prebuilds/win32-x64/$relative" }
            else { $relative }
        if (-not $entries.ContainsKey($source)) { throw "Unknown Copilot runtime origin: $relative" }
        $original = $entries[$source]
        $cached = Join-Path $CacheDirectory $source
        if (-not (Test-Path -LiteralPath $cached -PathType Leaf) -or (Get-FileHash -LiteralPath $cached).Hash -ne $original.sha256) {
            throw "Copilot cached extraction differs: $source"
        }
        if ((Get-FileHash -LiteralPath $file.FullName).Hash -ne $original.sha256) { throw "Copilot runtime payload differs: $relative" }
        $origins["runtimes/win-x64/native/$relative"] = "github/copilot-cli/v1.0.98/github-copilot-1.0.98-win32-x64.tgz!$($original.path)"
    }
    return $origins
}
