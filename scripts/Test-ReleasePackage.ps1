#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory, Position = 0)][string]$PackageRoot,
    [Parameter(Mandatory, Position = 1)][string]$ExpectedCommit,
    [Parameter(Mandatory, Position = 2)][string]$ExpectedVersion
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = (Resolve-Path -LiteralPath $PackageRoot).Path
$manifest = Get-Content -LiteralPath (Join-Path $root 'distribution-manifest.json') -Raw | ConvertFrom-Json
if ($manifest.format -ne 1 -or $manifest.sourceCommit -cne $ExpectedCommit -or
    $manifest.version -cne $ExpectedVersion -or $manifest.sourceDirty -ne $false) {
    throw 'Release source/version does not match the clean selected commit.'
}
$paths = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($file in $manifest.files) {
    $relative = [string]$file.path
    if (-not $relative -or $relative -match '(^/|\\|:|(^|/)\.\.?(/|$))') { throw "Unsafe payload path: $relative" }
    if (-not $paths.Add($relative)) { throw "Duplicate payload path: $relative" }
    $path = Join-Path $root $relative
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing payload: $relative" }
    if ((Get-Item -LiteralPath $path).Length -ne $file.bytes -or
        (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $file.sha256) { throw "Payload differs: $relative" }
}
foreach ($file in Get-ChildItem -LiteralPath $root -Recurse -File) {
    $relative = [IO.Path]::GetRelativePath($root, $file.FullName).Replace('\', '/')
    if ($relative -ne 'distribution-manifest.json' -and -not $paths.Contains($relative)) { throw "Unexpected payload: $relative" }
}
foreach ($required in @('GhProjectsBoards.App.exe', 'GhProjectsBoards.App.dll', 'coreclr.dll', 'hostpolicy.dll',
    'Microsoft.UI.Xaml.dll', 'GhProjectsBoards.App.pri', 'App.xbf', '使い方.md', 'マニュアル.md', 'templates/new-tasks.csv')) {
    if (-not $paths.Contains($required)) { throw "Required payload missing: $required" }
}
if (@($paths | Where-Object { $_.StartsWith('notices/') }).Count -eq 0) { throw 'Required payload missing: notices' }
if (@($paths | Where-Object { $_ -match '^manual/images/[^/]+\.png$' }).Count -eq 0) { throw 'Required payload missing: manual/images/*.png' }
Join-Path $root 'GhProjectsBoards.App.exe'
