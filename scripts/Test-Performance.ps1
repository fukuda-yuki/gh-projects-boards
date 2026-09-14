param(
    [ValidateSet('Synthetic')][string]$Mode = 'Synthetic',
    [string]$RunId = ('run-' + (Get-Date -Format 'yyyyMMdd-HHmmss')),
    [switch]$NoBuild
)
$ErrorActionPreference = 'Stop'
if ($RunId -notmatch '^[a-zA-Z0-9_-]+$') { throw 'Invalid run ID' }
$repo = Split-Path $PSScriptRoot -Parent
$root = Join-Path $repo "TestResults/performance/$RunId"
if (Test-Path -LiteralPath $root) { throw 'Run already exists; never overwrite evidence' }
New-Item -ItemType Directory -Path $root | Out-Null
$matrix = @(@(100,0,$false,5,$true), @(100,10,$false,3,$true), @(101,10,$false,3,$true), @(1000,10,$false,3,$true), @(100,100,$false,3,$true), @(100,10,$true,3,$true), @(100,10,$false,3,$false))
@{ source = (git -C $repo rev-parse HEAD); diff = (git -C $repo diff); matrix = $matrix; mode = $Mode; started = [DateTimeOffset]::Now.ToString('o'); policy = 'One warmup per case; all individual samples retained; compare median and range; no tail percentile or machine CI threshold.' } | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $root 'manifest.json') -Encoding utf8
if (!$NoBuild) {
    dotnet build (Join-Path $repo 'tests/GhProjectsBoards.Tests/GhProjectsBoards.Tests.csproj') -c Release *> (Join-Path $root 'build.log')
    if ($LASTEXITCODE) { throw 'Build failed; evidence retained' }
}
$exe = Join-Path $repo 'tests/GhProjectsBoards.Tests/bin/Release/net10.0-windows/GhProjectsBoards.Tests.exe'
Get-FileHash $exe, (Join-Path (Split-Path $exe) 'GhProjectsBoards.Core.dll') | ConvertTo-Json | Set-Content (Join-Path $root 'hashes.json')
for ($i = 0; $i -lt $matrix.Count; $i++) {
    $case = $matrix[$i]
    & $exe --performance (Join-Path $root "case-$i") $case[0] $case[1] $case[2] $case[3] $case[4] *> (Join-Path $root "case-$i.log")
    if ($LASTEXITCODE) { throw "Case $i failed; all evidence retained in $root" }
}
Write-Output $root
