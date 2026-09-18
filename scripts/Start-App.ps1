[CmdletBinding()]
param(
    [switch]$NoBuild
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$solution = Join-Path $repo 'GhProjectsBoards.sln'
$app = Join-Path $repo 'src/GhProjectsBoards.App/bin/Release/net10.0-windows10.0.26100.0/win-x64/GhProjectsBoards.App.exe'
if (-not $NoBuild) {
    & dotnet build $solution --configuration Release --nologo -v minimal
    if ($LASTEXITCODE -ne 0) { throw 'The Release build failed; not starting the application.' }
}
if (-not (Test-Path -LiteralPath $app)) { throw 'Build GhProjectsBoards.sln in Release first.' }
# Start detached so the caller's run script finishes instead of waiting for the app to close.
$process = Start-Process -FilePath $app -WorkingDirectory (Split-Path $app -Parent) -PassThru
Write-Host "Application PID: $($process.Id)"
