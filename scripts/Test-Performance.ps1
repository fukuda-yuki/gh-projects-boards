param(
    [ValidateSet('Synthetic','Desktop','Live','AdapterStage')][string]$Mode = 'Synthetic',
    [string]$RunId = ('run-' + (Get-Date -Format 'yyyyMMdd-HHmmss')),
    [string]$Executable,
    [ValidatePattern('^[a-fA-F0-9]{40}$')][string]$SourceRevision,
    [string]$ReferenceRoot,
    [ValidateSet('Legacy','Apply50','CandidateCheck')][string]$Workload = 'Legacy',
    [ValidateSet('Initialize','Prepare','Verify','Measure','Restore','Cleanup')][string]$Stage = 'Verify',
    [ValidateSet('main','candidate')][string]$Label = 'candidate',
    [string]$FixtureRoot,
    [string]$PlanPath,
    [switch]$NoBuild
)
$ErrorActionPreference = 'Stop'
if ($RunId -notmatch '^[a-zA-Z0-9_-]+$') { throw 'Invalid run ID' }
$repo = Split-Path $PSScriptRoot -Parent
$root = Join-Path $repo "TestResults/performance/$RunId"
if (Test-Path -LiteralPath $root) { throw 'Run already exists; never overwrite evidence' }
New-Item -ItemType Directory -Path $root | Out-Null
@{ cpu = (Get-CimInstance Win32_Processor | Select-Object Name,NumberOfCores,NumberOfLogicalProcessors); os = [Environment]::OSVersion.ToString(); sdk = (dotnet --version); powershell = $PSVersionTable.PSVersion.ToString(); network = 'Synthetic has no network; Live records are contextual only'; packaging = 'Unpackaged Release' } | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $root 'environment.json')
if ($Mode -eq 'AdapterStage') {
    if (!$NoBuild -or !$Executable) { throw 'Use an explicitly frozen executable and -NoBuild; the fixed plan verifies its hashes.' }
    $exe = (Resolve-Path -LiteralPath $Executable).Path
    @{ source = (git -C $repo rev-parse HEAD); stage = $Stage; label = $Label; fixtureRoot = $FixtureRoot; executable = $exe; started = [DateTimeOffset]::UtcNow.ToString('o') } | ConvertTo-Json | Set-Content (Join-Path $root 'manifest.json')
    Get-FileHash $exe, (Join-Path (Split-Path $exe) 'GhProjectsBoards.Tests.dll'), (Join-Path (Split-Path $exe) 'GhProjectsBoards.Core.dll') | ConvertTo-Json | Set-Content (Join-Path $root 'hashes.json')
    $stageArguments = if ($Stage -eq 'Initialize') { @('--performance-init', (Resolve-Path -LiteralPath $PlanPath).Path) }
        else { @('--performance-stage', (Resolve-Path -LiteralPath $FixtureRoot).Path, (Join-Path $root 'adapter'), $Stage, $Label) }
    @{ executable = $exe; arguments = $stageArguments } | ConvertTo-Json | Set-Content (Join-Path $root 'command.json')
    & $exe @stageArguments 2>&1 | Tee-Object -FilePath (Join-Path $root 'stage.log')
    $stageExit = $LASTEXITCODE
    if ($stageExit -eq 3) { Write-Output "Budget deferred; retained checkpoint and continuation: $root"; return }
    if ($stageExit) { throw "Stage failed; retain $root and inspect before any continuation. No automatic setup or mutation retry." }
    Write-Output $root
    return
}
if ($Mode -ne 'Synthetic') {
    @{ source = (git -C $repo rev-parse HEAD); mode = $Mode; started = [DateTimeOffset]::Now.ToString('o') } | ConvertTo-Json | Set-Content (Join-Path $root 'manifest.json')
    if ($Mode -eq 'Live') {
        # Existing runners retain exact-resource, interrupted-run and complete-baseline guards.
        & (Join-Path $PSScriptRoot 'Test-ApplyLive.ps1') *> (Join-Path $root 'apply-live.log')
        & (Join-Path $PSScriptRoot 'Test-CreationLive.ps1') *> (Join-Path $root 'creation-live.log')
    } else {
        $previous = $env:GHPB_PERFORMANCE_UI_ROOT
        try {
            $env:GHPB_PERFORMANCE_UI_ROOT = $root
            & (Join-Path $PSScriptRoot 'Test-E2E.ps1') -Filter 'FullyQualifiedName~MeasureOrdinaryHundredItemInteraction' *> (Join-Path $root 'desktop.log')
        } finally { $env:GHPB_PERFORMANCE_UI_ROOT = $previous }
    }
    Write-Output $root
    return
}
$matrix = @(@(100,0,$false,5,$true,"Title"), @(100,10,$false,3,$true,"Title"), @(101,10,$false,3,$true,"Title"), @(1000,10,$false,3,$true,"Title"), @(100,100,$false,3,$true,"Title"), @(100,10,$true,3,$true,"Title"), @(100,10,$false,3,$false,"Title"), @(100,10,$false,3,$true,"Select"), @(101,10,$false,3,$true,"Select"), @(1000,10,$false,3,$true,"Select"))
if ($Workload -eq 'Apply50') {
    $matrix = @(@(50,50,$false,3,$true,'Title'), @(50,50,$false,3,$true,'Select'), @(50,50,$false,3,$true,'Both'),
        @(50,0,$false,5,$true,'Title'), @(50,1,$false,3,$true,'Title'), @(101,1,$false,3,$true,'Select'))
}
if ($Workload -eq 'CandidateCheck') {
    # Current-source corroboration only; the historical four-variant matrix is retained.
    $matrix = @(@(50,50,$false,3,$true,'Title'), @(50,0,$false,1,$true,'Title'), @(50,1,$false,1,$true,'Title'),
        @(101,1,$false,1,$true,'Select'), @(2,2,$false,1,$true,'Both'))
}
@{ source = (git -C $repo rev-parse HEAD); binarySource = $SourceRevision; binarySourcePolicy = 'Caller-declared source; verify against retained build and binary hash mapping'; diff = (git -C $repo diff); matrix = $matrix; mode = $Mode; started = [DateTimeOffset]::Now.ToString('o'); policy = 'One warmup per case; all individual samples retained; compare median and range; no tail percentile or machine CI threshold.' } | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $root 'manifest.json') -Encoding utf8
if (!$NoBuild) {
    dotnet build (Join-Path $repo 'tests/GhProjectsBoards.Tests/GhProjectsBoards.Tests.csproj') -c Release *> (Join-Path $root 'build.log')
    if ($LASTEXITCODE) { throw 'Build failed; evidence retained' }
}
$exe = if ($Executable) { (Resolve-Path -LiteralPath $Executable).Path } else { Join-Path $repo 'tests/GhProjectsBoards.Tests/bin/Release/net10.0-windows/GhProjectsBoards.Tests.exe' }
Get-FileHash $exe, (Join-Path (Split-Path $exe) 'GhProjectsBoards.Tests.dll'), (Join-Path (Split-Path $exe) 'GhProjectsBoards.Core.dll') | ConvertTo-Json | Set-Content (Join-Path $root 'hashes.json')
for ($i = 0; $i -lt $matrix.Count; $i++) {
    $case = $matrix[$i]
    $reference = if ($ReferenceRoot) { Join-Path $ReferenceRoot "case-$i" } else { 'none' }
    & $exe --performance (Join-Path $root "case-$i") $case[0] $case[1] $case[2] $case[3] $case[4] $case[5] $reference *> (Join-Path $root "case-$i.log")
    if ($LASTEXITCODE) { throw "Case $i failed; all evidence retained in $root" }
}
Write-Output $root
