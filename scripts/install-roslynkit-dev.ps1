[CmdletBinding()]
param(
    [string]$Version,
    [string]$PackageFeedPath,
    [string]$ToolPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "RoslynKit.Packaging.ps1")

$context = Get-RoslynKitToolingContext -ScriptPath $PSCommandPath

if ([string]::IsNullOrWhiteSpace($Version))
{
    $Version = $context.PackageVersion
}

Assert-RoslynKitPrereleaseVersion -Version $Version

$resolvedPackageFeedPath = if ([string]::IsNullOrWhiteSpace($PackageFeedPath))
{
    Prepare-RoslynKitPackageFeed -Context $context -PackageFeedPath $context.DevPackageFeedPath -Label "RoslynKit dev package feed" -Version $Version -ResetFeed
}
else
{
    Prepare-RoslynKitPackageFeed -Context $context -PackageFeedPath $PackageFeedPath -Label "RoslynKit dev package feed" -Version $Version
}

$resolvedToolPath = if ([string]::IsNullOrWhiteSpace($ToolPath))
{
    $context.DevToolPath
}
else
{
    Resolve-FullPath $ToolPath
}

$toolCommandPath = Get-RoslynKitToolCommandPath -ToolPath $resolvedToolPath
$packagePath = Get-RoslynKitPackagePath -Context $context -Version $Version -PackageFeedPath $resolvedPackageFeedPath

$command = if (Test-Path -LiteralPath $toolCommandPath)
{
    "update"
}
else
{
    "install"
}

Write-Host "dotnet executable: $($context.DotNet)"
Write-Host "SDK anchor: $($context.RepoRoot)"
Write-Host "solution path: $($context.SolutionPath)"
Write-Host "package feed: $resolvedPackageFeedPath"
Write-Host "package path: $packagePath"
Write-Host "tool path: $resolvedToolPath"
Write-Host "command path: $toolCommandPath"
Write-Host "tool scope: side-by-side --tool-path install or update"
Write-Host "requested version: $Version"
Write-Host "mode: side-by-side prerelease tool-path build, pack, and install or update"
Write-Host ""

Invoke-RoslynKitBuild -Context $context
Invoke-RoslynKitPack -Context $context -PackageFeedPath $resolvedPackageFeedPath -Version $Version
Assert-RoslynKitPackageExists -Context $context -Version $Version -PackageFeedPath $resolvedPackageFeedPath

Invoke-DotNet -Context $context -Arguments @(
    "tool"
    $command
    $context.PackageId
    "--tool-path"
    $resolvedToolPath
    "--add-source"
    $resolvedPackageFeedPath
    "--version"
    $Version
    "--ignore-failed-sources"
)

Write-Host ""
Write-Host "Dev tool command:"
Write-Host $toolCommandPath
Write-Host "This side-by-side --tool-path install is not added to PATH automatically."
Write-Host "Invoke it with the full command path above, or prepend '$resolvedToolPath' to PATH in the current shell."
Write-Host ""
Write-Host "Smoke test:"
Write-Host "& `"$toolCommandPath`" version"
Write-Host "& `"$toolCommandPath`" help"
