param(
    [string]$Where = 'cat != Infrastructure',
    [switch]$Discover,
    [switch]$NoBuild,
    [ValidateRange(1, 3600)][int]$TimeoutSeconds = 180
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$run = Join-Path $repo ('TestResults/ui-integration/run-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff') + '-' + [guid]::NewGuid().ToString('N').Substring(0,8))
New-Item -ItemType Directory -Path $run | Out-Null
$project = Join-Path $repo 'tests/GhProjectsBoards.UiIntegration.Tests/GhProjectsBoards.UiIntegration.Tests.csproj'
$binaryRoot = Join-Path $repo 'tests/GhProjectsBoards.UiIntegration.Tests/bin/Release/net10.0-windows10.0.26100.0/win-x64'
$timer = [Diagnostics.Stopwatch]::StartNew()
$metadata = [ordered]@{ source = (git -C $repo rev-parse HEAD); changes = @(git -C $repo status --porcelain); command = $MyInvocation.Line; where = $Where; discoveryOnly = $Discover.IsPresent; sdk = (dotnet --version); os = [Environment]::OSVersion.VersionString; started = (Get-Date).ToUniversalTime().ToString('o'); results = $run }
git -C $repo diff --binary 2> (Join-Path $run 'source-diff.log') | Set-Content -LiteralPath (Join-Path $run 'source.diff') -Encoding utf8
$metadata.sources = @(Get-ChildItem -LiteralPath (Join-Path $repo 'tests/GhProjectsBoards.UiIntegration.Tests') -File | ForEach-Object { @{name=$_.Name; sha256=(Get-FileHash -LiteralPath $_.FullName).Hash} })
try {
    if (-not $NoBuild) {
        & dotnet build $project -c Release *> (Join-Path $run 'build.log')
        if ($LASTEXITCODE -ne 0) { throw 'UI host build failed' }
    }
    $metadata.buildSeconds = $timer.Elapsed.TotalSeconds
    $metadata.binaries = @(foreach ($name in @('GhProjectsBoards.App.dll','GhProjectsBoards.Core.dll','GhProjectsBoards.UiIntegration.Tests.dll','Microsoft.UI.Xaml.dll','GhProjectsBoards.App.pri','GhProjectsBoards.UiIntegration.Tests.pri')) {
        $path = Join-Path $binaryRoot $name
        if (Test-Path -LiteralPath $path) { @{name=$name; sha256=(Get-FileHash -LiteralPath $path).Hash} }
    })
    $metadata.packageAssets = (Get-FileHash -LiteralPath (Join-Path $repo 'tests/GhProjectsBoards.UiIntegration.Tests/obj/project.assets.json')).Hash
    $result = Join-Path $run 'results.xml'
    $arguments = @('--workers=0', '--where', $Where, '--noheader')
    if ($Discover) { $arguments += "--explore=$result" }
    else { $arguments += "--result=$result" }
    $start = [Diagnostics.ProcessStartInfo]::new((Join-Path $binaryRoot 'GhProjectsBoards.UiIntegration.Tests.exe'))
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true; $start.WindowStyle = 'Hidden'
    $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true; $start.WorkingDirectory = $binaryRoot
    foreach ($argument in $arguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($start)
    $stdout = $process.StandardOutput.ReadToEndAsync(); $stderr = $process.StandardError.ReadToEndAsync()
    $metadata.processId = $process.Id
    if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
        $process.Kill($true); $process.WaitForExit(); $metadata.timedOut = $true
    }
    $stdout.GetAwaiter().GetResult() | Set-Content -LiteralPath (Join-Path $run 'stdout.log') -Encoding utf8
    $stderr.GetAwaiter().GetResult() | Set-Content -LiteralPath (Join-Path $run 'stderr.log') -Encoding utf8
    $metadata.exitCode = $process.ExitCode
    if (Test-Path -LiteralPath $result) {
        [xml]$xml = Get-Content -LiteralPath $result -Raw
        $report = $xml.'test-run'
        if ($report) { $metadata.counts = @{total=$report.total; passed=$report.passed; failed=$report.failed; skipped=$report.skipped; inconclusive=$report.inconclusive} }
    }
    if ($metadata.timedOut) { throw 'UI host timed out; owned process terminated' }
    if ($process.ExitCode -ne 0) { throw "UI host failed: $($process.ExitCode)" }
    [xml]$xml = Get-Content -LiteralPath $result -Raw
    if ($Discover) {
        if (@($xml.SelectNodes('//test-case')).Count -eq 0) { throw 'Empty discovery selection' }
    } else {
        $report = $xml.'test-run'
        $metadata.counts = @{total=$report.total; passed=$report.passed; failed=$report.failed; skipped=$report.skipped; inconclusive=$report.inconclusive}
        if ([int]$report.total -eq 0 -or [int]$report.passed -ne [int]$report.total -or $report.result -ne 'Passed') { throw 'Empty, failed, skipped or incomplete test selection' }
    }
} finally {
    $metadata.elapsedSeconds = $timer.Elapsed.TotalSeconds
    $metadata | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $run 'metadata.json') -Encoding utf8
    Write-Host "UI integration artifacts: $run"
}
