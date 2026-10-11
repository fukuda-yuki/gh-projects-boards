<#
.SYNOPSIS
  Sets up the Claude Code ⇄ DeepSeek Harness bridge on Windows.

.DESCRIPTION
  Two things are installed:
    1. The DSH `headless` profile the bridge delegates into: the profile folder
       is created by the DSH launcher itself, then this script writes the patch
       layer that selects the OpenCode Go route and DeepSeek V4.1 Flash.
    2. With -Register, the bridge is registered in Claude Code as a local stdio
       MCP server (user scope by default).

  The bridge itself has no PATH dependency: it locates the DeepSeek Harness
  installation by absolute path and is started through the absolute Node.js
  path found here.

.EXAMPLE
  pwsh -File install.ps1
  pwsh -File install.ps1 -Register
  pwsh -File install.ps1 -Register -Scope project
#>
[CmdletBinding()]
param(
    [string]$BridgeDir = $PSScriptRoot,
    [string]$DshHome = $(if ($env:DSH_HOME) { $env:DSH_HOME } else { Join-Path $env:USERPROFILE '.dsh' }),
    [string]$ProfileName = 'headless',
    [string]$McpServerName = 'dsh-flash',
    [ValidateSet('user', 'project', 'local')][string]$Scope = 'user',
    [switch]$Register,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'

function Write-Step([string]$Message) { Write-Host "== $Message" -ForegroundColor Cyan }
function Write-Ok([string]$Message) { Write-Host "   $Message" -ForegroundColor Green }
function Write-Warn([string]$Message) { Write-Host "   $Message" -ForegroundColor Yellow }

function Resolve-Node {
    $command = Get-Command node -ErrorAction SilentlyContinue
    if (-not $command) {
        throw "Node.js was not found on PATH. Install Node.js 22.15 or newer (the bridge reads zstd-compressed DSH session logs)."
    }
    $path = $command.Source
    & $path -e "process.exit(typeof require('node:zlib').zstdDecompressSync === 'function' ? 0 : 1)" 2>$null
    if ($LASTEXITCODE -ne 0) {
        throw "Node.js at $path lacks zlib.zstdDecompressSync. Node.js 22.15+ or 23.8+ is required to read DSH session logs."
    }
    return $path
}

function Resolve-DshInstallation([string]$NodePath) {
    $resolver = Join-Path $BridgeDir 'lib\launcher.js'
    $json = & $NodePath -e "process.stdout.write(JSON.stringify(require(process.argv[1]).resolveInstallation(process.env)))" $resolver
    if ($LASTEXITCODE -ne 0) { throw "Could not locate the DeepSeek Harness installation:`n$json" }
    return $json | ConvertFrom-Json
}

function Test-EmptyPatch([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return $true }
    $meaningful = Get-Content -LiteralPath $Path | Where-Object { $_.Trim() -ne '' -and -not $_.Trim().StartsWith('#') }
    return ($meaningful.Count -eq 1 -and $meaningful[0].Trim() -eq '[]') -or $meaningful.Count -eq 0
}

Write-Step 'Locating Node.js and the DeepSeek Harness installation'
$node = Resolve-Node
Write-Ok "node      : $node"
$installation = Resolve-DshInstallation $node
Write-Ok "dsh root  : $($installation.root)  (found via $($installation.source))"
Write-Ok "cli entry : $($installation.cliJs)  [$($installation.cli_entry)]"

Write-Step "Preparing the DSH '$ProfileName' profile"
$profileDir = Join-Path $DshHome "profiles\$ProfileName"
if (-not (Test-Path -LiteralPath $installation.launcher)) {
    throw "The DSH launcher was not found at $($installation.launcher)."
}

# Loading the profile once makes DSH create the profile folder from its shipped
# template (base + headless); the app's own --help exits right after.
$launcherOutput = & $installation.launcher $ProfileName --help 2>&1
if ($LASTEXITCODE -ne 0) { throw "The DSH launcher could not load the '$ProfileName' profile:`n$($launcherOutput -join "`n")" }
if (-not (Test-Path -LiteralPath $profileDir)) { throw "DSH did not create $profileDir." }
Write-Ok "profile   : $profileDir"

$sourcePatch = Join-Path $BridgeDir 'profile\cordis.patch.yml'
$targetPatch = Join-Path $profileDir 'cordis.patch.yml'
$sourceText = (Get-Content -LiteralPath $sourcePatch -Raw).Trim()

if ((Test-Path -LiteralPath $targetPatch) -and ((Get-Content -LiteralPath $targetPatch -Raw).Trim() -eq $sourceText)) {
    Write-Ok 'patch     : already installed'
} elseif (Test-EmptyPatch $targetPatch) {
    Copy-Item -LiteralPath $sourcePatch -Destination $targetPatch -Force
    Write-Ok 'patch     : written (route opencode-go / deepseek-v4.1-flash)'
} elseif ($Force) {
    $backup = "$targetPatch.bak-$(Get-Date -Format 'yyyyMMdd-HHmmss')"
    Copy-Item -LiteralPath $targetPatch -Destination $backup -Force
    Copy-Item -LiteralPath $sourcePatch -Destination $targetPatch -Force
    Write-Warn "patch     : replaced; the previous layer is kept at $backup"
} else {
    throw "The profile already has a custom patch layer at $targetPatch. Merge profile\cordis.patch.yml by hand, or re-run with -Force to replace it (a backup is kept)."
}

Write-Step 'Verifying the composed profile'
$dump = & $installation.launcher --profile $ProfileName --dump-config 2>&1
$composed = $dump -join "`n"
if ($composed -notmatch 'opencode-go' -or $composed -notmatch 'deepseek-v4\.1-flash') {
    throw "The composed profile does not contain the expected route. Check $targetPatch."
}
Write-Ok 'provider  : opencode-go'
Write-Ok 'model     : deepseek-v4.1-flash'

Write-Step 'Checking the OpenCode Go credential'
$key = [Environment]::GetEnvironmentVariable('OPENCODE_API_KEY', 'User')
$processKey = [Environment]::GetEnvironmentVariable('OPENCODE_API_KEY', 'Process')
if ($key -or $processKey) {
    Write-Ok 'OPENCODE_API_KEY is present (value is never read or printed)'
} else {
    Write-Warn 'OPENCODE_API_KEY is not set for this user. Delegated runs authenticate through this variable; set it with:'
    Write-Warn '  [Environment]::SetEnvironmentVariable("OPENCODE_API_KEY", "<key>", "User")'
}

if ($Register) {
    Write-Step 'Registering the MCP server in Claude Code'
    $claude = Get-Command claude -ErrorAction SilentlyContinue
    if (-not $claude) {
        Write-Warn 'The claude CLI was not found on PATH; register manually with:'
        Write-Warn "  claude mcp add --scope $Scope $McpServerName -- `"$node`" `"$(Join-Path $BridgeDir 'server.js')`""
    } else {
        $serverPath = Join-Path $BridgeDir 'server.js'
        & $claude.Source mcp remove --scope $Scope $McpServerName 2>&1 | Out-Null
        $added = & $claude.Source mcp add --scope $Scope $McpServerName -- $node $serverPath 2>&1
        Write-Ok ($added -join ' ')
        $details = & $claude.Source mcp get $McpServerName 2>&1
        Write-Host ($details -join "`n")
    }
}

Write-Step 'Next steps'
Write-Host "   1. Restart Claude Code so it connects to the MCP server."
Write-Host "   2. Run the bridge's own checks from $BridgeDir :"
Write-Host "        node test/unit.js     # no model call"
Write-Host "        node test/e2e.js      # real delegation, needs OPENCODE_API_KEY"
Write-Host "   3. In Claude Code, the tools appear as mcp__$McpServerName`__dsh_delegate and friends."
