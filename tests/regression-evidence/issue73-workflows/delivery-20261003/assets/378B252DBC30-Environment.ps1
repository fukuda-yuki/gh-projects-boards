$ErrorActionPreference = 'Stop'
$evaluationRoot = [IO.Path]::GetFullPath($PSScriptRoot)
$preparation = Get-Content -LiteralPath (Join-Path $evaluationRoot 'diagnostics/preparation.json') -Raw | ConvertFrom-Json
if ($preparation.evaluationRoot -ne $evaluationRoot) { throw 'Evaluation root identity mismatch.' }
function Assert-FileManifest([string]$directory, $expected) {
    $prefix = [IO.Path]::GetFullPath($directory).TrimEnd('\') + '\'
    $actual = @(Get-ChildItem -LiteralPath $directory -Recurse -File)
    if ($actual.Count -ne $expected.Count) { throw 'Frozen file count mismatch.' }
    if (@($expected.path | Sort-Object -Unique).Count -ne $expected.Count) { throw 'Duplicate frozen file path.' }
    foreach ($entry in $expected) {
        $path = [IO.Path]::GetFullPath((Join-Path $directory $entry.path))
        if (-not $path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -or $entry.sha256 -notmatch '^[A-Fa-f0-9]{64}$') { throw 'Invalid frozen file entry.' }
        if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $entry.sha256) { throw "Frozen file mismatch: $($entry.path)" }
    }
}
function New-IsolatedStart([string]$executable) {
    $start = [Diagnostics.ProcessStartInfo]::new($executable)
    $start.UseShellExecute = $false
    $start.WorkingDirectory = Split-Path $executable
    foreach ($name in @($start.Environment.Keys)) {
        if ($name -match '^(GHPB_|GH_|GITHUB_|CORECLR_|COR_)' -or
            $name -in @('DEBUG','DOTNET_STARTUP_HOOKS','DOTNET_ADDITIONAL_DEPS','HTTP_PROXY','HTTPS_PROXY','ALL_PROXY')) {
            [void]$start.Environment.Remove($name)
        }
    }
    $start.Environment['PATH'] = (Split-Path $preparation.fakeGh) + ';' + (Join-Path $env:SystemRoot 'System32') + ';' + $env:SystemRoot
    $start.Environment['GH_CONFIG_DIR'] = $preparation.ghConfig
    $start.Environment['GH_PROMPT_DISABLED'] = '1'
    $start.Environment['GH_NO_UPDATE_NOTIFIER'] = '1'
    $start.Environment['GH_NO_EXTENSION_UPDATE_NOTIFIER'] = '1'
    $start.Environment['GH_TELEMETRY'] = 'false'
    $start.Environment['GHPB_DATA_ROOT'] = $preparation.dataRoot
    $start.Environment['GHPB_RECYCLED_PRESENTATION'] = '1'
    return $start
}
