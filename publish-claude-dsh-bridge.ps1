<#
.SYNOPSIS
  Publishes tools/claude-dsh-bridge: branch from origin/main, commit, push, pull request.

.DESCRIPTION
  Run this yourself when you want the bridge on the remote. Nothing is pushed
  until you execute it without -DryRun.

  The script follows the repository's publication rules:
    * the branch is created from origin/main with `git switch --no-track -c`,
    * staging is limited to tools/claude-dsh-bridge and the script aborts if
      anything else became staged,
    * the repository-local branch.autoSetupMerge/push.default/push.autoSetupRemote
      settings make the first push create the same-name upstream, which is then
      verified to be origin/<branch> and never main.

  Checks run before the commit: the bridge unit checks always, the end-to-end
  delegation cycle only with -RunE2E (it spends real model calls).

.EXAMPLE
  pwsh -File publish-claude-dsh-bridge.ps1 -DryRun
  pwsh -File publish-claude-dsh-bridge.ps1
  pwsh -File publish-claude-dsh-bridge.ps1 -Issue 146 -Draft
  pwsh -File publish-claude-dsh-bridge.ps1 -RunE2E -SkipChecks
#>
[CmdletBinding()]
param(
    [string]$RepoDir = $PSScriptRoot,
    [string]$Repo = 'fukuda-yuki/gh-projects-boards',
    [string]$BaseBranch = 'main',
    [string]$Branch = 'claude/dsh-flash-bridge',
    [string]$Title = 'Add a Claude Code to DeepSeek Harness delegation bridge',
    [int]$Issue = 0,
    [switch]$RunE2E,
    [switch]$SkipChecks,
    [switch]$Draft,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
# git writes progress to stderr; on shells that opt into treating native stderr as
# an error, a successful push would abort the script.
if ($null -ne (Get-Variable -Name PSNativeCommandUseErrorActionPreference -ErrorAction SilentlyContinue)) {
    $PSNativeCommandUseErrorActionPreference = $false
}
$ToolPath = 'tools/claude-dsh-bridge'
$ToolDir = Join-Path $RepoDir $ToolPath
$Gh = (Get-Command gh -ErrorAction SilentlyContinue).Source
if (-not $Gh) { $Gh = 'C:\Program Files\GitHub CLI\gh.exe' }

function Write-Step([string]$Message) { Write-Host "== $Message" -ForegroundColor Cyan }
function Write-Ok([string]$Message) { Write-Host "   $Message" -ForegroundColor Green }
function Write-Warn([string]$Message) { Write-Host "   $Message" -ForegroundColor Yellow }
function Fail([string]$Message) { throw $Message }

function Invoke-Git {
    param([Parameter(ValueFromRemainingArguments = $true)][string[]]$Arguments)
    # core.fsmonitor is disabled per call: a stale local fsmonitor daemon writes
    # "fsmonitor_ipc__send_query" noise to stderr, which must not be read as output.
    $output = & git -C $RepoDir -c core.fsmonitor=false @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) { Fail "git $($Arguments -join ' ') failed:`n$($output -join "`n")" }
    return @($output)
}

# One porcelain status entry (for example " M path" or "?? path"), never a git diagnostic.
function Select-Porcelain([string[]]$Lines) {
    return @($Lines | Where-Object { $_ -match '^(\?\?|[ MARCUDT]{1,2}) \S' })
}

# Path output of a diff, with any diagnostic line dropped.
function Select-Paths([string[]]$Lines) {
    return @($Lines | Where-Object { $_ -ne '' -and $_ -notmatch '^(error|warning|fatal|hint):' })
}

# --- preconditions ----------------------------------------------------------

Write-Step 'Checking the checkout'
if (-not (Test-Path -LiteralPath (Join-Path $RepoDir '.git'))) { Fail "$RepoDir is not a git checkout." }
if (-not (Test-Path -LiteralPath $ToolDir)) { Fail "$ToolDir does not exist; nothing to publish." }
if (-not (Test-Path -LiteralPath $Gh)) { Fail "The GitHub CLI was not found (looked for gh on PATH and at $Gh)." }
if (-not (Test-Path -LiteralPath (Join-Path $ToolDir 'test\unit.js'))) { Fail "$ToolDir does not look like the bridge." }
Write-Ok "repository : $RepoDir"
Write-Ok "tool       : $ToolPath"

$modified = @(Select-Porcelain (Invoke-Git status --porcelain --untracked-files=no))
if ($modified.Count -gt 0) {
    Fail ("Tracked files have uncommitted changes; commit or stash them first so this branch carries only the tool:`n  " + ($modified -join "`n  "))
}
Write-Ok 'working tree: no modified tracked files'

$currentBranch = @(Invoke-Git rev-parse --abbrev-ref HEAD)[0]
Write-Ok "branch now : $currentBranch"

$candidates = @(Select-Porcelain (Invoke-Git status --porcelain --untracked-files=all -- $ToolPath))
Write-Ok "files to publish: $($candidates.Count)"
foreach ($line in $candidates) { Write-Host "     $line" }

if ($DryRun) {
    Write-Step 'Dry run: nothing was fetched, staged, committed, pushed or opened'
    Write-Host @"
Planned actions
  1. git fetch --prune origin
  2. git switch --no-track -c $Branch origin/$BaseBranch      (or: git switch $Branch when it exists)
  3. run the bridge checks$([bool]$RunE2E ? ' (unit + end-to-end)' : ' (unit only)')
  4. git add -- $ToolPath
  5. git commit -F <message file>   # "$Title"
  6. git config --local branch.autoSetupMerge simple / push.default simple / push.autoSetupRemote true
  7. git push                      # creates the origin/$Branch upstream
  8. gh pr create --repo $Repo --base $BaseBranch --head $Branch $(if ($Draft) { '--draft ' })--title "$Title"
"@
    exit 0
}

# --- checks -----------------------------------------------------------------

if (-not $SkipChecks) {
    Write-Step 'Running the bridge unit checks'
    Push-Location $ToolDir
    try {
        & node test/unit.js
        if ($LASTEXITCODE -ne 0) { Fail 'node test/unit.js failed; fix the checks before publishing.' }
    } finally { Pop-Location }
    Write-Ok 'unit checks passed'

    if ($RunE2E) {
        Write-Step 'Running the end-to-end delegation cycle (real model calls)'
        if (-not $env:OPENCODE_API_KEY) {
            $env:OPENCODE_API_KEY = [Environment]::GetEnvironmentVariable('OPENCODE_API_KEY', 'User')
        }
        if (-not $env:OPENCODE_API_KEY) { Fail 'OPENCODE_API_KEY is not available; -RunE2E needs the OpenCode Go credential.' }
        Push-Location $ToolDir
        try {
            & node test/e2e.js
            if ($LASTEXITCODE -ne 0) { Fail 'node test/e2e.js failed; fix the checks before publishing.' }
        } finally { Pop-Location }
        Write-Ok 'end-to-end cycle passed'
    }
}

# --- branch -----------------------------------------------------------------

Write-Step "Preparing branch $Branch from origin/$BaseBranch"
Invoke-Git fetch --prune origin | Out-Null
Write-Ok 'fetched origin'

if ($currentBranch -ne $Branch) {
    $exists = @(Invoke-Git branch --list $Branch)
    if ($exists.Count -gt 0) {
        Invoke-Git switch $Branch | Out-Null
        Write-Ok "switched to the existing branch $Branch"
    } else {
        Invoke-Git switch --no-track -c $Branch "origin/$BaseBranch" | Out-Null
        Write-Ok "created $Branch from origin/$BaseBranch"
    }
} else {
    Write-Ok "already on $Branch"
}

# --- commit ----------------------------------------------------------------

Write-Step 'Staging and committing the tool'
Invoke-Git add -- $ToolPath | Out-Null

$staged = @(Select-Paths (Invoke-Git diff --cached --name-only))
if ($staged.Count -eq 0) {
    # A re-run after a failed push or pull request must not have to redo the commit.
    $ahead = @(Select-Paths (Invoke-Git log "origin/$BaseBranch..HEAD" --oneline))
    if ($ahead.Count -eq 0) {
        Fail "Nothing to publish: $ToolPath is already committed on $BaseBranch and nothing is staged."
    }
    Write-Warn "nothing new to stage; publishing the $($ahead.Count) commit(s) already on $Branch"
} else {
    $outside = @($staged | Where-Object { -not $_.StartsWith("$ToolPath/") })
    if ($outside.Count -gt 0) {
        Fail ("Staged files outside $ToolPath were found; unstage them before publishing:`n  " + ($outside -join "`n  "))
    }
    Write-Ok "staged $($staged.Count) files, all under $ToolPath"

    $messageFile = [IO.Path]::GetTempFileName()
    @"
$Title

Claude Code keeps planning, design, review and the completion decision; a DSH
agent run on DeepSeek V4.1 Flash over OpenCode Go does the implementation work.
The bridge hands one request at a time to the child and returns its state,
report, end reason and session identity, and DSH never calls Claude Code back.

It drives the documented ``dsh --profile headless --json --session-id`` surface,
so the run reuses DSH's own launcher, agent loop, sandboxed tools, session
persistence and session adoption instead of reimplementing them or coupling to
0.2.0-rc.2 plugin internals. One run is one process: that is what makes a
finished run and a stopped run provable, so the model's report and the process
outcome stay separate fields.

Developer tooling only: no product project, dependency or behavior changes. The
only machine configuration is the CLI-owned headless profile that install.ps1
writes under %DSH_HOME%, kept out of this repository.
"@ | Set-Content -LiteralPath $messageFile -Encoding UTF8

    Invoke-Git commit -F $messageFile | ForEach-Object { Write-Host "   $_" }
    Remove-Item -LiteralPath $messageFile -Force
    Write-Ok "committed $(@(Invoke-Git rev-parse --short HEAD)[0])"
}

# --- push ------------------------------------------------------------------

Write-Step 'Pushing the branch'
Invoke-Git config --local branch.autoSetupMerge simple | Out-Null
Invoke-Git config --local push.default simple | Out-Null
Invoke-Git config --local push.autoSetupRemote true | Out-Null
Invoke-Git push | ForEach-Object { Write-Host "   $_" }

$upstream = @(Invoke-Git rev-parse --abbrev-ref '@{u}')[0]
$mergeRef = @(Invoke-Git config --get "branch.$Branch.merge")[0]
if ($upstream -ne "origin/$Branch" -or $mergeRef -ne "refs/heads/$Branch") {
    Fail "The upstream is not the same-name branch (upstream=$upstream, merge=$mergeRef). Do not push again until this is fixed."
}
Write-Ok "upstream verified: $upstream -> $mergeRef"

# --- pull request ----------------------------------------------------------

Write-Step 'Opening the pull request'
$relatedIssue = if ($Issue -gt 0) {
    "Refs #$Issue - the tooling requested for this workflow. The bridge is published on its own branch and does not change product behavior."
} else {
    'No Issue: developer tooling requested directly by the owner; no product behavior changes.'
}

$bodyFile = [IO.Path]::GetTempFileName()
@"
## Related Issue

- $relatedIssue

## Summary

- Adds ``$ToolPath``: a dependency-free stdio MCP server that Claude Code starts itself, delegating one implementation task at a time to a DeepSeek Harness agent run on DeepSeek V4.1 Flash over OpenCode Go.
- Keeps the split the owner asked for: Claude Code plans, splits work, reviews the result and decides completion; the child investigates, edits, runs tests and reports failures. The bridge hands the request over and returns state, final report, end reason and session identity. DSH never calls Claude Code back, and the child is a DSH agent with DSH's own tools - not Claude Code as a subagent.
- Drives the documented ``dsh --profile headless --json --session-id`` surface, so a run reuses DSH's launcher, agent loop, sandboxed tools, session persistence and session adoption. The rejected alternatives are recorded in ``README.md``: 0.2.0-rc.2 ships no MCP server plugin, the SDK JSON-RPC server has no cancel method, ACP is the editor direction, and the desktop HTTP API depends on a live GUI session whose profile the CLI cannot boot at all.
- One run is one process, which is what makes the two judgements separable: ``dsh_result`` reports the model's own message and the process outcome (exit code plus ``turn/end`` reason) in different fields, and ``dsh_cancel`` proves the stop with taskkill, the run's own exit event, a pid probe and tasklist.
- Reports the route actually used by reading it back from DSH's session log (``request/header``, ``request/context``) instead of trusting configuration, and returns every record path (bridge run records, event stream, child stderr, DSH session log).
- ``install.ps1`` prepares the CLI-owned ``headless`` profile under ``%DSH_HOME%``, verifies the composed route, checks the credential without reading it, and registers the server in Claude Code. The bridge depends on no PATH entry: it resolves the installation by absolute path and is registered with the absolute Node.js path.
- Developer tooling only: no product project, dependency or behavior is touched.

## Validation

Windows 11, Node.js 24.15.0, DeepSeek Harness ``0.2.0-rc.2`` (``dsh --version``), route ``opencode-go`` / ``deepseek-v4.1-flash``. Commands run from ``$ToolPath``.

- ``node test/unit.js`` - 10/10 passed: MCP handshake, notification and error handling, launcher command line against the shipped ``dsh.cmd``, CLI entry detection inside ``app.asar``, multi-frame zstd session-log facts, ``tasklist`` CSV parsing, configuration and profile patch reading.
- ``node test/e2e.js`` - 33/33 passed in 88 s (first complete run 60 s), driving the MCP server over real stdio with the same handshake and tool calls Claude Code makes. In that cycle a real DSH run implemented ``multiply`` in a scratch copy of ``demo/calc.js`` and the parent's own ``node demo/check.js`` then passed 3/3; ``dsh_rework`` continued the same session (``same_session: true``, same session id) and the review findings were implemented - ``multiply('x', 2)`` throws ``TypeError`` and the parent's run passed 6/6; a second ``dsh_delegate`` was refused while a run was active; ``dsh_cancel`` reported ``verified_stopped: true`` from taskkill, the run's exit event, the pid probe and tasklist, and the run stayed ``cancelled``.
- ``claude mcp get dsh-flash`` - ``Status: Connected``: Claude Code started the registered server itself and completed the MCP handshake.
- A pre-bridge smoke run (``dsh --profile headless --json "implement multiply ..."``) exited 0 in 26 s with the file really changed.
- Evidence: the JSON of every executed cycle is committed under ``$ToolPath/test/artifacts/``; the two 2 KB files record the two failed first attempts (a test-harness defect, then a sandbox limitation).

## Not verified

- No tool call has been made from inside an interactive Claude Code session. The MCP connection and all six tools are verified over real stdio, and Claude Code's own health check connects, but the in-session prompt is still outstanding.
- Allow-listing ``mcp__dsh-flash__*`` in ``.claude/settings.json`` was not exercised; registration used Claude Code's own approval path.
- Only ``workspace-write`` runs were exercised. Headless runs have no interactive answerer, so a tool call that needs a wider approval fails closed rather than blocking.
- Cancellation is Windows-only (``taskkill``/``tasklist``); the non-Windows fallback kills only the direct child and does not reap descendants.
- One delegated child at a time (``DSH_BRIDGE_MAX_RUNS=1``) is what was exercised; higher concurrency is untested.
- Run records under ``%DSH_BRIDGE_HOME%\runs`` are not garbage-collected.
"@ | Set-Content -LiteralPath $bodyFile -Encoding UTF8

$ghArgs = @('pr', 'create', '--repo', $Repo, '--base', $BaseBranch, '--head', $Branch, '--title', $Title, '--body-file', $bodyFile)
if ($Draft) { $ghArgs += '--draft' }

$created = & $Gh @ghArgs 2>&1
$exit = $LASTEXITCODE
Remove-Item -LiteralPath $bodyFile -Force

if ($exit -ne 0) {
    Write-Warn ($created -join "`n")
    $existing = & $Gh pr view $Branch --repo $Repo --json url -q .url 2>&1
    if ($LASTEXITCODE -eq 0) {
        Write-Ok "a pull request for $Branch already exists: $existing"
    } else {
        Fail 'gh pr create failed and no existing pull request was found.'
    }
} else {
    Write-Ok ($created -join "`n")
}

Write-Step 'Done'
Write-Host "   branch : $Branch (upstream $upstream)"
Write-Host "   commit : $(@(Invoke-Git rev-parse --short HEAD)[0])"
