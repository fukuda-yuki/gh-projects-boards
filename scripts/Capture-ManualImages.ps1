#requires -Version 7.0
[CmdletBinding()]
param([switch]$NoBuild)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = Split-Path $PSScriptRoot -Parent
$run = Join-Path $repo ('TestResults/manual-images/' + [guid]::NewGuid().ToString('N'))
$dataRoot = Join-Path $run 'data'
$staged = Join-Path $run 'images'
$destination = Join-Path $repo 'docs/manual/images'
$app = Join-Path $repo 'src/GhProjectsBoards.App/bin/Release/net10.0-windows10.0.26100.0/win-x64/GhProjectsBoards.App.exe'
$automation = Join-Path $PSScriptRoot 'Invoke-EvaluationUi.ps1'
New-Item -ItemType Directory -Path $staged -Force | Out-Null
$record = [ordered]@{
    command = './scripts/Capture-ManualImages.ps1' + $(if ($NoBuild) { ' -NoBuild' } else { '' })
    startedUtc = [DateTimeOffset]::UtcNow.ToString('o'); finishedUtc = $null
    dataRoot = $dataRoot; commit = $null; dirty = $null; sources = @(); binaries = @()
    version = '2027.04'; statusDate = '2027-01-13'; today = '2027-01-14'
    effectiveWidth = 1600; effectiveHeight = 1000; dpi = $null; physicalWidth = $null; physicalHeight = $null
    processId = $null; closed = $false; forcedClose = $false; outcome = 'started'; failure = $null; files = @()
}
$process = $null
$oldDpi = [IntPtr]::Zero
function Save-Record { $record | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $run 'record.json') -Encoding utf8 }
function File-Record([string]$Path) {
    [ordered]@{ path = [IO.Path]::GetRelativePath($repo, $Path).Replace('\', '/'); sha256 = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash }
}
function Ui {
    param([string]$Action, [string]$Id, [string]$Name, [string]$Text, [string]$Keys, [string]$Out, [switch]$All,
        [int]$X, [int]$Y, [int]$Wheel)
    $parameters = @{ Action = $Action; ProcId = $process.Id }
    foreach ($key in @('Id', 'Name', 'Text', 'Keys', 'Out', 'X', 'Y', 'Wheel')) {
        if ($PSBoundParameters.ContainsKey($key)) { $parameters[$key] = $PSBoundParameters[$key] }
    }
    if ($All) { $parameters.All = $true }
    & $automation @parameters
}
function Tree { (Ui tree) -join "`n" }
function Wait-State([string]$Pattern, [int]$Seconds = 90) {
    $until = [DateTime]::UtcNow.AddSeconds($Seconds)
    do {
        if ($process.HasExited) { throw "App exited while waiting for $Pattern" }
        $tree = Tree
        if ($tree -match $Pattern) { return $tree }
        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $until)
    $tree | Set-Content -LiteralPath (Join-Path $run 'last-tree.txt') -Encoding utf8
    throw "Timed out waiting for UI state: $Pattern"
}
function Wait-Control([string]$Id) { Wait-State ('#' + [regex]::Escape($Id) + '(?:\s|$)') | Out-Null }
function Capture([string]$Name, [string]$Id) {
    Wait-Control $Id
    Start-Sleep -Milliseconds 400
    $path = Join-Path $staged ($Name + '.png')
    Ui shot -Out $path | Out-Host
    if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or (Get-Item -LiteralPath $path).Length -eq 0) { throw "Empty capture: $Name" }
    $record.files += File-Record $path
    Save-Record
}
function Set-TitleFilter([string]$Text) {
    Ui setvalue -Id PlanSheetFilter -Text $Text | Out-Null
    Start-Sleep -Milliseconds 350
}
function Edit-Cell([string]$Id, [string]$Text) {
    Wait-Control $Id
    Ui click -Id $Id | Out-Null
    Ui key -Keys '{F2}' | Out-Null
    Ui setvalue -Id $Id -Text $Text | Out-Null
    Ui key -Keys '{ENTER}' | Out-Null
    # Effort cells display the accepted value with its unit (24 -> 24h).
    Wait-State ('#' + [regex]::Escape($Id) + " val='" + [regex]::Escape($Text) + "h?'") | Out-Null
}
# The settings list jumps to a section; a control scrolled out above the page has no rectangle to steer the wheel by.
function Show-Section([string]$Key) {
    Wait-Control "PlanSettingsSection_$Key"
    Ui click -Id "PlanSettingsSection_$Key" | Out-Null
    Start-Sleep -Milliseconds 400
}
function Reveal([string[]]$Ids) {
    # The settings page keeps every control in the tree; controls never scrolled into view have no rectangle yet.
    # Wheel the page until the first control sits in the upper part of the window; no value changes.
    Wait-Control $Ids[0]
    $pattern = '#' + [regex]::Escape($Ids[0]) + '(?: [^\n]*)? @(-?\d+),(-?\d+) '
    $low = [int](180 * $record.dpi / 96); $high = [int](360 * $record.dpi / 96)
    for ($step = 0; $step -lt 120; $step++) {
        $top = if ((Tree) -match $pattern) { [int]$Matches[2] } else { [int]::MaxValue }
        if ($top -ge $low -and $top -le $high) { Start-Sleep -Milliseconds 300; return }
        $wheel = if ($top -lt $low) { 120 } elseif ($top -gt 4 * $high) { -360 } else { -120 }
        Ui scroll -X ([int](800 * $record.dpi / 96)) -Y ([int](600 * $record.dpi / 96)) -Wheel $wheel | Out-Null
        Start-Sleep -Milliseconds 150
    }
    Tree | Set-Content -LiteralPath (Join-Path $run 'last-tree.txt') -Encoding utf8
    throw "Scrolling did not reveal: $($Ids[0])"
}

try {
    $record.commit = (& git -C $repo rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'Cannot identify source commit.' }
    $record.dirty = @(& git -C $repo status --porcelain).Count -gt 0
    if ($LASTEXITCODE -ne 0) { throw 'Cannot identify source state.' }
    $sourceFiles = @($PSCommandPath, $automation, (Join-Path $PSScriptRoot 'Start-Evaluation.ps1'),
        (Join-Path $repo 'docs/user-manual.md'), (Join-Path $repo 'evaluation/version-plan.json'))
    $sourceFiles += Get-ChildItem -LiteralPath (Join-Path $repo 'src'), (Join-Path $repo 'tests/GhProjectsBoards.Tests') -Recurse -File |
        Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' -and $_.Extension -in '.cs', '.xaml', '.csproj' } | ForEach-Object FullName
    $record.sources = @($sourceFiles | Sort-Object -Unique | ForEach-Object { File-Record $_ })
    Save-Record
    if (-not $NoBuild) {
        & dotnet build (Join-Path $repo 'GhProjectsBoards.sln') -c Release --no-restore -m:1 -nr:false
        if ($LASTEXITCODE -ne 0) { throw 'Release build failed.' }
    }
    & (Join-Path $PSScriptRoot 'Start-Evaluation.ps1') -NoBuild -PrepareOnly -Configuration Release -Version 2027.04 -StatusDate $record.statusDate -DataRoot $dataRoot
    $record.binaries = @($app, (Join-Path (Split-Path $app -Parent) 'GhProjectsBoards.App.dll'),
        (Join-Path (Split-Path $app -Parent) 'GhProjectsBoards.Core.dll'), (Join-Path $dataRoot 'fake-gh/bin/GhProjectsBoards.Tests.dll')) |
        ForEach-Object { File-Record $_ }
    $start = [Diagnostics.ProcessStartInfo]::new($app)
    $start.UseShellExecute = $false
    $start.WorkingDirectory = Split-Path $app -Parent
    foreach ($name in @('GH_TOKEN', 'GITHUB_TOKEN', 'GH_ENTERPRISE_TOKEN', 'GITHUB_ENTERPRISE_TOKEN', 'GH_DEBUG', 'GH_HOST', 'GH_REPO', 'GHPB_PLAN_METRICS', 'GHPB_PUBLISH_METRICS')) {
        [void]$start.Environment.Remove($name)
    }
    $start.Environment['GHPB_DATA_ROOT'] = $dataRoot
    $start.Environment['GHPB_TODAY'] = $record.today
    $start.Environment['GH_CONFIG_DIR'] = Join-Path $dataRoot 'fake-gh'
    $start.Environment['PATH'] = (Join-Path $dataRoot 'fake-gh/bin') + [IO.Path]::PathSeparator + $start.Environment['PATH']
    $process = [Diagnostics.Process]::Start($start)
    $record.processId = $process.Id
    $until = [DateTime]::UtcNow.AddSeconds(60)
    do {
        $process.Refresh()
        if ($process.HasExited) { throw 'App exited before opening its window.' }
        if ($process.MainWindowHandle -ne [IntPtr]::Zero) { break }
        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $until)
    if ($process.MainWindowHandle -eq [IntPtr]::Zero) { throw 'App window did not appear.' }
    if (-not ('ManualCaptureWindow' -as [type])) {
        Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class ManualCaptureWindow {
    [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
}
'@
    }
    # Thread-local DPI awareness changes no desktop setting and also applies to shot's Win32 rectangle.
    $oldDpi = [ManualCaptureWindow]::SetThreadDpiAwarenessContext([IntPtr]::new(-4))
    if ($oldDpi -eq [IntPtr]::Zero) { throw 'Cannot select per-monitor DPI coordinates.' }
    $window = $process.MainWindowHandle
    [ManualCaptureWindow]::ShowWindow($window, 9) | Out-Null
    $record.dpi = [ManualCaptureWindow]::GetDpiForWindow($window)
    if ($record.dpi -eq 0) { throw 'Cannot read window DPI.' }
    $width = [int][Math]::Round(1600 * $record.dpi / 96)
    $height = [int][Math]::Round(1000 * $record.dpi / 96)
    # Keep the current monitor and position; Windows may have a nonzero virtual-desktop origin.
    if (-not [ManualCaptureWindow]::SetWindowPos($window, [IntPtr]::Zero, 0, 0, $width, $height, 0x0016)) { throw 'SetWindowPos failed.' }
    $rect = [ManualCaptureWindow+Rect]::new()
    if (-not [ManualCaptureWindow]::GetWindowRect($window, [ref]$rect)) { throw 'Cannot verify window size.' }
    $record.physicalWidth = $rect.Right - $rect.Left; $record.physicalHeight = $rect.Bottom - $rect.Top
    if ($record.physicalWidth -ne $width -or $record.physicalHeight -ne $height) { throw 'Window did not accept the requested effective size.' }

    Capture '02-connection' 'PlanConnect'
    Ui invoke -Id PlanConnect | Out-Null
    Wait-Control PlanProjectOwners
    Wait-Control PlanProjectRepositories
    Wait-State "第2027\.04版 fixture-user プロジェクト 3" | Out-Null
    Capture '03-project-chooser' 'AvailableProjects'
    Ui select -Name '第2027.04版 fixture-user プロジェクト 3' | Out-Null
    Wait-Control PlanTasks
    Wait-State '未発行 0 タスク' | Out-Null
    Capture '04-plan' 'PlanSheetFilter'
    Ui click -Id PlanProjectPicker | Out-Null
    Ui key -Keys '{ESC}{TAB}' | Out-Null
    Capture '01-help' 'PlanHelp'

    # This fixture task is intentionally open at Remaining 0, so both effort fields are editable.
    Set-TitleFilter 'R09 SS-003'
    $tree = Wait-State "#PlanCell[1-9]\d*_Title.*val='R09 SS-003"
    # The filter keeps the matching task's requirement on screen as well, so match the task by its title.
    $cells = @([regex]::Matches($tree, "#PlanCell([1-9]\d*)_Title val='R09 SS-003") | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)
    if ($cells.Count -ne 1) { throw "Expected one filtered task, found $($cells.Count)." }
    $row = $cells[0]
    Ui click -Id "PlanCell${row}_Title" | Out-Null
    Capture '05-selection' 'PlanStartReason'
    Edit-Cell "PlanCell${row}_Actual" '24'
    Edit-Cell "PlanCell${row}_Remaining" '32'
    Ui click -Id "PlanCell${row}_Remaining" | Out-Null
    Capture '07-effort-edit' "PlanCell${row}_Actual"
    Ui invoke -Id PlanRefresh | Out-Null
    # #238: a completed read opens no result bar; the status date moves to the read day and the strip shows the read time.
    Wait-State "#PlanStatusDate val='2027/01/14" | Out-Null
    Capture '08-refresh' 'PlanReadResult'
    Set-TitleFilter 'R09 '
    Wait-Control "PlanCell${row}_Title"
    Ui click -Id "PlanCell${row}_Title" | Out-Null
    Capture '06-states' "PlanIndicator$row"
    Ui invoke -Id PlanPublish | Out-Null
    Wait-Control PlanPublishConfirm
    Capture '09-publish' 'PlanPublishLines'
    Ui invoke -Id PlanPublishClose | Out-Null

    Set-TitleFilter ''
    Ui select -Id PlanShowSummary | Out-Null
    Capture '10-summary' 'SummaryFinish'
    Reveal @('SummaryMode')
    Ui click -Id SummaryMode | Out-Null
    Ui key -Keys '{HOME}{DOWN}{ENTER}' | Out-Null
    Capture '11-people' 'SummaryMode'

    Ui invoke -Id PlanShowSettings | Out-Null
    Wait-Control PlanMapEstimate
    Ui click -Id PlanMapEstimate | Out-Null
    Ui key -Keys '{ESC}' | Out-Null
    Show-Section calendar
    Reveal @('PlanCompanyDaysOffDate')
    Capture '14-calendar' 'PlanCompanyDaysOffDate'
    Show-Section period
    Reveal @('PlanProjectStart', 'PlanProjectEnd')
    Capture '12-period' 'PlanProjectStart'
    Show-Section phases
    Reveal @('PlanPhaseMilestone_SA')
    Capture '13-milestones' 'PlanPhaseMilestone_SA'
    Ui invoke -Id PlanSettingsBack | Out-Null
    Ui select -Id PlanShowTasks | Out-Null
    # 表示列 opens the same settings section; reach it through settings so the capture does not depend on the overflow popup.
    Ui invoke -Id PlanShowSettings | Out-Null
    Show-Section columns
    Reveal @('PlanColumnReset')
    Capture '15-columns' 'PlanColumnSettings'
    Ui invoke -Id PlanSettingsBack | Out-Null
    # The window capture omits popup menus, so overflow commands are described in text instead of an image.
    if ($record.files.Count -ne 15) { throw 'Not all planned images were captured.' }
    $record.outcome = 'captured'
}
catch {
    $record.outcome = 'failed'; $record.failure = $_.Exception.Message
    throw
}
finally {
    if ($null -ne $process) {
        try {
            if (-not $process.HasExited) {
                [void]$process.CloseMainWindow()
                if (-not $process.WaitForExit(15000)) {
                    # Only this run's offline process and its owned children may be stopped.
                    $record.forcedClose = $true
                    $record.outcome = 'failed'
                    if (-not $record.failure) { $record.failure = 'App did not close normally.' }
                    $process.Kill($true); $process.WaitForExit()
                }
            }
            $record.closed = $process.HasExited
        }
        catch {
            $record.outcome = 'failed'
            if (-not $record.failure) { $record.failure = $_.Exception.Message }
        }
        finally { $process.Dispose() }
    }
    if ($oldDpi -ne [IntPtr]::Zero) { [ManualCaptureWindow]::SetThreadDpiAwarenessContext($oldDpi) | Out-Null }
    $record.finishedUtc = [DateTimeOffset]::UtcNow.ToString('o')
    Save-Record
}
if (-not $record.closed -or $record.forcedClose) { throw "App did not close normally; staged captures retained at $run." }
try {
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    Get-ChildItem -LiteralPath $staged -Filter '*.png' -File | Copy-Item -Destination $destination -Force
    $record.outcome = 'complete'
}
catch { $record.outcome = 'failed'; $record.failure = $_.Exception.Message; throw }
finally { Save-Record }
Write-Host "Manual images: $destination"
Write-Host "Run record: $(Join-Path $run 'record.json')"
Write-Host 'Inspect every image, then rebuild Release to copy the images beside the executable.'
