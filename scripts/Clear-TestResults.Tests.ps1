#Requires -Modules @{ ModuleName = 'Pester'; ModuleVersion = '5.0' }

BeforeAll {
    $script:Sweep = Join-Path $PSScriptRoot 'Clear-TestResults.ps1'
    function New-Entry([string]$RelativePath, [int]$AgeDays, [switch]$Directory) {
        $path = Join-Path $script:Root $RelativePath
        New-Item -ItemType Directory -Force -Path (Split-Path $path -Parent) | Out-Null
        if ($Directory) { New-Item -ItemType Directory -Force -Path $path | Out-Null }
        else { Set-Content -LiteralPath $path -Value 'x' }
        (Get-Item -LiteralPath $path).LastWriteTimeUtc = [DateTime]::UtcNow.AddDays(-$AgeDays)
    }
    function Set-Age([string]$RelativePath, [int]$AgeDays) {
        (Get-Item -LiteralPath (Join-Path $script:Root $RelativePath)).LastWriteTimeUtc = [DateTime]::UtcNow.AddDays(-$AgeDays)
    }
    function Test-Entry([string]$RelativePath) { Test-Path -LiteralPath (Join-Path $script:Root $RelativePath) }
}

Describe 'Clear-TestResults' {
    BeforeEach {
        $script:Root = Join-Path ([IO.Path]::GetTempPath()) ('ghpb-sweep-' + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $script:Root | Out-Null
        $env:GHPB_TESTRESULTS_DAYS = $null
    }

    AfterEach {
        Remove-Item -LiteralPath $script:Root -Recurse -Force -ErrorAction SilentlyContinue
        $env:GHPB_TESTRESULTS_DAYS = $null
    }

    It 'removes top-level files and folders unchanged for the period and keeps newer ones' {
        New-Entry 'issue65/native.etl' 30
        Set-Age 'issue65' 30
        New-Entry 'old.log' 15
        New-Entry 'issue131/core.trx' 2
        New-Entry 'new.log' 13

        & $script:Sweep -Root $script:Root

        Test-Entry 'issue65' | Should -BeFalse
        Test-Entry 'old.log' | Should -BeFalse
        Test-Entry 'issue131/core.trx' | Should -BeTrue
        Test-Entry 'new.log' | Should -BeTrue
    }

    It 'keeps a folder whose only recent change is deep inside it' {
        New-Entry 'workspace-evaluation/PlanningEditor/v1/old-settings.json' 60
        New-Entry 'workspace-evaluation/PlanningEditor/v1/projects/p3.json' 1
        Set-Age 'workspace-evaluation/PlanningEditor/v1/projects' 60
        Set-Age 'workspace-evaluation/PlanningEditor/v1' 60
        Set-Age 'workspace-evaluation/PlanningEditor' 60
        Set-Age 'workspace-evaluation' 60

        & $script:Sweep -Root $script:Root

        Test-Entry 'workspace-evaluation/PlanningEditor/v1/old-settings.json' | Should -BeTrue
        Test-Entry 'workspace-evaluation/PlanningEditor/v1/projects/p3.json' | Should -BeTrue
    }

    It 'judges each run inside a script run folder separately and keeps the run folder' {
        New-Entry 'ui-integration/run-20260901-101010-000-aaaaaaaa/results.xml' 40
        Set-Age 'ui-integration/run-20260901-101010-000-aaaaaaaa' 40
        New-Entry 'ui-integration/run-20261009-101010-000-bbbbbbbb/results.xml' 1
        New-Entry 'e2e/20260901-101010-0123/e2e.trx' 40
        Set-Age 'e2e/20260901-101010-0123' 40
        Set-Age 'e2e' 40

        & $script:Sweep -Root $script:Root

        Test-Entry 'ui-integration/run-20260901-101010-000-aaaaaaaa' | Should -BeFalse
        Test-Entry 'ui-integration/run-20261009-101010-000-bbbbbbbb/results.xml' | Should -BeTrue
        Test-Entry 'e2e/20260901-101010-0123' | Should -BeFalse
        Test-Entry 'e2e' | Should -BeTrue
    }

    It 'uses GHPB_TESTRESULTS_DAYS as the period' {
        New-Entry 'three-days.log' 3
        New-Entry 'one-day.log' 1
        $env:GHPB_TESTRESULTS_DAYS = '2'

        & $script:Sweep -Root $script:Root

        Test-Entry 'three-days.log' | Should -BeFalse
        Test-Entry 'one-day.log' | Should -BeTrue
    }

    It 'rejects a period of <Value> and deletes nothing' -TestCases @(
        @{ Value = '0' }, @{ Value = '-3' }, @{ Value = 'two' }
    ) {
        New-Entry 'old.log' 400
        $env:GHPB_TESTRESULTS_DAYS = $Value

        { & $script:Sweep -Root $script:Root } | Should -Throw

        Test-Entry 'old.log' | Should -BeTrue
    }

    It 'does nothing when the results folder does not exist' {
        { & $script:Sweep -Root (Join-Path $script:Root 'missing') } | Should -Not -Throw
    }
}
