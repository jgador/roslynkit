[CmdletBinding()]
param(
    [switch]$PrintManualCommands
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "RoslynKit.Packaging.ps1")

$context = Get-RoslynKitToolingContext -ScriptPath $PSCommandPath
$globalCommandPath = Get-RoslynKitGlobalToolCommandPath
$validationRoot = Join-Path $context.RepoRoot "artifacts\global-command-validation\roslynkit"

Assert-RoslynKitCommandVersion `
    -CommandPath $globalCommandPath `
    -ExpectedVersion $context.PackageVersion

& (Join-Path $PSScriptRoot "test-roslynkit-commands.ps1") `
    -CommandPath $globalCommandPath `
    -ExpectedVersion $context.PackageVersion `
    -ValidationRoot $validationRoot `
    -PrintManualCommands:$PrintManualCommands

Write-Host ""
if ($PrintManualCommands)
{
    Write-Host "RoslynKit $($context.PackageVersion) manual global command checklist prepared."
}
else
{
    Write-Host "RoslynKit $($context.PackageVersion) global command smoke test passed."
}

Write-Host "Installed command: $globalCommandPath"
