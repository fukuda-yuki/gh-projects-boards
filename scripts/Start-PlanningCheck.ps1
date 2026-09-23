[CmdletBinding()]
param(
    [ValidateSet('Fresh','Weekly','Load')][string]$Scenario = 'Weekly',
    [string]$DataRoot,
    [string]$FrozenLaunch,
    [switch]$Resume,
    [switch]$PrepareOnly
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$app = Join-Path $repo 'src/GhProjectsBoards.App/bin/Release/net10.0-windows10.0.26100.0/win-x64/GhProjectsBoards.App.exe'
$seed = Join-Path $repo 'tests/GhProjectsBoards.Tests/bin/Release/net10.0-windows/GhProjectsBoards.Tests.exe'
if (-not $DataRoot) { $DataRoot = Join-Path $repo ('TestResults/planning-evaluation-' + [guid]::NewGuid().ToString('N')) }
if (-not [IO.Path]::IsPathFullyQualified($DataRoot)) { throw 'DataRoot must be absolute.' }
$DataRoot = [IO.Path]::GetFullPath($DataRoot)
$marker = Join-Path $DataRoot 'diagnostics/planning-evaluation.json'
if ($Resume) {
    if (-not (Test-Path -LiteralPath $marker)) { throw 'Resume requires an existing isolated planning evaluation root.' }
    $evaluation = Get-Content -Raw -LiteralPath $marker | ConvertFrom-Json
    $identifier = [guid]::Empty
    if ($evaluation.synthetic -ne $true -or $evaluation.validatedReadback -ne $true -or
        -not [guid]::TryParse([string]$evaluation.isolationId, [ref]$identifier) -or
        $identifier -eq [guid]::Empty -or -not $evaluation.evaluationRoot -or
        -not [string]::Equals([IO.Path]::GetFullPath($evaluation.evaluationRoot), $DataRoot, [StringComparison]::OrdinalIgnoreCase) -or
        $evaluation.scenario -notin @('Fresh','Weekly','Load')) { throw 'The evaluation marker does not identify this isolated synthetic root. Existing data was not changed.' }
    $Scenario = $evaluation.scenario
    if (-not $FrozenLaunch) {
        $prior = Get-ChildItem -LiteralPath (Split-Path $marker) -Filter 'launch-*.json' -File |
            Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
        if (-not $prior -or (Get-Content -Raw -LiteralPath $prior.FullName | ConvertFrom-Json).receiptVersion -ne 2) {
            throw 'This evaluation has no frozen launch receipt. Supply FrozenLaunch explicitly; existing data was not changed.'
        }
        $FrozenLaunch = $prior.FullName
    }
} else {
    if (Test-Path -LiteralPath $DataRoot) { throw 'Existing data is never overwritten. Use Resume for a prepared evaluation root.' }
}
# A new candidate is built from this checkout. Further scenarios and resumes can
# use its immutable receipt without silently rebuilding a different executable.
function BuildInputs {
    @(git -C $repo ls-files --cached --others --exclude-standard | Where-Object {
        $_ -match '^(src/|tests/GhProjectsBoards\.)' -or $_ -match '\.(props|targets|sln|slnx)$' -or $_ -eq 'global.json'
    } | Sort-Object -Unique | ForEach-Object { [ordered]@{ path = $_; sha256 = (Get-FileHash -LiteralPath (Join-Path $repo $_)).Hash } })
}
function BinaryFiles([string]$executable) {
    $root = Split-Path $executable -Parent
    @(Get-ChildItem -LiteralPath $root -File -Recurse | Sort-Object FullName | ForEach-Object {
        [ordered]@{ path = [IO.Path]::GetRelativePath($root, $_.FullName); sha256 = (Get-FileHash -LiteralPath $_.FullName).Hash }
    })
}
function VerifyBinaryFiles([string]$executable, $expected) {
    if (-not [IO.Path]::IsPathFullyQualified($executable) -or -not (Test-Path -LiteralPath $executable -PathType Leaf) -or -not $expected) {
        throw 'Frozen launch receipt has no complete executable identity. No evaluation was started.'
    }
    $actual = BinaryFiles $executable
    if (($actual | ConvertTo-Json -Depth 3 -Compress) -ne ($expected | ConvertTo-Json -Depth 3 -Compress)) {
        throw "Frozen executable files changed: $executable. No evaluation data was changed and no app was launched."
    }
}
$binding = $null
if ($FrozenLaunch) {
    $FrozenLaunch = (Resolve-Path -LiteralPath $FrozenLaunch).Path
    $frozen = Get-Content -Raw -LiteralPath $FrozenLaunch | ConvertFrom-Json
    if ($frozen.receiptVersion -ne 2 -or $frozen.source -notmatch '^[a-f0-9]{40}$' -or
        $frozen.flags.recycledPresentation -notin @('0','1')) { throw 'Use a frozen launch receipt produced by this launcher.' }
    $app = [string]$frozen.executable; $seed = [string]$frozen.seedExecutable
    VerifyBinaryFiles $app $frozen.appFiles
    VerifyBinaryFiles $seed $frozen.seedFiles
    $source = $frozen.source; $inputs = $frozen.buildInputs; $builds = $frozen.builds; $changes = $frozen.changes
    $recycled = $frozen.flags.recycledPresentation
    $appFiles = $frozen.appFiles; $seedFiles = $frozen.seedFiles
    $binding = @{ path = $FrozenLaunch; sha256 = (Get-FileHash -LiteralPath $FrozenLaunch).Hash }
} else {
    $source = git -C $repo rev-parse HEAD
    $inputs = BuildInputs
    $before = $inputs | ConvertTo-Json -Depth 3 -Compress
    $buildRoot = Join-Path $repo ('TestResults/planning-build-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $buildRoot | Out-Null
    $builds = @()
    foreach ($project in @('src/GhProjectsBoards.App/GhProjectsBoards.App.csproj','tests/GhProjectsBoards.Tests/GhProjectsBoards.Tests.csproj')) {
        $buildLog = Join-Path $buildRoot ([IO.Path]::GetFileNameWithoutExtension($project) + '.log')
        $buildArguments = @('build', (Join-Path $repo $project), '-c', 'Release', '-t:Rebuild')
        & dotnet @buildArguments *> $buildLog
        $builds += @{ command = @('dotnet') + $buildArguments; exitCode = $LASTEXITCODE; log = $buildLog }
        if ($LASTEXITCODE -ne 0) { throw "Evaluation build failed. Retained log: $buildLog" }
    }
    if ($before -ne ((BuildInputs) | ConvertTo-Json -Depth 3 -Compress) -or $source -ne (git -C $repo rev-parse HEAD)) {
        throw 'Source changed while building. No evaluation was started. Run the launcher again from a stable checkout.'
    }
    $changes = @(git -C $repo status --porcelain)
    Copy-Item -LiteralPath (Split-Path $app -Parent) -Destination (Join-Path $buildRoot 'app') -Recurse
    Copy-Item -LiteralPath (Split-Path $seed -Parent) -Destination (Join-Path $buildRoot 'seed') -Recurse
    $app = Join-Path $buildRoot 'app/GhProjectsBoards.App.exe'; $seed = Join-Path $buildRoot 'seed/GhProjectsBoards.Tests.exe'
    $appFiles = BinaryFiles $app; $seedFiles = BinaryFiles $seed
    $recycled = if ($env:GHPB_RECYCLED_PRESENTATION -eq '1') { '1' } else { '0' }
}
if (-not $Resume) {
    & $seed --seed-planning-check $DataRoot $Scenario
    if ($LASTEXITCODE -ne 0) { throw 'Fixture preparation failed.' }
}
$manifest = Join-Path $DataRoot ('diagnostics/launch-' + [guid]::NewGuid().ToString('N') + '.json')
@{
    receiptVersion = 2; createdAt = [DateTimeOffset]::UtcNow.ToString('O'); source = $source; changes = $changes; scenario = $Scenario; buildInputs = $inputs; builds = $builds
    frozenLaunch = $binding; launchSource = (git -C $repo rev-parse HEAD); launchScriptSha256 = (Get-FileHash -LiteralPath $PSCommandPath).Hash
    appFiles = $appFiles; seedFiles = $seedFiles; flags = @{ recycledPresentation = $recycled; sheetDiagnostics = 'off'; imeTrace = 'off' }
    dataRoot = $DataRoot; executable = $app; appSha256 = (Get-FileHash -LiteralPath (Join-Path (Split-Path $app) 'GhProjectsBoards.App.dll')).Hash
    executableSha256 = (Get-FileHash -LiteralPath $app).Hash
    coreSha256 = (Get-FileHash -LiteralPath (Join-Path (Split-Path $app) 'GhProjectsBoards.Core.dll')).Hash
    seedExecutable = $seed; seedSha256 = (Get-FileHash -LiteralPath $seed).Hash; os = [Environment]::OSVersion.VersionString
    preparedOnly = $PrepareOnly.IsPresent; humanAcceptance = 'not_run'
} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $manifest
Write-Host "Synthetic $Scenario data: $DataRoot"
Write-Host "Frozen launch receipt: $manifest"
Write-Host 'Select the saved github.com / ID 42 profile and P1. No connection check is needed. P2 checks Project switching.'
Write-Host 'Fresh: configure field mappings and U1 weight once, then enter Estimate. Weekly: Actual 5 to 7, Remaining 4 to 3, and minute dates. Load: 1,000 tasks / 20 people with retained legacy plans.'
if (-not $PrepareOnly) {
    $start = [Diagnostics.ProcessStartInfo]::new($app)
    $start.UseShellExecute = $false; $start.WorkingDirectory = Split-Path $app -Parent
    $start.Environment['GHPB_DATA_ROOT'] = $DataRoot
    $start.Environment['GH_CONFIG_DIR'] = Join-Path $DataRoot 'diagnostics/empty-gh-config'
    $start.Environment['GHPB_RECYCLED_PRESENTATION'] = $recycled
    foreach ($key in @('GH_TOKEN','GITHUB_TOKEN','GH_ENTERPRISE_TOKEN','GITHUB_ENTERPRISE_TOKEN','GHPB_SHEET_DIAGNOSTICS','GHPB_IME_TRACE')) { $start.Environment.Remove($key) | Out-Null }
    $process = [Diagnostics.Process]::Start($start)
    Write-Host "Ordinary application PID: $($process.Id)"
}
