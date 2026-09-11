[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "common/packaging.ps1")

$context = Get-RoslynKitToolingContext -ScriptPath $PSCommandPath
$globalCommandPath = Get-RoslynKitGlobalToolCommandPath
$validationRoot = Join-Path $context.RepoRoot "artifacts/global-command-validation/roslynkit"

# Reject a stale global command before spending time on the complete smoke suite.
Assert-RoslynKitCommandVersion `
    -CommandPath $globalCommandPath `
    -ExpectedVersion $context.PackageVersion

& (Join-Path $PSScriptRoot "test-commands.ps1") `
    -CommandPath $globalCommandPath `
    -ExpectedVersion $context.PackageVersion `
    -ValidationRoot $validationRoot

Write-Verbose "Installed command: $globalCommandPath"
