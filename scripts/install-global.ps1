# Check an isolated installation of the local package before replacing the global tool.
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "common/packaging.ps1")

$context = Get-RoslynKitToolingContext -ScriptPath $PSCommandPath
$validationRoot = Join-Path $context.RepoRoot "artifacts/global-install-validation/roslynkit"
$globalCommandPath = Get-RoslynKitGlobalToolCommandPath

Write-Host "Installing RoslynKit $($context.PackageVersion) from the local package..."
$validation = Invoke-RoslynKitPackageValidation -Context $context -ValidationRoot $validationRoot -Action {
    param($installation)

    # The staged version check passed; select the caller's tool home while keeping the isolated package cache.
    $env:DOTNET_CLI_HOME = $installation.OriginalDotNetCliHome
    if (-not [string]::IsNullOrWhiteSpace($env:DOTNET_CLI_HOME))
    {
        # Anchor relative overrides here before dotnet runs with the checkout as its working directory.
        $env:DOTNET_CLI_HOME = Resolve-FullPath $env:DOTNET_CLI_HOME
    }
    $toolList = Invoke-DotNet -Context $context -Arguments @("tool", "list", "--global", "--format", "json") -PassThru |
        ConvertFrom-Json
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
