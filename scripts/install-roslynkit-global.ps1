[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "RoslynKit.Packaging.ps1")

$context = Get-RoslynKitToolingContext -ScriptPath $PSCommandPath
$packagePath = Get-RoslynKitPackagePath -Context $context
$validationRoot = Join-Path $context.RepoRoot "artifacts\global-install-validation\roslynkit"
$stagingToolPath = Join-Path $validationRoot "tool"
$nugetPackagesPath = Join-Path $validationRoot "nuget-packages"
$dotnetCliHome = Join-Path $validationRoot "dotnet-cli-home"
$nugetConfigPath = Join-Path $validationRoot "NuGet.Config"
$globalCommandPath = Get-RoslynKitGlobalToolCommandPath

Assert-RoslynKitPackageExists -Context $context
Reset-Directory `
    -Path $validationRoot `
    -RootPath $context.RepoRoot `
    -Label "RoslynKit global install validation root"

New-Item -ItemType Directory -Path $nugetPackagesPath -Force | Out-Null
New-Item -ItemType Directory -Path $dotnetCliHome -Force | Out-Null
Write-RoslynKitLocalNuGetConfig `
    -PackageFeedPath $context.PackageFeedPath `
    -ConfigPath $nugetConfigPath

$packageHashBefore = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash
$previousNugetPackages = $env:NUGET_PACKAGES
$previousDotNetCliHome = $env:DOTNET_CLI_HOME
$previousTelemetryOptOut = $env:DOTNET_CLI_TELEMETRY_OPTOUT
$previousNoLogo = $env:DOTNET_NOLOGO
$previousSkipFirstTimeExperience = $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE

try
{
    $env:NUGET_PACKAGES = $nugetPackagesPath
    $env:DOTNET_CLI_HOME = $dotnetCliHome
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
    $env:DOTNET_NOLOGO = "1"
    $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "1"

    Invoke-DotNet -Context $context -Arguments @(
        "tool"
        "install"
        $context.PackageId
        "--tool-path"
        $stagingToolPath
        "--configfile"
        $nugetConfigPath
        "--version"
        $context.PackageVersion
        "--ignore-failed-sources"
    )

    $stagingCommandPath = Get-RoslynKitToolCommandPath -ToolPath $stagingToolPath
    Assert-RoslynKitCommandVersion `
        -CommandPath $stagingCommandPath `
        -ExpectedVersion $context.PackageVersion

    $env:DOTNET_CLI_HOME = $previousDotNetCliHome
    $toolListOutput = @(& $context.DotNet "tool" "list" "--global" "--format" "json" 2>&1)
    if ($LASTEXITCODE -ne 0)
    {
        throw "Unable to list global .NET tools. Exit code: $LASTEXITCODE. Output: $($toolListOutput -join [Environment]::NewLine)"
    }

    $toolList = ($toolListOutput -join [Environment]::NewLine) | ConvertFrom-Json
    $installedTool = @($toolList.data) |
        Where-Object packageId -EQ $context.PackageId |
        Select-Object -First 1
    $previousVersion = if ($null -eq $installedTool) { "not installed" } else { $installedTool.version }
    $installationResult = if ($null -eq $installedTool) { "installed" } else { "replaced" }

    if ($null -ne $installedTool)
    {
        Write-Host "Removing global RoslynKit $($installedTool.version) before installing the exact local package."
        Invoke-DotNet -Context $context -Arguments @(
            "tool"
            "uninstall"
            "--global"
            $context.PackageId
        )
    }

    Invoke-DotNet -Context $context -Arguments @(
        "tool"
        "install"
        "--global"
        $context.PackageId
        "--configfile"
        $nugetConfigPath
        "--version"
        $context.PackageVersion
        "--ignore-failed-sources"
    )

    Assert-RoslynKitCommandVersion `
        -CommandPath $globalCommandPath `
        -ExpectedVersion $context.PackageVersion
}
finally
{
    $env:NUGET_PACKAGES = $previousNugetPackages
    $env:DOTNET_CLI_HOME = $previousDotNetCliHome
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = $previousTelemetryOptOut
    $env:DOTNET_NOLOGO = $previousNoLogo
    $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = $previousSkipFirstTimeExperience
}

$packageHashAfter = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash
if ($packageHashAfter -ne $packageHashBefore)
{
    throw "The package changed during global installation: $packagePath"
}

Write-Host ""
Write-Host "RoslynKit $($context.PackageVersion) global installation passed."
Write-Host "Global installation: $installationResult"
Write-Host "Previous global version: $previousVersion"
Write-Host "Installed command: $globalCommandPath"
Write-Host "Package: $packagePath"
Write-Host "SHA-256: $packageHashAfter"
