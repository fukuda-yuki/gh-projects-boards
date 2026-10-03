# Operator only. The coordinator supplies the frozen candidate explicitly.
param([Parameter(Mandatory)][string]$CandidateReceipt)
. (Join-Path $PSScriptRoot 'Environment.ps1')
if (@(Get-Process -Name 'GhProjectsBoards.App' -ErrorAction SilentlyContinue).Count -gt 0) { throw 'An ordinary application is still open.' }
$receiptPath = (Resolve-Path -LiteralPath $CandidateReceipt).Path
$candidate = Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json
if ($candidate.receiptVersion -ne 2 -or -not $candidate.preparedOnly -or -not $candidate.source -or
    -not [IO.Path]::IsPathFullyQualified($candidate.executable) -or $candidate.appFiles.Count -eq 0) { throw 'Explicit version-2 frozen candidate receipt required.' }
Assert-FileManifest (Split-Path $candidate.executable) $candidate.appFiles
Assert-FileManifest (Split-Path $preparation.fakeGh) $preparation.fakeFiles
if ((Get-FileHash -LiteralPath $candidate.executable).Hash -ne $candidate.executableSha256 -or
    (Get-FileHash -LiteralPath $preparation.fakeGh).Hash -ne $preparation.fakeAliasSha256) { throw 'Executable identity mismatch.' }
if (Test-Path -LiteralPath (Join-Path $preparation.ghConfig 'hosts.yml')) { throw 'Real credential configuration is prohibited.' }
$scenario = Get-Content -LiteralPath (Join-Path $preparation.ghConfig 'scenario.json') -Raw | ConvertFrom-Json
if (($scenario | ConvertTo-Json -Depth 5 -Compress) -ne ($preparation.scenario | ConvertTo-Json -Depth 5 -Compress)) { throw 'Prepared service scenario changed.' }
$bindingPath = Join-Path $evaluationRoot 'diagnostics/candidate-frozen-receipt.json'
$candidateHash = (Get-FileHash -LiteralPath $receiptPath).Hash
if (Test-Path -LiteralPath $bindingPath) {
    if ((Get-FileHash -LiteralPath $bindingPath).Hash -ne $candidateHash) { throw 'Resume must use the same frozen candidate.' }
} else {
    foreach ($file in $preparation.preparedFiles) {
        if ((Get-FileHash -LiteralPath (Join-Path $evaluationRoot $file.path)).Hash -ne $file.sha256) { throw "Initial input changed: $($file.path)" }
    }
    Copy-Item -LiteralPath $receiptPath -Destination $bindingPath
}
$process = [Diagnostics.Process]::Start((New-IsolatedStart $candidate.executable))
$launchPath = Join-Path $evaluationRoot ('diagnostics/launch-' + [guid]::NewGuid().ToString('N') + '.json')
[ordered]@{ at=[DateTimeOffset]::UtcNow.ToString('o');pid=$process.Id;source=$candidate.source;
    candidateReceipt=$receiptPath;candidateReceiptSha256=$candidateHash;executable=$candidate.executable;
    executableSha256=$candidate.executableSha256;appSha256=$candidate.appSha256;
    fakeGh=$preparation.fakeGh;fakeGhSha256=$preparation.fakeAliasSha256;dataRoot=$preparation.dataRoot;ghConfig=$preparation.ghConfig;
    scenarioSha256=(Get-FileHash -LiteralPath (Join-Path $preparation.ghConfig 'scenario.json')).Hash;
    credentialPolicy='Inherited GH_*, GITHUB_*, GHPB_* removed; isolated config; fake CLI first; live proxy absent';
    uiDriver='none';liveGitHub='prohibited';inputLineage='diagnostics/input-lineage-readback.json' } |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $launchPath -Encoding utf8
Write-Output "Ordinary application PID: $($process.Id)"
Write-Output "Launch receipt: $launchPath"
