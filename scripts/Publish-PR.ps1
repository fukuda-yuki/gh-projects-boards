# Explicitly task-authorized publication. Never bypass an enforced push denial.
[CmdletBinding()]
param([string]$Manifest = (Join-Path (Split-Path $PSScriptRoot -Parent) 'TestResults/local-rows/publication.json'))
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$gh = 'C:\Program Files\GitHub CLI\gh.exe'
function Invoke-Git([string[]]$Arguments) {
    $result = & git -C $repo @Arguments
    if ($LASTEXITCODE -ne 0) { throw "git failed ($LASTEXITCODE): $($Arguments[0])" }
    return ($result -join "`n").Trim()
}
function Invoke-Gh([string[]]$Arguments) {
    $result = & $gh @Arguments
    if ($LASTEXITCODE -ne 0) { throw "gh failed ($LASTEXITCODE): $($Arguments[0]). Inspect remote state before retrying any uncertain mutation." }
    return ($result -join "`n").Trim()
}
$m = Get-Content -LiteralPath $Manifest -Raw | ConvertFrom-Json
if ($m.repository -ne 'fukuda-yuki/gh-projects-boards' -or $m.branch -notmatch '^codex/issue-[0-9]+-[a-z0-9-]+$' -or $m.head -notmatch '^[0-9a-f]{40}$') { throw 'Unexpected publication identity.' }
if ((Invoke-Git -Arguments @('remote','get-url','origin')) -ne 'https://github.com/fukuda-yuki/gh-projects-boards.git') { throw 'Origin does not match the intended repository.' }
$pushUrls = (Invoke-Git -Arguments @('remote','get-url','--push','--all','origin')) -split "`n"
if ($pushUrls.Count -ne 1 -or $pushUrls[0] -ne 'https://github.com/fukuda-yuki/gh-projects-boards.git') { throw 'Push destination differs or has multiple targets.' }
if ((Invoke-Git -Arguments @('branch','--show-current')) -ne $m.branch -or (Invoke-Git -Arguments @('rev-parse','HEAD')) -ne $m.head) { throw 'Branch/head differs from the reviewed source.' }
if (Invoke-Git -Arguments @('status','--porcelain')) { throw 'Working tree is not clean.' }
if ((Invoke-Git -Arguments @('merge-base','HEAD',$m.base)) -ne $m.base) { throw 'Expected main baseline is not an ancestor.' }
$body = Join-Path (Split-Path ([IO.Path]::GetFullPath($Manifest)) -Parent) 'pr-body.md'
if (-not (Test-Path -LiteralPath $body) -or -not (Get-Content -LiteralPath $body -Raw).Contains($m.head)) { throw 'PR body does not identify the reviewed head.' }
$existing = @(Invoke-Gh -Arguments @('pr','list','--repo',$m.repository,'--head',$m.branch,'--state','all','--json','number,state,baseRefName,isCrossRepository,url') | ConvertFrom-Json)
if ($existing.Count -gt 1 -or @($existing | Where-Object { $_.baseRefName -ne 'main' -or $_.isCrossRepository -or $_.state -ne 'OPEN' }).Count -gt 0) { throw 'An existing PR needs manual reconciliation; no duplicate will be created.' }
Invoke-Git -Arguments @('push','--no-follow-tags','--set-upstream','origin',"HEAD:refs/heads/$($m.branch)") | Write-Host
$remote = Invoke-Git -Arguments @('ls-remote','--heads','origin',"refs/heads/$($m.branch)")
if (($remote -split '\s+')[0] -ne $m.head) { throw 'Remote feature head does not match the reviewed source.' }
if ($existing.Count -eq 1) {
    $number = [string]$existing[0].number
    Invoke-Gh -Arguments @('pr','edit',$number,'--repo',$m.repository,'--title',$m.title,'--body-file',$body) | Write-Host
} else {
    Invoke-Gh -Arguments @('pr','create','--repo',$m.repository,'--base','main','--head',$m.branch,'--title',$m.title,'--body-file',$body) | Write-Host
    $published = @(Invoke-Gh -Arguments @('pr','list','--repo',$m.repository,'--head',$m.branch,'--state','open','--json','number') | ConvertFrom-Json)
    if ($published.Count -ne 1) { throw 'PR creation readback is ambiguous. Inspect GitHub before retrying.' }
    $number = [string]$published[0].number
}
$pr = Invoke-Gh -Arguments @('pr','view',$number,'--repo',$m.repository,'--json','url,headRefOid,baseRefName,state,title,body') | ConvertFrom-Json
if ($pr.headRefOid -ne $m.head -or $pr.baseRefName -ne 'main' -or $pr.state -ne 'OPEN') { throw 'PR head/base/state verification failed.' }
if ($pr.title -ne $m.title -or $pr.body.Replace("`r`n", "`n").TrimEnd() -cne (Get-Content -LiteralPath $body -Raw).Replace("`r`n", "`n").TrimEnd()) { throw 'Prepared PR title/body was not retained; inspect before retrying.' }
Write-Host "Published/reused: $($pr.url)"
$deadline = [DateTime]::UtcNow.AddMinutes(10)
do {
    $runs = @(Invoke-Gh -Arguments @('run','list','--repo',$m.repository,'--commit',$m.head,'--limit','30','--json','status,conclusion,url,headSha') | ConvertFrom-Json)
    if (@($runs | Where-Object { $_.status -eq 'completed' -and $_.conclusion -ne 'success' }).Count -gt 0) { throw 'CI failed/cancelled at the published head. The PR remains open; inspect its runs.' }
    if ($runs.Count -gt 0 -and @($runs | Where-Object status -ne 'completed').Count -eq 0) {
        $runs | ForEach-Object { Write-Host "CI success: $($_.url)" }
        Write-Host 'Remote head and observed CI verified. Nothing was merged or closed.'
        return
    }
    Start-Sleep -Seconds 10
} while ([DateTime]::UtcNow -lt $deadline)
throw "PR exists at $($pr.url), but CI was not confirmed within ten minutes. Do not report integration or rerun creation blindly."
