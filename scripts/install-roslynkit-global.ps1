[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "RoslynKit.Packaging.ps1")

$context = Get-RoslynKitToolingContext -ScriptPath $PSCommandPath
$validationRoot = Join-Path $context.RepoRoot "artifacts/global-install-validation/roslynkit"
$globalCommandPath = Get-RoslynKitGlobalToolCommandPath

Write-Host "Installing RoslynKit $($context.PackageVersion) from the local package..."
$validation = Invoke-RoslynKitPackageValidation -Context $context -ValidationRoot $validationRoot -Action {
    param($installation)

    $env:DOTNET_CLI_HOME = $installation.OriginalDotNetCliHome
    $toolListOutput = @(& $context.DotNet "tool" "list" "--global" "--format" "json" 2>&1)
    if ($LASTEXITCODE -ne 0)
    {
        throw "Unable to list global .NET tools. Exit code: $LASTEXITCODE. Output: $($toolListOutput -join [Environment]::NewLine)"
    }

    $toolList = ($toolListOutput -join [Environment]::NewLine) | ConvertFrom-Json
    $installedTool = @($toolList.data) |
        Where-Object packageId -EQ $context.PackageId |
        Select-Object -First 1

    if ($null -ne $installedTool)
    {
        Write-Host "Replacing global RoslynKit $($installedTool.version); a failed install after uninstall may leave the command unavailable."
        Invoke-DotNet -Context $context -Arguments @("tool", "uninstall", "--global", $context.PackageId)
    }

    Invoke-DotNet -Context $context -Arguments @(
        "tool", "install", "--global", $context.PackageId,
        "--configfile", $installation.NuGetConfigPath,
        "--version", $context.PackageVersion,
        "--ignore-failed-sources"
    )
    Assert-RoslynKitCommandVersion -CommandPath $globalCommandPath -ExpectedVersion $context.PackageVersion
}

Write-Host "RoslynKit $($context.PackageVersion) global installation passed."
Write-Host "Installed command: $globalCommandPath"
Write-Host "Package: $($validation.PackagePath)"
Write-Host "SHA-256: $($validation.PackageHash)"
