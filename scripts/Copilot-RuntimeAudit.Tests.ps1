#Requires -Modules @{ ModuleName = 'Pester'; ModuleVersion = '5.0' }
BeforeAll { . (Join-Path $PSScriptRoot 'Copilot-RuntimeAudit.ps1') }
Describe 'Copilot runtime archive provenance' {
    BeforeEach {
        $script:Cache = Join-Path $TestDrive ([guid]::NewGuid().ToString('N'))
        $script:Payload = Join-Path $TestDrive ([guid]::NewGuid().ToString('N'))
        $prebuild = Join-Path $Cache 'prebuilds/win32-x64'
        $native = Join-Path $Payload 'runtimes/win-x64/native'
        New-Item -ItemType Directory $prebuild, $native | Out-Null
        Set-Content (Join-Path $prebuild 'runtime.node') 'runtime'
        Set-Content (Join-Path $prebuild 'copilot-runtime.exe') 'wrapper'
        Set-Content (Join-Path $Cache 'LICENSE.md') 'retained licence'
        Copy-Item (Join-Path $prebuild '*') $native
        Copy-Item (Join-Path $prebuild 'runtime.node') (Join-Path $native 'copilot_runtime.dll')
        $archive = Join-Path $Cache 'copilot.tgz'
        $stream = [IO.File]::Create($archive)
        $gzip = [IO.Compression.GZipStream]::new($stream, [IO.Compression.CompressionMode]::Compress)
        $tar = [System.Formats.Tar.TarWriter]::new($gzip)
        try {
            foreach ($name in @('prebuilds/win32-x64/runtime.node', 'prebuilds/win32-x64/copilot-runtime.exe', 'LICENSE.md')) {
                $tar.WriteEntry((Join-Path $Cache $name), "package/$name")
            }
        } finally { $tar.Dispose(); $gzip.Dispose(); $stream.Dispose() }
        $script:Expected = (Get-FileHash $archive).Hash
    }
    It 'records the archive path for both runtime.node and the in-process alias' {
        $origins = Get-CopilotRuntimeOrigins $Cache $Payload $Expected
        $origins['runtimes/win-x64/native/copilot_runtime.dll'] | Should -Be 'github/copilot-cli/v1.0.98/github-copilot-1.0.98-win32-x64.tgz!package/prebuilds/win32-x64/runtime.node'
        $origins.Count | Should -Be 3
    }
    It 'rejects an archive with the wrong SHA-256' {
        { Get-CopilotRuntimeOrigins $Cache $Payload ('0' * 64) } | Should -Throw '*archive SHA-256*'
    }
    It 'rejects a modified cached extraction' {
        Set-Content (Join-Path $Cache 'prebuilds/win32-x64/runtime.node') 'changed'
        { Get-CopilotRuntimeOrigins $Cache $Payload $Expected } | Should -Throw '*extraction differs*'
    }
    It 'rejects an unknown runtime payload including non-binaries' {
        Set-Content (Join-Path $Payload 'runtimes/win-x64/native/unreviewed.json') '{}'
        { Get-CopilotRuntimeOrigins $Cache $Payload $Expected } | Should -Throw '*Unknown Copilot runtime origin*'
    }
    It 'rejects a modified runtime payload' {
        Set-Content (Join-Path $Payload 'runtimes/win-x64/native/runtime.node') 'changed'
        { Get-CopilotRuntimeOrigins $Cache $Payload $Expected } | Should -Throw '*Copilot runtime payload differs*'
    }
}
