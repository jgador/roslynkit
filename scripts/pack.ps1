[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "common/packaging.ps1")

$context = Get-RoslynKitToolingContext -ScriptPath $PSCommandPath

Write-Host "Packing RoslynKit $($context.PackageVersion)..."

# Start with an empty feed so the reported package was created by this invocation.
Prepare-RoslynKitPackageFeed -Context $context -PackageFeedPath $context.PackageFeedPath -Label "RoslynKit package feed" -ResetFeed | Out-Null

Invoke-RoslynKitPack -Context $context -PackageFeedPath $context.PackageFeedPath

Assert-RoslynKitPackageExists -Context $context
Write-Host "Package: $(Get-RoslynKitPackagePath -Context $context)"
Write-Host "Release guide: docs/dotnet-tool-release.md"
