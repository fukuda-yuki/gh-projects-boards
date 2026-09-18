param(
    [string]$Configuration = 'Release',
    [switch]$NoBuild,
    [switch]$Open
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$run = Join-Path $repo ('TestResults/coverage/' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $run | Out-Null
$project = Join-Path $repo 'tests/GhProjectsBoards.Tests/GhProjectsBoards.Tests.csproj'
$settings = Join-Path $repo 'tests/coverage.runsettings'
Push-Location $repo
try {
    if (-not $NoBuild) {
        & dotnet build $project -c $Configuration
        if ($LASTEXITCODE -ne 0) { throw 'Core test project build failed' }
    }
    & dotnet test $project -c $Configuration --no-build --filter 'TestCategory!=LiveGitHub' --logger "trx;LogFileName=coverage.trx" --results-directory $run --collect 'Code Coverage;Format=cobertura' --settings $settings
    if ($LASTEXITCODE -ne 0) { throw "Core tests failed. See $run" }
    # Microsoft Code Coverage also writes an identical copy under ...\In\<machine>\.
    $reports = @(Get-ChildItem -LiteralPath $run -Recurse -Filter '*.cobertura.xml' | Where-Object { $_.FullName -notmatch '[\\/]In[\\/]' })
    if ($reports.Count -eq 0) { throw "No Cobertura coverage file was produced in $run" }
    & dotnet tool restore
    if ($LASTEXITCODE -ne 0) { throw 'dotnet tool restore failed' }
    $reportDir = Join-Path $run 'report'
    $reportArg = ($reports | ForEach-Object { $_.FullName }) -join ';'
    & dotnet tool run reportgenerator -- "-reports:$reportArg" "-targetdir:$reportDir" "-reporttypes:Html;TextSummary" "-assemblyfilters:+GhProjectsBoards.Core" "-sourcedirs:src/GhProjectsBoards.Core" "-title:GhProjectsBoards.Core"
    if ($LASTEXITCODE -ne 0) { throw 'ReportGenerator failed' }
    $htm = Join-Path $reportDir 'index.htm'
    if (Test-Path -LiteralPath $htm) { Copy-Item -LiteralPath $htm (Join-Path $reportDir 'index.html') }
    $summary = Join-Path $reportDir 'Summary.txt'
    if (Test-Path -LiteralPath $summary) { Get-Content -LiteralPath $summary }
    Write-Host "Coverage report: $reportDir"
    if ($Open) {
        $index = Join-Path $reportDir 'index.html'
        if (-not (Test-Path -LiteralPath $index)) { $index = $htm }
        Start-Process $index
    }
}
finally { Pop-Location }
