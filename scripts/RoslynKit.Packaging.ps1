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
    $devPackageFeedPath = Join-Path $repoRoot "artifacts\packages\roslynkit-dev"
    $devToolPath = Join-Path (Join-Path (Join-Path $HOME ".roslynkit") "tools") "roslynkit-dev"
    $devToolCommandPath = Join-Path $devToolPath (Get-RoslynKitToolCommandName)

    [xml]$versionXml = Get-Content (Join-Path $repoRoot "Directory.Build.props")
    $packageVersion = $versionXml.Project.PropertyGroup.Version

    if ([string]::IsNullOrWhiteSpace($packageVersion))
    {
        throw "Directory.Build.props must define <Version> for RoslynKit packages."
    }

    if ($packageVersion.StartsWith("v", [System.StringComparison]::OrdinalIgnoreCase))
    {
        throw "Directory.Build.props <Version> must use a bare NuGet version like 0.2.0, not v0.2.0. Use the leading 'v' only for Git tags or release titles."
    }

    return [pscustomobject]@{
        RepoRoot = $repoRoot
        DotNet = $dotnet
        SolutionPath = $solutionPath
        PackageProjectPath = $packageProjectPath
        PackageFeedPath = $packageFeedPath
        DevPackageFeedPath = $devPackageFeedPath
        DevToolPath = $devToolPath
        DevToolCommandPath = $devToolCommandPath
        PackConfiguration = "Release"
        PackageId = "roslynkit"
        PackageVersion = $packageVersion
    }
}

function Get-RoslynKitPackagePath
{
    param(
        [Parameter(Mandatory = $true)]
        [pscustomobject]$Context,
        [string]$Version,
        [string]$PackageFeedPath
    )

    if ([string]::IsNullOrWhiteSpace($Version))
    {
        $Version = $Context.PackageVersion
    }

    if ([string]::IsNullOrWhiteSpace($PackageFeedPath))
    {
        $PackageFeedPath = $Context.PackageFeedPath
    }

    return Join-Path $PackageFeedPath "$($Context.PackageId).$Version.nupkg"
}

function Get-RoslynKitToolCommandName
{
    if ($IsWindows)
    {
        return "roslynkit.exe"
    }

    return "roslynkit"
}

function Get-RoslynKitToolCommandPath
{
    param(
        [Parameter(Mandatory = $true)]
        [string]$ToolPath
    )

    return Join-Path $ToolPath (Get-RoslynKitToolCommandName)
}

function Resolve-FullPath
{
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    return [System.IO.Path]::GetFullPath($Path)
}

function Test-IsPrereleaseVersion
{
    param(
        [Parameter(Mandatory = $true)]
        [string]$Version
    )

    return $Version.Contains("-", [System.StringComparison]::Ordinal)
}

function Assert-RoslynKitPrereleaseVersion
{
    param(
        [Parameter(Mandatory = $true)]
        [string]$Version
    )

    if (-not (Test-IsPrereleaseVersion -Version $Version))
    {
        throw "Version '$Version' is not a prerelease version. Use a bare stable version like 0.2.0 for global installs and a prerelease like 0.2.1-dev.1 for the side-by-side dev tool."
    }
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

    $pathComparison = if ($IsWindows) { [System.StringComparison]::OrdinalIgnoreCase } else { [System.StringComparison]::Ordinal }
    $directorySeparators = [char[]]@([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar)
    $normalizedRoot = [System.IO.Path]::GetFullPath($RootPath)
    $normalizedPath = [System.IO.Path]::GetFullPath($Path)

    $rootVolume = [System.IO.Path]::GetPathRoot($normalizedRoot)
    if ($normalizedRoot.Length -gt $rootVolume.Length)
    {
        $normalizedRoot = $normalizedRoot.TrimEnd($directorySeparators)
    }

    $pathVolume = [System.IO.Path]::GetPathRoot($normalizedPath)
    if ($normalizedPath.Length -gt $pathVolume.Length)
    {
        $normalizedPath = $normalizedPath.TrimEnd($directorySeparators)
    }

    if ($normalizedPath.Equals($normalizedRoot, $pathComparison))
    {
        throw "$Label cannot target the protected root path $normalizedRoot."
    }

    $rootBoundary = if ($normalizedRoot.EndsWith([System.IO.Path]::DirectorySeparatorChar.ToString(), [System.StringComparison]::Ordinal) -or
        $normalizedRoot.EndsWith([System.IO.Path]::AltDirectorySeparatorChar.ToString(), [System.StringComparison]::Ordinal))
    {
        $normalizedRoot
    }
    else
    {
        $normalizedRoot + [System.IO.Path]::DirectorySeparatorChar
    }

    if (-not $normalizedPath.StartsWith($rootBoundary, $pathComparison))
    {
        throw "$Label must stay under $normalizedRoot, but resolved to $normalizedPath."
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

function Prepare-RoslynKitPackageFeed
{
    param(
        [Parameter(Mandatory = $true)]
        [pscustomobject]$Context,
        [Parameter(Mandatory = $true)]
        [string]$PackageFeedPath,
        [Parameter(Mandatory = $true)]
        [string]$Label,
        [string]$Version,
        [switch]$ResetFeed
    )

    $resolvedPackageFeedPath = Resolve-FullPath $PackageFeedPath

    if ($ResetFeed)
    {
        Reset-Directory -Path $resolvedPackageFeedPath -RootPath $Context.RepoRoot -Label $Label
        return $resolvedPackageFeedPath
    }

    if (Test-Path -LiteralPath $resolvedPackageFeedPath -PathType Leaf)
    {
        throw "$Label must resolve to a directory path, but '$resolvedPackageFeedPath' is a file."
    }

    New-Item -ItemType Directory -Path $resolvedPackageFeedPath -Force | Out-Null

    if (-not [string]::IsNullOrWhiteSpace($Version))
    {
        $packagePath = Get-RoslynKitPackagePath -Context $Context -Version $Version -PackageFeedPath $resolvedPackageFeedPath
        if (Test-Path -LiteralPath $packagePath)
        {
            Remove-Item -LiteralPath $packagePath -Force
        }
    }

    return $resolvedPackageFeedPath
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

function Invoke-RoslynKitBuild
{
    param(
        [Parameter(Mandatory = $true)]
        [pscustomobject]$Context
    )

    Invoke-DotNet -Context $Context -Arguments @(
        "build"
        $Context.SolutionPath
        "-c"
        $Context.PackConfiguration
        "--tl:off"
        "--nologo"
        "-clp:ErrorsOnly;NoSummary"
    )
}

function Invoke-RoslynKitPack
{
    param(
        [Parameter(Mandatory = $true)]
        [pscustomobject]$Context,
        [Parameter(Mandatory = $true)]
        [string]$PackageFeedPath,
        [string]$Version
    )

    $arguments = @(
        "pack"
        $Context.PackageProjectPath
        "-c"
        $Context.PackConfiguration
        "--tl:off"
        "--nologo"
        "-clp:ErrorsOnly;NoSummary"
        "-o"
        $PackageFeedPath
    )

    if (-not [string]::IsNullOrWhiteSpace($Version))
    {
        $arguments += "/p:Version=$Version"
    }

    Invoke-DotNet -Context $Context -Arguments $arguments
}

function Assert-RoslynKitPackageExists
{
    param(
        [Parameter(Mandatory = $true)]
        [pscustomobject]$Context,
        [string]$Version,
        [string]$PackageFeedPath
    )

    $packagePath = Get-RoslynKitPackagePath -Context $Context -Version $Version -PackageFeedPath $PackageFeedPath
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
    Write-Host "Global install commands:"
    Write-Host "dotnet tool install --global $($Context.PackageId) --add-source `"$($Context.PackageFeedPath)`" --version $($Context.PackageVersion) --ignore-failed-sources"
    Write-Host "dotnet tool update --global $($Context.PackageId) --add-source `"$($Context.PackageFeedPath)`" --version $($Context.PackageVersion) --ignore-failed-sources"
    Write-Host "roslynkit version"

    Write-Host ""
    Write-Host "Side-by-side dev install:"
    $devVersionExample = if (Test-IsPrereleaseVersion -Version $Context.PackageVersion)
    {
        $Context.PackageVersion
    }
    else
    {
        "$($Context.PackageVersion)-dev.1"
    }

    Write-Host "pwsh ./scripts/install-roslynkit-dev.ps1 -Version $devVersionExample"
    Write-Host "The dev installer builds, packs, and installs the requested prerelease from the current checkout."
    Write-Host "& `"$($Context.DevToolCommandPath)`" version"
    Write-Host "& `"$($Context.DevToolCommandPath)`" help"
}
