[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "common/packaging.ps1")

$context = Get-RoslynKitToolingContext -ScriptPath $PSCommandPath
$validationRoot = Join-Path $context.RepoRoot "artifacts/package-validation/roslynkit"
$commandTestScript = Join-Path $PSScriptRoot "test-commands.ps1"

Write-Host "Testing RoslynKit $($context.PackageVersion) from the local package..."
$validation = Invoke-RoslynKitPackageValidation -Context $context -ValidationRoot $validationRoot -Action {
    param($installation)

    & $commandTestScript `
        -CommandPath $installation.CommandPath `
        -ExpectedVersion $context.PackageVersion `
        -ValidationRoot (Join-Path $validationRoot "command-smoke")
}

Write-Host "Package: $($validation.PackagePath)"
Write-Host "SHA-256: $($validation.PackageHash)"
Write-Verbose "Installed command: $($validation.CommandPath)"
