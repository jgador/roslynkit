Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "RoslynKit.Packaging.ps1")

$context = Get-RoslynKitToolingContext -ScriptPath $PSCommandPath
$packagePath = Get-RoslynKitPackagePath -Context $context
$validationRoot = Join-Path $context.RepoRoot "artifacts/package-validation/roslynkit"
$toolPath = Join-Path $validationRoot "tool"
$nugetPackagesPath = Join-Path $validationRoot "nuget-packages"
$dotnetCliHome = Join-Path $validationRoot "dotnet-cli-home"
$nugetConfigPath = Join-Path $validationRoot "NuGet.Config"
$commandValidationRoot = Join-Path $validationRoot "command-smoke"

Assert-RoslynKitPackageExists -Context $context
Reset-Directory `
    -Path $validationRoot `
    -RootPath $context.RepoRoot `
    -Label "RoslynKit package validation root"

New-Item -ItemType Directory -Path $nugetPackagesPath -Force | Out-Null
New-Item -ItemType Directory -Path $dotnetCliHome -Force | Out-Null

Write-RoslynKitLocalNuGetConfig `
    -PackageFeedPath $context.PackageFeedPath `
    -ConfigPath $nugetConfigPath

$packageHashBefore = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash
$toolCommandPath = Get-RoslynKitToolCommandPath -ToolPath $toolPath
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
        $toolPath
        "--configfile"
        $nugetConfigPath
        "--version"
        $context.PackageVersion
        "--ignore-failed-sources"
    )

    Assert-RoslynKitCommandVersion `
        -CommandPath $toolCommandPath `
        -ExpectedVersion $context.PackageVersion

    & (Join-Path $PSScriptRoot "test-roslynkit-commands.ps1") `
        -CommandPath $toolCommandPath `
        -ExpectedVersion $context.PackageVersion `
        -ValidationRoot $commandValidationRoot

    $packageHashAfter = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash
    if ($packageHashAfter -ne $packageHashBefore)
    {
        throw "The package changed during smoke testing: $packagePath"
    }
}
finally
{
    $env:NUGET_PACKAGES = $previousNugetPackages
    $env:DOTNET_CLI_HOME = $previousDotNetCliHome
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = $previousTelemetryOptOut
    $env:DOTNET_NOLOGO = $previousNoLogo
    $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = $previousSkipFirstTimeExperience
}

Write-Host ""
Write-Host "RoslynKit $($context.PackageVersion) installed-package smoke test passed."
Write-Host "Installed command: $toolCommandPath"
Write-Host "Package: $packagePath"
Write-Host "SHA-256: $packageHashAfter"
