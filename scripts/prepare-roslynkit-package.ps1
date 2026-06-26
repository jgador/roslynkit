Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "RoslynKit.Packaging.ps1")

$context = Get-RoslynKitToolingContext -ScriptPath $PSCommandPath

Write-Host "dotnet executable: $($context.DotNet)"
Write-Host "SDK anchor: $($context.RepoRoot)"
Write-Host "solution path: $($context.SolutionPath)"
Write-Host "tool scopes: global install from the stable folder feed and side-by-side tool-path install via the self-packaging dev installer"
Write-Host ""

Prepare-RoslynKitPackageFeed -Context $context -PackageFeedPath $context.PackageFeedPath -Label "RoslynKit package feed" -ResetFeed | Out-Null

Invoke-RoslynKitPack -Context $context -PackageFeedPath $context.PackageFeedPath

Assert-RoslynKitPackageExists -Context $context
Show-RoslynKitDogfoodCommands -Context $context
