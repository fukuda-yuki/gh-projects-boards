param(
    [Parameter(Mandatory)][ValidateSet('Export','Restore')][string]$Mode,
    [Parameter(Mandatory)][string]$DataRoot,
    [Parameter(Mandatory)][string]$File,
    [Parameter(Mandatory)][string]$HostName,
    [Parameter(Mandatory)][long]$ViewerId,
    [string]$CoreAssembly
)
$ErrorActionPreference = 'Stop'
if ([Environment]::Version.Major -lt 10) { throw 'Use PowerShell on .NET 10 or newer after building Release.' }
if (![IO.Path]::IsPathFullyQualified($DataRoot) -or ![IO.Path]::IsPathFullyQualified($File)) { throw 'Absolute data-root and backup paths are required.' }
if (!$CoreAssembly) { $CoreAssembly = Join-Path (Split-Path $PSScriptRoot -Parent) 'src/GhProjectsBoards.Core/bin/Release/net10.0/GhProjectsBoards.Core.dll' }
# A thin offline entry point to the same validated store used by the app. This
# script has no serializer, merge, remote access or alternate recovery rules.
$assembly = [Reflection.Assembly]::LoadFrom((Resolve-Path -LiteralPath $CoreAssembly))
$storeType = $assembly.GetType('GhProjectsBoards.Core.Projects.DraftStore', $true)
$scopeType = $assembly.GetType('GhProjectsBoards.Core.Projects.ConnectionScope', $true)
$scope = [Activator]::CreateInstance($scopeType, [object[]]@($HostName, $ViewerId))
$store = [Activator]::CreateInstance($storeType, [object[]]@($DataRoot))
if ($Mode -eq 'Export') {
    $operation = $storeType.GetMethod('ExportBackupAsync').Invoke($store, [object[]]@($scope, $File))
} else {
    $operation = $storeType.GetMethod('RestoreBackupAsync').Invoke($store, [object[]]@($File, $scope))
}
[void]$operation.GetAwaiter().GetResult()
Write-Output "$Mode complete. Host/viewer scope checked; GitHub was not contacted."
