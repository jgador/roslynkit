Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "RoslynKit.Tooling.ps1")

$context = Get-RoslynKitToolingContext -ScriptPath $PSCommandPath

Write-Host "dotnet executable: $($context.DotNet)"
Write-Host "SDK anchor: $($context.RepoRoot)"
Write-Host "solution path: $($context.SolutionPath)"
Write-Host "tool scope: global install from local folder feed"
Write-Host ""

Reset-Directory -Path $context.PackageFeedPath -RootPath $context.RepoRoot -Label "RoslynKit package feed"

Invoke-DotNet -Context $context -Arguments @(
    "pack"
    $context.PackageProjectPath
    "-c"
    $context.PackConfiguration
    "--tl:off"
    "--nologo"
    "-clp:ErrorsOnly;NoSummary"
    "-o"
    $context.PackageFeedPath
)

Assert-RoslynKitPackageExists -Context $context
Show-RoslynKitDogfoodCommands -Context $context
