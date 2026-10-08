#requires -Version 7.0
[CmdletBinding()]
param([string]$ArtifactsRoot)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = Split-Path $PSScriptRoot -Parent
if (-not $ArtifactsRoot) { $ArtifactsRoot = Join-Path $repo 'TestResults/internal-distribution' }
if (-not [IO.Path]::IsPathFullyQualified($ArtifactsRoot)) { throw 'ArtifactsRoot must be an absolute path.' }
$run = Join-Path ([IO.Path]::GetFullPath($ArtifactsRoot)) ([guid]::NewGuid().ToString('N').Substring(0, 12))
$build = Join-Path $run 'build'
$package = Join-Path $run 'GhProjectsBoards-win-x64'
New-Item -ItemType Directory -Path $package | Out-Null

function Hash([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash }
function JsonFile($Value, [string]$Path) { $Value | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $Path -Encoding utf8 }
function Sources {
    @((Get-ChildItem -LiteralPath (Join-Path $repo 'src') -Recurse -File | Where-Object FullName -NotMatch '[\\/](bin|obj)[\\/]'),
      (Get-ChildItem -LiteralPath (Join-Path $repo 'docs/notices') -File),
      (Get-Item -LiteralPath $PSCommandPath, (Join-Path $repo 'docs/dependencies.md'), (Join-Path $repo 'docs/internal-distribution-ja.md')),
      (Get-ChildItem -LiteralPath (Join-Path $repo 'templates') -File)) | ForEach-Object { $_ } | Sort-Object FullName | ForEach-Object {
        [ordered]@{ path = [IO.Path]::GetRelativePath($repo, $_.FullName).Replace('\', '/'); sha256 = Hash $_.FullName }
    }
}

try {
    $source = @(Sources)
    $head = & git -C $repo rev-parse HEAD
    if ($LASTEXITCODE -ne 0) { throw 'Cannot identify source commit.' }
    $status = @(& git -C $repo status --porcelain)
    if ($LASTEXITCODE -ne 0) { throw 'Cannot identify worktree state.' }
    JsonFile ([ordered]@{ commit = $head; dirty = $status.Count -gt 0; files = $source }) (Join-Path $run 'source.json')
    $sdk = (& dotnet --version).Trim()
    if ($LASTEXITCODE -ne 0 -or $sdk -notlike '10.*') { throw '.NET SDK 10 is required on the packaging machine.' }
    $arguments = @('publish', (Join-Path $repo 'src/GhProjectsBoards.App/GhProjectsBoards.App.csproj'), '-c', 'Release', '-r', 'win-x64',
        '--self-contained', 'true', '--artifacts-path', $build, '-o', $package,
        '-p:WindowsAppSDKSelfContained=true', '-p:DebugType=None', '-p:DebugSymbols=false', '-p:ContinuousIntegrationBuild=true')
    JsonFile ([ordered]@{ executable = 'dotnet'; arguments = $arguments; sdk = $sdk }) (Join-Path $run 'command.json')
    & dotnet @arguments 2>&1 | Tee-Object -FilePath (Join-Path $run 'publish.log') | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed; logs and partial output are retained.' }
    if (($source | ConvertTo-Json -Depth 5 -Compress) -cne (@(Sources) | ConvertTo-Json -Depth 5 -Compress)) {
        throw 'Source files changed during publication. Retry after the source is stable.'
    }
    # WinUI 1.8's publish target omits the compiled application XAML and PRI from publish output.
    # Use only the resources belonging to this isolated build, never an existing checkout's bin.
    $appBuild = Join-Path $build 'bin/GhProjectsBoards.App/release_win-x64'
    if ((Hash (Join-Path $appBuild 'GhProjectsBoards.App.dll')) -ne (Hash (Join-Path $package 'GhProjectsBoards.App.dll'))) {
        throw 'The published application does not match its resource build.'
    }
    $appSource = Join-Path $repo 'src/GhProjectsBoards.App'
    $resources = @('GhProjectsBoards.App.pri') + @(Get-ChildItem -LiteralPath $appSource -Recurse -File -Filter '*.xaml' |
        Where-Object FullName -NotMatch '[\\/](bin|obj)[\\/]' | ForEach-Object {
            [IO.Path]::ChangeExtension([IO.Path]::GetRelativePath($appSource, $_.FullName), '.xbf')
        })
    foreach ($resource in $resources) {
        $built = Join-Path $appBuild $resource
        if (-not (Test-Path -LiteralPath $built -PathType Leaf)) { throw "Compiled application resource missing: $resource" }
        $destination = Join-Path $package $resource
        New-Item -ItemType Directory -Path (Split-Path $destination -Parent) -Force | Out-Null
        Copy-Item -LiteralPath $built -Destination $destination
        if ((Hash $built) -ne (Hash $destination)) { throw "Compiled application resource differs: $resource" }
    }

    $assetsFiles = @(Get-ChildItem -LiteralPath $build -Recurse -File -Filter 'project.assets.json' | Where-Object FullName -Match '[\\/]GhProjectsBoards.App[\\/]')
    if ($assetsFiles.Count -ne 1) { throw 'Expected one isolated app assets file.' }
    $assets = Get-Content -LiteralPath $assetsFiles[0].FullName -Raw | ConvertFrom-Json -AsHashtable
    $depsPath = Join-Path $package 'GhProjectsBoards.App.deps.json'
    $deps = Get-Content -LiteralPath $depsPath -Raw | ConvertFrom-Json -AsHashtable
    if (Test-Path -LiteralPath (Join-Path $package 'Microsoft.Windows.Widgets.Projection.dll')) { throw 'Widgets component projection must not ship.' }
    foreach ($target in $deps.targets.Values) {
        foreach ($entry in $target.Values) {
            if ($entry.ContainsKey('runtime') -and @($entry.runtime.Keys | Where-Object { $_ -like '*Microsoft.Windows.Widgets.Projection.dll' }).Count -gt 0) {
                throw 'The dependency manifest still loads the Widgets component projection.'
            }
        }
    }

    $noticeSource = Join-Path $repo 'docs/notices'
    foreach ($retained in (Get-Content -LiteralPath (Join-Path $noticeSource 'hashes.json') -Raw | ConvertFrom-Json)) {
        if ((Hash (Join-Path $noticeSource $retained.file)) -ne $retained.sha256) { throw "Retained notice changed: $($retained.file)" }
    }
    $notices = Join-Path $package 'notices'
    New-Item -ItemType Directory -Path $notices | Out-Null
    Get-ChildItem -LiteralPath $noticeSource -File | Copy-Item -Destination $notices
    Copy-Item -LiteralPath (Join-Path $repo 'docs/internal-distribution-ja.md') -Destination (Join-Path $package '使い方.md')

    $packageIds = @($assets.libraries.Keys | Where-Object { $assets.libraries[$_].type -eq 'package' })
    $packageIds += @($deps.libraries.Keys | Where-Object { $_ -like 'runtimepack.*' } | ForEach-Object { $_.Substring('runtimepack.'.Length) })
    $reviewedPackages = @(Get-Content -LiteralPath (Join-Path $noticeSource 'packages.json') -Raw | ConvertFrom-Json)
    $packages = @{}
    $packageRecords = @()
    foreach ($identity in ($packageIds | Sort-Object -Unique)) {
        $parts = $identity.Split('/')
        $location = @($assets.packageFolders.Keys | ForEach-Object { Join-Path $_ $identity.ToLowerInvariant() } | Where-Object { Test-Path -LiteralPath $_ -PathType Container }) | Select-Object -First 1
        if (-not $location) { throw "Cannot find exact package: $identity" }
        $packages[$identity] = $location
        $archiveHash = Join-Path $location ($parts[0].ToLowerInvariant() + '.' + $parts[1] + '.nupkg.sha512')
        if (-not (Test-Path -LiteralPath $archiveHash)) { throw "Missing package content identity: $identity" }
        $archiveSha512 = (Get-Content -LiteralPath $archiveHash -Raw).Trim()
        $contentHash = if ($assets.libraries.ContainsKey($identity)) { $assets.libraries[$identity].sha512 }
            else { (Get-Content -LiteralPath (Join-Path $location '.nupkg.metadata') -Raw | ConvertFrom-Json).contentHash }
        if ($assets.libraries.ContainsKey($identity)) {
            $reviewed = @($reviewedPackages | Where-Object { $_.id -eq $parts[0] -and $_.version -eq $parts[1] -and $_.sha512 -ceq $contentHash })
            if ($reviewed.Count -ne 1) { throw "Package is not in the reviewed notice inventory: $identity" }
        }
        $packageRecords += [ordered]@{ id = $parts[0]; version = $parts[1]; contentHash = $contentHash; archiveSha512 = $archiveSha512; source = "https://www.nuget.org/packages/$($parts[0])/$($parts[1])" }
        if ($parts[0] -eq 'Microsoft.NETCore.App.Runtime.win-x64') {
            foreach ($name in @('LICENSE.TXT', 'THIRD-PARTY-NOTICES.TXT')) {
                Copy-Item -LiteralPath (Join-Path $location $name) -Destination (Join-Path $notices ($parts[0] + '-' + $parts[1] + '-' + $name))
            }
        }
    }
    JsonFile $packageRecords (Join-Path $notices 'resolved-packages.json')

    $runtimeId = @($packages.Keys | Where-Object { $_ -like 'Microsoft.WindowsAppSDK.Runtime/*' })
    if ($runtimeId.Count -ne 1) { throw 'Expected exactly one Windows App Runtime package.' }
    $runtimeMsix = Join-Path $packages[$runtimeId[0]] 'tools/MSIX/win10-x64/Microsoft.WindowsAppRuntime.1.8.msix'
    $runtimeLicense = Get-Content -LiteralPath (Join-Path $packages[$runtimeId[0]] 'license.txt') -Raw
    if (-not $runtimeLicense.Contains('Any files that are binplaced with your application')) { throw 'Runtime package grant changed; review exact terms.' }
    $origins = @{}
    foreach ($resource in $resources) { $origins[$resource.Replace('\', '/')] = 'application build resources' }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($runtimeMsix)
    try {
        foreach ($entry in $archive.Entries) {
            if (-not $entry.Name) { continue }
            $outputFile = Join-Path $package $entry.FullName
            if (-not (Test-Path -LiteralPath $outputFile -PathType Leaf)) { continue }
            # resources.pri is regenerated by the app's XAML build rather than copied from the MSIX.
            if ($entry.FullName -eq 'resources.pri') { continue }
            $stream = $entry.Open()
            try { $expected = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)) } finally { $stream.Dispose() }
            if ((Hash $outputFile) -ne $expected) { throw "Runtime MSIX file changed: $($entry.FullName)" }
            $origins[$entry.FullName.Replace('\', '/')] = "$($runtimeId[0])/tools/MSIX/win10-x64/Microsoft.WindowsAppRuntime.1.8.msix!$($entry.FullName)"
        }
    } finally { $archive.Dispose() }
    foreach ($required in @('DWriteCore.dll', 'Microsoft.Windows.Widgets.dll', 'Microsoft.Windows.Widgets.winmd')) {
        if (-not $origins.ContainsKey($required)) { throw "Missing verified Runtime MSIX origin: $required" }
    }

    # Match remaining managed/native binaries by exact content, including implicit SDK runtime packs.
    $packageFiles = @{}
    foreach ($identity in $packages.Keys) {
        foreach ($file in (Get-ChildItem -LiteralPath $packages[$identity] -Recurse -File | Where-Object Extension -In '.dll', '.exe', '.winmd')) {
            if (-not $packageFiles.ContainsKey($file.Name)) { $packageFiles[$file.Name] = @() }
            $packageFiles[$file.Name] += [PSCustomObject]@{ identity = $identity; file = $file.FullName }
        }
    }
    $ownBinaries = @('GhProjectsBoards.App.exe', 'GhProjectsBoards.App.dll', 'GhProjectsBoards.Core.dll')
    foreach ($file in (Get-ChildItem -LiteralPath $package -Recurse -File | Where-Object Extension -In '.dll', '.exe', '.winmd')) {
        $relative = [IO.Path]::GetRelativePath($package, $file.FullName).Replace('\', '/')
        if ($origins.ContainsKey($relative)) { continue }
        if ($relative -in $ownBinaries) { $origins[$relative] = 'application build'; continue }
        $hash = Hash $file.FullName
        $matches = @($packageFiles[$file.Name] | Where-Object { (Hash $_.file) -eq $hash })
        if ($matches.Count -eq 0) { throw "Unknown binary origin: $relative" }
        $match = $matches[0]
        $origins[$relative] = $match.identity + '/' + [IO.Path]::GetRelativePath($packages[$match.identity], $match.file).Replace('\', '/')
        if ($match.identity -like 'Microsoft.WindowsAppSDK.Widgets/*') { throw "Unexpected Widgets component file: $relative" }
    }
    $holidays = Get-Content -LiteralPath (Join-Path $repo 'src/GhProjectsBoards.Core/Planning/JapanHolidays2025-2027.json') -Raw | ConvertFrom-Json
    @"
出典：内閣府「国民の祝日」 $($holidays.Source)
2025–2027年分を抽出し、本アプリ用JSONへ加工しました。内閣府が本アプリを作成・承認したものではありません。
取得日時：$($holidays.RetrievedAt) / 元CSV SHA-256：$($holidays.SourceSha256)
利用規約：https://www.cao.go.jp/notice/rule.html
"@ | Set-Content -LiteralPath (Join-Path $notices 'Japan-holidays-attribution.txt') -Encoding utf8

    $payload = @(Get-ChildItem -LiteralPath $package -Recurse -File | Sort-Object FullName | ForEach-Object {
        $relative = [IO.Path]::GetRelativePath($package, $_.FullName).Replace('\', '/')
        [ordered]@{ path = $relative; bytes = $_.Length; sha256 = Hash $_.FullName; origin = $origins[$relative] }
    })
    $manifest = [ordered]@{ format = 1; platform = 'Windows 11 x64'; packaging = 'unpackaged self-contained'; sdk = $sdk;
        sourceCommit = $head; sourceDirty = $status.Count -gt 0; sourceManifestSha256 = Hash (Join-Path $run 'source.json');
        runtimeMsix = [ordered]@{ package = $runtimeId[0]; sha256 = Hash $runtimeMsix };
        validation = [ordered]@{ widgetComponentExcluded = $true; binaryOriginsVerified = $true; applicationResourcesVerified = $resources;
            cleanPc = 'not established'; humanAcceptance = 'not established' };
        files = $payload }
    JsonFile $manifest (Join-Path $package 'distribution-manifest.json')
    $zip = Join-Path $run 'GhProjectsBoards-win-x64.zip'
    [IO.Compression.ZipFile]::CreateFromDirectory($package, $zip, [IO.Compression.CompressionLevel]::Optimal, $true)
    JsonFile ([ordered]@{ zip = [IO.Path]::GetFileName($zip); sha256 = Hash $zip; bytes = (Get-Item -LiteralPath $zip).Length;
        files = $payload.Count + 1; checks = 'Widgets assets excluded; all binary origins verified; notices retained' }) (Join-Path $run 'result.json')
    Write-Output "Package: $zip"
    Write-Output "Evidence: $run"
} catch {
    $_ | Out-String | Set-Content -LiteralPath (Join-Path $run 'failure.txt') -Encoding utf8
    throw
}
