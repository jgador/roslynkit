Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Get-RoslynKitToolingContext
{
    param(
        [Parameter(Mandatory = $true)]
        [string]$ScriptPath
    )

    $scriptsRoot = Split-Path -Parent $ScriptPath
    $repoRoot = (Resolve-Path (Join-Path $scriptsRoot "..")).Path
    $dotnet = (Get-Command dotnet -ErrorAction Stop).Source
    $solutionPath = Join-Path $repoRoot "RoslynKit.slnx"
    $packageProjectPath = Join-Path $repoRoot "src\RoslynKit\RoslynKit.csproj"
    $packageFeedPath = Join-Path $repoRoot "artifacts\packages\roslynkit"

    [xml]$versionXml = Get-Content (Join-Path $repoRoot "Directory.Build.props")
    $packageVersion = $versionXml.Project.PropertyGroup.Version

    if ([string]::IsNullOrWhiteSpace($packageVersion))
    {
        throw "Directory.Build.props must define <Version> for RoslynKit packages."
    }

    if ($packageVersion.StartsWith("v", [System.StringComparison]::OrdinalIgnoreCase))
    {
        throw "Directory.Build.props <Version> must use a bare NuGet version like 0.1.0, not v0.1.0. Use the leading 'v' only for Git tags or release titles."
    }

    return [pscustomobject]@{
        RepoRoot = $repoRoot
        DotNet = $dotnet
        SolutionPath = $solutionPath
        PackageProjectPath = $packageProjectPath
        PackageFeedPath = $packageFeedPath
        PackConfiguration = "Release"
        PackageId = "roslynkit"
        PackageVersion = $packageVersion
    }
}

function Get-RoslynKitPackagePath
{
    param(
        [Parameter(Mandatory = $true)]
        [pscustomobject]$Context
    )

    return Join-Path $Context.PackageFeedPath "$($Context.PackageId).$($Context.PackageVersion).nupkg"
}

function Assert-PathUnderRoot
{
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,
        [Parameter(Mandatory = $true)]
        [string]$RootPath,
        [Parameter(Mandatory = $true)]
        [string]$Label
    )

    $normalizedRoot = [System.IO.Path]::GetFullPath($RootPath).TrimEnd("\", "/")
    $normalizedPath = [System.IO.Path]::GetFullPath($Path)

    if (-not $normalizedPath.StartsWith($normalizedRoot, [System.StringComparison]::OrdinalIgnoreCase))
    {
        throw "$Label must stay under $normalizedRoot, but resolved to $normalizedPath."
    }

    if ($normalizedPath.Equals($normalizedRoot, [System.StringComparison]::OrdinalIgnoreCase))
    {
        throw "$Label cannot target the protected root path $normalizedRoot."
    }
}

function Reset-Directory
{
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,
        [Parameter(Mandatory = $true)]
        [string]$RootPath,
        [Parameter(Mandatory = $true)]
        [string]$Label
    )

    Assert-PathUnderRoot -Path $Path -RootPath $RootPath -Label $Label

    if (Test-Path -LiteralPath $Path)
    {
        Remove-Item -LiteralPath $Path -Recurse -Force
    }

    New-Item -ItemType Directory -Path $Path -Force | Out-Null
}

function Invoke-DotNet
{
    param(
        [Parameter(Mandatory = $true)]
        [pscustomobject]$Context,
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments
    )

    Write-Host "$($Context.DotNet) $($Arguments -join ' ')"
    & $Context.DotNet @Arguments

    if ($LASTEXITCODE -ne 0)
    {
        throw "dotnet $($Arguments[0]) failed with exit code $LASTEXITCODE."
    }
}

function Assert-RoslynKitPackageExists
{
    param(
        [Parameter(Mandatory = $true)]
        [pscustomobject]$Context
    )

    $packagePath = Get-RoslynKitPackagePath -Context $Context
    if (-not (Test-Path -LiteralPath $packagePath))
    {
        throw "Expected RoslynKit package was not produced: $packagePath"
    }
}

function Show-RoslynKitDogfoodCommands
{
    param(
        [Parameter(Mandatory = $true)]
        [pscustomobject]$Context
    )

    $packagePath = Get-RoslynKitPackagePath -Context $Context

    Write-Host ""
    Write-Host "Local folder-feed package:"
    Write-Host $packagePath
    Write-Host ""
    Write-Host "Dogfood install commands:"
    Write-Host "dotnet tool install --global $($Context.PackageId) --add-source `"$($Context.PackageFeedPath)`" --version $($Context.PackageVersion) --ignore-failed-sources"
    Write-Host "dotnet tool update --global $($Context.PackageId) --add-source `"$($Context.PackageFeedPath)`" --version $($Context.PackageVersion) --ignore-failed-sources"
    Write-Host "roslynkit help"
}
