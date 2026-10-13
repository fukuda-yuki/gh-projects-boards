#Requires -Modules @{ ModuleName = 'Pester'; ModuleVersion = '5.0' }
BeforeAll {
    $script:Verify = Join-Path $PSScriptRoot 'Test-ReleasePackage.ps1'
    function Save-Manifest {
        $script:Manifest | ConvertTo-Json -Depth 10 | Set-Content (Join-Path $script:Root 'distribution-manifest.json')
    }
}
Describe 'Extracted release package' {
    BeforeEach {
        $script:Root = Join-Path $TestDrive ([guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory $script:Root | Out-Null
        $files = foreach ($name in @('GhProjectsBoards.App.exe', 'GhProjectsBoards.App.dll', 'coreclr.dll', 'hostpolicy.dll',
            'Microsoft.UI.Xaml.dll', 'GhProjectsBoards.App.pri', 'App.xbf', '使い方.md', 'マニュアル.md', 'manual/images/01-help.png', 'templates/new-tasks.csv', 'notices/license.txt')) {
            $path = Join-Path $script:Root $name
            New-Item -ItemType Directory (Split-Path $path -Parent) -Force | Out-Null
            Set-Content -LiteralPath $path 'fixture payload'
            @{ path = $name; bytes = (Get-Item $path).Length; sha256 = (Get-FileHash $path).Hash }
        }
        $script:Manifest = @{ format = 1; sourceCommit = 'abc123'; sourceDirty = $false; version = '1.2.3-rc.1'; files = @($files) }
        Save-Manifest
    }
    It 'returns the extracted executable only after all payload hashes match' {
        & $script:Verify -PackageRoot $script:Root -ExpectedCommit abc123 -ExpectedVersion '1.2.3-rc.1' |
            Should -Be (Join-Path $script:Root 'GhProjectsBoards.App.exe')
    }
    It 'rejects a modified payload' {
        Add-Content (Join-Path $script:Root 'coreclr.dll') 'corruption'
        { & $script:Verify $script:Root abc123 '1.2.3-rc.1' } | Should -Throw '*Payload differs*'
    }
    It 'rejects a missing payload' {
        Remove-Item (Join-Path $script:Root 'App.xbf')
        { & $script:Verify $script:Root abc123 '1.2.3-rc.1' } | Should -Throw '*Missing payload*'
    }
    It 'rejects extra files not in the manifest' {
        Set-Content (Join-Path $script:Root 'credentials.json') '{}'
        { & $script:Verify $script:Root abc123 '1.2.3-rc.1' } | Should -Throw '*Unexpected payload*'
    }
    It 'rejects a package missing <Required> even if the manifest agrees' -TestCases @(
        @{ Required = 'coreclr.dll' }, @{ Required = 'マニュアル.md' }, @{ Required = 'manual/images/01-help.png' }
    ) {
        param($Required)
        Remove-Item (Join-Path $script:Root $Required)
        $script:Manifest.files = @($script:Manifest.files | Where-Object path -NE $Required)
        Save-Manifest
        { & $script:Verify $script:Root abc123 '1.2.3-rc.1' } | Should -Throw '*Required payload*'
    }
    It 'rejects a mismatched source commit' {
        { & $script:Verify $script:Root wrong '1.2.3-rc.1' } | Should -Throw '*source/version*'
    }
    It 'rejects a mismatched version' {
        { & $script:Verify $script:Root abc123 '1.2.4' } | Should -Throw '*source/version*'
    }
    It 'rejects dirty source' {
        $script:Manifest.sourceDirty = $true
        Save-Manifest
        { & $script:Verify $script:Root abc123 '1.2.3-rc.1' } | Should -Throw '*source/version*'
    }
    It 'rejects duplicate payload identities' {
        $script:Manifest.files += $script:Manifest.files[0]
        Save-Manifest
        { & $script:Verify $script:Root abc123 '1.2.3-rc.1' } | Should -Throw '*Duplicate*'
    }
    It 'rejects a path escaping the package' {
        $script:Manifest.files[0].path = '../outside.dll'
        Save-Manifest
        { & $script:Verify $script:Root abc123 '1.2.3-rc.1' } | Should -Throw '*Unsafe*'
    }
}
