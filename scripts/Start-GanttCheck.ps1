[CmdletBinding()]
param([string]$DataRoot, [switch]$Resume, [switch]$PrepareOnly)

# Share source verification, fixture isolation and resume protection with the
# planning evaluation. Fixture metadata must not enter RegistrationStore's root.
& (Join-Path $PSScriptRoot 'Start-PlanningCheck.ps1') -Scenario Load @PSBoundParameters
