[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Baseline,
    [Parameter(Mandatory)][string]$OutputPath
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$baselineCommit = & git -C $repo rev-parse --verify "$Baseline^{commit}"
if ($LASTEXITCODE -ne 0) { throw 'Baseline must resolve to a local commit.' }
$fixturePath = 'tests/GhProjectsBoards.UiIntegration.Tests/PlanSheetHostedTests.cs'
$harnessPath = 'tests/GhProjectsBoards.UiIntegration.Tests/PlanSheetScrollPerformanceTests.cs'
$lines = & git -C $repo show "${baselineCommit}:$fixturePath"
if ($LASTEXITCODE -ne 0) { throw 'Cannot read the baseline hosted fixture.' }
$fixture = ($lines -join "`n") + "`n"
$declaration = 'internal sealed class PlanSheetHostedTests'
$marker = '        await Ui.Run(() => sheet = new(session, () => clipboardReader is { } read ? read() : Task.FromResult(clipboard), '
if (-not $fixture.Contains($declaration) -or -not $fixture.Contains($marker) -or
    $fixture.IndexOf($marker, [StringComparison]::Ordinal) -ne $fixture.LastIndexOf($marker, [StringComparison]::Ordinal) -or
    $fixture.Contains('VersionRowsScrollRoundTripRecordsFrameIntervalsAndViewportPopulation')) {
    throw 'Baseline fixture is not the pre-scroll-harness version. No files were written.'
}
$fixtureSetup = @'
        if (TestContext.CurrentContext.Test.MethodName == nameof(VersionRowsScrollRoundTripRecordsFrameIntervalsAndViewportPopulation))
        {
            var plan = GhProjectsBoards.Tests.EvaluationFixture.ReadPlan();
            var snapshot = GhProjectsBoards.Tests.EvaluationFixture.Simulate(plan, [plan.StatusDate]).Single();
            session = await PlanSession.CreateAsync(new(root), snapshot.Document, plan.StatusDate);
        }
'@
$fixture = $fixture.Replace($declaration, 'internal sealed partial class PlanSheetHostedTests')
$fixture = $fixture.Replace($marker, $fixtureSetup.Replace("`r`n", "`n") + "`n" + $marker)
$harness = [IO.File]::ReadAllText((Join-Path $repo $harnessPath)).Replace("`r`n", "`n")
$destination = [IO.Path]::GetFullPath($OutputPath)
if (Test-Path -LiteralPath $destination) {
    if (-not (Test-Path -LiteralPath $destination -PathType Container) -or
        @(Get-ChildItem -LiteralPath $destination -Force).Count -ne 0) {
        throw 'OutputPath must be absent or an empty directory.'
    }
}
$encoding = [Text.UTF8Encoding]::new($false)
$files = @(
    @{ path = $fixturePath; text = $fixture },
    @{ path = $harnessPath; text = $harness }
)
$manifestFiles = foreach ($file in $files) {
    $path = Join-Path $destination $file.path
    [IO.Directory]::CreateDirectory((Split-Path $path -Parent)) | Out-Null
    [IO.File]::WriteAllText($path, $file.text, $encoding)
    @{ path = $file.path; sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash }
}
$manifest = @{ baseline = $baselineCommit; files = @($manifestFiles) } | ConvertTo-Json -Depth 4
[IO.File]::WriteAllText((Join-Path $destination 'manifest.json'), $manifest, $encoding)
Write-Host "Exported the test-only scroll harness for $baselineCommit to $destination"
