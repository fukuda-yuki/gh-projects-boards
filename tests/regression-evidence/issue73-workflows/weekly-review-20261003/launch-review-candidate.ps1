[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$expectedRoot = 'C:\Users\mwam0\.copilot\repos\gh-projects-boards\TestResults\issue73\weekly-value-20261003'
$dataRoot = Join-Path $expectedRoot 'live-app-data'
$receiptPath = 'C:\Users\mwam0\.copilot\repos\gh-projects-boards\TestResults\issue73\weekly-value-20261003\review-candidate\diagnostics\launch-4d8af16624c14a24ad97ba9879ae313c.json'
$receiptHash = '843E6F4634D4FFD8FC7CE89402E38DA1F57739841472775F347F93E670484257'
$expectedExe = 'C:\Users\mwam0\.copilot\repos\gh-projects-boards\TestResults\planning-build-7b81326e93064588bd530f1e040d5be5\app\GhProjectsBoards.App.exe'
if (-not [string]::Equals([IO.Path]::GetFullPath($PSScriptRoot), $expectedRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Run the original scoped launch script.' }
if (-not (Test-Path -LiteralPath $dataRoot -PathType Container)) { throw 'The prepared isolated data root is missing.' }
if (@(Get-Process -Name 'GhProjectsBoards.App' -ErrorAction SilentlyContinue).Count) { throw 'An ordinary App is already running. Close it normally before launching this candidate.' }
if ((Get-FileHash -LiteralPath $receiptPath).Hash -ne $receiptHash) { throw 'The frozen review receipt changed.' }
$receipt = Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json
if ($receipt.receiptVersion -ne 2 -or $receipt.executable -ne $expectedExe -or $receipt.appFiles.Count -ne 516 -or
    $receipt.appSha256 -ne 'AF78DF1AAC8821C5B47BB7E957980C8DEAEE6020DD17C767336C188E8F98D4A5' -or
    $receipt.coreSha256 -ne 'E7EF7179BED66C4794D46EA7038D2443E2CE9DF6F22D481B0811A956BD30540A') { throw 'The candidate identity differs.' }
$runtime = Split-Path -Parent $expectedExe
$actual = @(Get-ChildItem -LiteralPath $runtime -Force -File -Recurse | ForEach-Object { [IO.Path]::GetRelativePath($runtime, $_.FullName) })
$expected = @($receipt.appFiles | ForEach-Object path)
if ($actual.Count -ne $expected.Count -or @(Compare-Object $actual $expected).Count) { throw 'The frozen runtime inventory differs.' }
foreach ($file in $receipt.appFiles) {
    $path = [IO.Path]::GetFullPath((Join-Path $runtime $file.path))
    if (-not $path.StartsWith($runtime + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
        (Get-FileHash -LiteralPath $path).Hash -ne $file.sha256) { throw 'A frozen runtime file changed.' }
}
$start = [Diagnostics.ProcessStartInfo]::new($expectedExe)
$start.UseShellExecute = $false
$start.WorkingDirectory = $runtime
$start.Environment['GHPB_DATA_ROOT'] = $dataRoot
$start.Environment['GHPB_RECYCLED_PRESENTATION'] = '1'
foreach ($key in @('GH_TOKEN','GITHUB_TOKEN','GH_ENTERPRISE_TOKEN','GITHUB_ENTERPRISE_TOKEN','GHPB_SHEET_DIAGNOSTICS','GHPB_IME_TRACE')) { $start.Environment.Remove($key) | Out-Null }
# Leave real gh configuration in place; reject an inherited test configuration.
if ($start.Environment.ContainsKey('GH_CONFIG_DIR') -and $start.Environment['GH_CONFIG_DIR']) {
    $configRoot = [IO.Path]::GetFullPath($start.Environment['GH_CONFIG_DIR'])
    $testRoot = 'C:\Users\mwam0\.copilot\repos\gh-projects-boards\TestResults'
    if ($configRoot.Equals($testRoot, [StringComparison]::OrdinalIgnoreCase) -or $configRoot.StartsWith($testRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'An inherited test gh configuration must be removed from the launching shell.' }
}
$launchedAt = [DateTimeOffset]::UtcNow.ToString('o')
$process = [Diagnostics.Process]::Start($start)
$launchRecord = Join-Path $expectedRoot ('checks/live-candidate-preparation/launch-' + [guid]::NewGuid().ToString('N') + '.json')
[ordered]@{ launchedAtUtc = $launchedAt; pid = $process.Id; executable = $expectedExe; dataRoot = $dataRoot; frozenReceipt = $receiptPath; frozenReceiptSha256 = $receiptHash; appSha256 = $receipt.appSha256; coreSha256 = $receipt.coreSha256; runtimeFilesVerified = $expected.Count; credentialEnvironmentRemoved = $true; ghConfiguration = 'Inherited real configuration; no credentials copied or logged'; nativeIdentity = 'Root must observe'; remoteExecution = 'Not initiated by this launcher'; humanAcceptance = 'not_run' } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $launchRecord -Encoding utf8
Write-Output ('Ordinary application PID: ' + $process.Id)
Write-Output ('Launch receipt: ' + $launchRecord)
