[CmdletBinding()]
param(
    [ValidateSet('Normal', 'Live')][string]$State = 'Normal',
    [switch]$VerifyOnly
)
$ErrorActionPreference = 'Stop'
$repoRoot = 'C:\Users\mwam0\.copilot\repos\gh-projects-boards'
$deliveryRoot = Join-Path $repoRoot 'TestResults/issue73/local-delivery-20261003'
if (-not [string]::Equals([IO.Path]::GetFullPath($PSScriptRoot), $deliveryRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Run the original local handoff script; the evidence copy is for inspection.'
}
$receiptPath = Join-Path $repoRoot 'TestResults/issue73/weekly-value-20261003/review-candidate/diagnostics/launch-4d8af16624c14a24ad97ba9879ae313c.json'
$receiptHash = '843E6F4634D4FFD8FC7CE89402E38DA1F57739841472775F347F93E670484257'
if ((Get-FileHash -LiteralPath $receiptPath -Algorithm SHA256).Hash -ne $receiptHash) { throw 'The frozen receipt differs.' }
$receipt = Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json
$expectedExe = Join-Path $repoRoot 'TestResults/planning-build-7b81326e93064588bd530f1e040d5be5/app/GhProjectsBoards.App.exe'
if ($receipt.receiptVersion -ne 2 -or $receipt.executable -ne $expectedExe -or $receipt.appFiles.Count -ne 516 -or
    $receipt.appSha256 -ne 'AF78DF1AAC8821C5B47BB7E957980C8DEAEE6020DD17C767336C188E8F98D4A5' -or
    $receipt.coreSha256 -ne 'E7EF7179BED66C4794D46EA7038D2443E2CE9DF6F22D481B0811A956BD30540A') { throw 'Unexpected candidate identity.' }
$runtimeRoot = Split-Path -Parent $expectedExe
$actualPaths = @(Get-ChildItem -LiteralPath $runtimeRoot -File -Recurse -Force | ForEach-Object { [IO.Path]::GetRelativePath($runtimeRoot, $_.FullName) })
$expectedPaths = @($receipt.appFiles | ForEach-Object path)
if ($actualPaths.Count -ne $expectedPaths.Count -or @(Compare-Object $actualPaths $expectedPaths).Count) { throw 'Runtime inventory differs.' }
foreach ($file in $receipt.appFiles) {
    $runtimeFile = [IO.Path]::GetFullPath((Join-Path $runtimeRoot $file.path))
    if (-not $runtimeFile.StartsWith($runtimeRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
        (Get-FileHash -LiteralPath $runtimeFile -Algorithm SHA256).Hash -ne $file.sha256) { throw 'A frozen runtime file differs.' }
}
$selectedRoot = if ($State -eq 'Normal') { Join-Path $repoRoot 'TestResults/issue73/normal-workload-20261003/session' }
    else { Join-Path $repoRoot 'TestResults/issue73/weekly-value-20261003/live-app-data' }
if (-not (Test-Path -LiteralPath (Join-Path $selectedRoot 'Drafts') -PathType Container)) { throw 'The retained isolated data root is missing.' }
$drafts = @(Get-ChildItem -LiteralPath (Join-Path $selectedRoot 'Drafts') -Filter '*.json' -File)
if ($drafts.Count -ne 1) { throw 'The retained state must contain its single expected profile checkpoint.' }
$running = @(Get-Process -Name 'GhProjectsBoards.App' -ErrorAction SilentlyContinue)
if ($VerifyOnly) {
    [ordered]@{ state=$State; verifyOnly=$true; frozenReceiptSha256=$receiptHash; appSha256=$receipt.appSha256; coreSha256=$receipt.coreSha256;
        runtimeFilesVerified=$expectedPaths.Count; dataRoot=$selectedRoot; checkpoint=$drafts[0].FullName;
        checkpointSha256=(Get-FileHash -LiteralPath $drafts[0].FullName -Algorithm SHA256).Hash;
        ordinaryProcessIds=@($running | ForEach-Object Id); appLaunched=$false; githubWriteInitiated=$false } | ConvertTo-Json -Depth 5
    return
}
if ($running.Count) { throw 'Close the ordinary app normally before resuming either retained state.' }
if ($State -eq 'Live') {
    & (Join-Path $repoRoot 'TestResults/issue73/weekly-value-20261003/launch-review-candidate.ps1')
} else {
    & (Join-Path $repoRoot 'scripts/Start-PlanningCheck.ps1') -Resume -DataRoot $selectedRoot -FrozenLaunch $receiptPath
}
