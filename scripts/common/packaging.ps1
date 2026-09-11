# Shared path, build, and isolated-install helpers for the scripts in the parent directory.
Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "process.ps1")

function Get-RoslynKitToolingContext
{
    param(
        [Parameter(Mandatory = $true)]
        [string]$ScriptPath
    )

    # Anchor paths to the calling script in scripts/, regardless of the current directory or this helper's location.
    $scriptsRoot = Split-Path -Parent $ScriptPath
    $repoRoot = (Resolve-Path -LiteralPath (Join-Path $scriptsRoot "..")).Path
    $dotnet = (Get-Command dotnet -ErrorAction Stop).Source
    $solutionPath = Join-Path $repoRoot "RoslynKit.slnx"
    $packageProjectPath = Join-Path $repoRoot "src/RoslynKit/RoslynKit.csproj"
    $packageFeedPath = Join-Path $repoRoot "artifacts/packages/roslynkit"
    $devPackageFeedPath = Join-Path $repoRoot "artifacts/packages/roslynkit-dev"
    $devToolPath = Join-Path $HOME ".roslynkit/tools/roslynkit-dev"

    [xml]$versionXml = Get-Content -LiteralPath (Join-Path $repoRoot "Directory.Build.props") -Raw
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

function Get-RoslynKitGlobalToolPath
{
    # Match dotnet's global tool home, including an explicit DOTNET_CLI_HOME override.
    $globalToolHome = if ([string]::IsNullOrWhiteSpace($env:DOTNET_CLI_HOME))
    {
        [Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)
    }
    else
    {
        Resolve-FullPath $env:DOTNET_CLI_HOME
    }

    if ([string]::IsNullOrWhiteSpace($globalToolHome))
    {
        throw "Unable to resolve the home directory for the global .NET tool path."
    }

    return Join-Path $globalToolHome ".dotnet/tools"
}

function Get-RoslynKitGlobalToolCommandPath
{
    return Get-RoslynKitToolCommandPath -ToolPath (Get-RoslynKitGlobalToolPath)
}

function Resolve-FullPath
{
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    # Resolve from Set-Location even when the target does not exist yet; .NET's current directory can lag behind.
    return $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Path)
}

function Write-RoslynKitLocalNuGetConfig
{
    param(
        [Parameter(Mandatory = $true)]
        [string]$PackageFeedPath,
        [Parameter(Mandatory = $true)]
        [string]$ConfigPath
    )

    $configDirectory = Split-Path -Parent $ConfigPath
    New-Item -ItemType Directory -Path $configDirectory -Force | Out-Null

    # Escape feed paths containing characters such as '&'; clear inherited feeds to select only local packages.
    $escapedPackageFeedPath = [System.Security.SecurityElement]::Escape($PackageFeedPath)
    $nugetConfig = @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="roslynkit-local" value="$escapedPackageFeedPath" />
  </packageSources>
</configuration>
"@
    Set-Content -LiteralPath $ConfigPath -Value $nugetConfig -Encoding utf8NoBOM
}

function Assert-RoslynKitPrereleaseVersion
{
    param(
        [Parameter(Mandatory = $true)]
        [string]$Version
    )

    if (-not $Version.Contains("-", [System.StringComparison]::Ordinal))
    {
        throw "Version '$Version' is not a prerelease version. Use a bare stable version like 0.2.0 for global installs and a prerelease like 0.2.1-dev.1 for the side-by-side dev tool."
    }
}

function Assert-PathUnderRoot
{
    # Check both directory boundaries and link targets before recursive cleanup.
    # The target may not exist yet, but its existing parents must still stay inside the root.
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
    $normalizedRoot = Resolve-FullPath $RootPath
    $normalizedPath = Resolve-FullPath $Path

    # Normalize trailing separators without stripping the separator from a filesystem root.
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

    # Include a directory separator so a sibling such as 'artifacts-old' cannot match 'artifacts'.
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

    # Check existing ancestors too: a missing final directory may sit below a link that escapes the root.
    $relativePath = $normalizedPath.Substring($rootBoundary.Length)
    $currentPath = $normalizedRoot
    foreach ($pathSegment in ($relativePath -split '[\\/]'))
    {
        if ([string]::IsNullOrEmpty($pathSegment))
        {
            continue
        }

        $currentPath = Join-Path $currentPath $pathSegment
        try
        {
            $pathItem = Get-Item -LiteralPath $currentPath -Force -ErrorAction Stop
        }
        catch [System.Management.Automation.ItemNotFoundException]
        {
            break
        }

        if (($pathItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -eq 0)
        {
            continue
        }

        try
        {
            $resolvedTarget = $pathItem.ResolveLinkTarget($true)
        }
        catch
        {
            throw "$Label cannot traverse reparse point '$currentPath' under the protected root $normalizedRoot."
        }

        if ($null -eq $resolvedTarget)
        {
            throw "$Label cannot traverse reparse point '$currentPath' under the protected root $normalizedRoot."
        }

        $resolvedTargetPath = [System.IO.Path]::GetFullPath($resolvedTarget.FullName)
        if (-not $resolvedTargetPath.Equals($normalizedRoot, $pathComparison) -and
            -not $resolvedTargetPath.StartsWith($rootBoundary, $pathComparison))
        {
            throw "$Label must stay under $normalizedRoot, but reparse point '$currentPath' resolves to $resolvedTargetPath."
        }
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

    # Validate before either deletion or creation, since both operations can follow directory links.
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
        # Clearing the whole feed is allowed only below this checkout.
        Reset-Directory -Path $resolvedPackageFeedPath -RootPath $Context.RepoRoot -Label $Label
        return $resolvedPackageFeedPath
    }

    if (Test-Path -LiteralPath $resolvedPackageFeedPath -PathType Leaf)
    {
        throw "$Label must resolve to a directory path, but '$resolvedPackageFeedPath' is a file."
    }

    New-Item -ItemType Directory -Path $resolvedPackageFeedPath -Force | Out-Null

    # Without a full reset, remove only the requested version before packing its replacement.
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
        [string[]]$Arguments,
        [switch]$PassThru
    )

    # Run from the checkout to use the .NET Software Development Kit (SDK) selected by global.json.
    $result = Invoke-CapturedProcess -FilePath $Context.DotNet -Arguments $Arguments -WorkingDirectory $Context.RepoRoot
    Assert-ProcessSucceeded -Description "dotnet $($Arguments -join ' ')" -Result $result

    if ($PassThru)
    {
        # Return only standard output so warnings cannot corrupt parsed JSON.
        return $result.StandardOutput
    }
}

function Test-RoslynKitCommandVersionOutput
{
    param(
        [Parameter(Mandatory = $true)]
        [string]$VersionText,
        [Parameter(Mandatory = $true)]
        [string]$ExpectedVersion
    )

    # The executable can append build metadata (+...) to the exact NuGet package version.
    $escapedExpectedVersion = [Regex]::Escape($ExpectedVersion)
    $versionPattern = "\Aroslynkit version $escapedExpectedVersion(?:\+[0-9A-Za-z.-]+)?\z"
    return [Regex]::IsMatch(
        $VersionText.Trim(),
        $versionPattern,
        [System.Text.RegularExpressions.RegexOptions]::CultureInvariant)
}

function Assert-RoslynKitCommandVersion
{
    param(
        [Parameter(Mandatory = $true)]
        [string]$CommandPath,
        [Parameter(Mandatory = $true)]
        [string]$ExpectedVersion
    )

    if (-not (Test-Path -LiteralPath $CommandPath -PathType Leaf))
    {
        throw "Expected installed RoslynKit command was not found: $CommandPath"
    }

    $result = Invoke-CapturedProcess -FilePath $CommandPath -Arguments @("--version")
    Assert-ProcessSucceeded -Description "The installed roslynkit command" -Result $result
    $versionText = $result.StandardOutput
    if (-not (Test-RoslynKitCommandVersionOutput -VersionText $versionText -ExpectedVersion $ExpectedVersion))
    {
        throw "Expected RoslynKit $ExpectedVersion, but received: $versionText"
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

function Invoke-RoslynKitPackageValidation
{
    # Install the exact local package, run the caller's checks, then verify its bytes are unchanged.
    # Restore the caller's environment even if installation or checks fail.
    param(
        [Parameter(Mandatory = $true)]
        [pscustomobject]$Context,
        [Parameter(Mandatory = $true)]
        [string]$ValidationRoot,
        [Parameter(Mandatory = $true)]
        [scriptblock]$Action
    )

    Assert-RoslynKitPackageExists -Context $Context
    $packagePath = Get-RoslynKitPackagePath -Context $Context
    $packageHash = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash
    Reset-Directory -Path $ValidationRoot -RootPath $Context.RepoRoot -Label "RoslynKit package validation root"

    $toolPath = Join-Path $ValidationRoot "tool"
    $nugetConfigPath = Join-Path $ValidationRoot "NuGet.Config"
    Write-RoslynKitLocalNuGetConfig -PackageFeedPath $Context.PackageFeedPath -ConfigPath $nugetConfigPath

    # Fresh caches prevent a previously installed copy of the same version from satisfying this check.
    $environment = @{
        NUGET_PACKAGES = (Join-Path $ValidationRoot "nuget-packages")
        DOTNET_CLI_HOME = (Join-Path $ValidationRoot "dotnet-cli-home")
        DOTNET_CLI_TELEMETRY_OPTOUT = "1"
        DOTNET_NOLOGO = "1"
        DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "1"
    }
    $previousEnvironment = @{}
    foreach ($name in $environment.Keys)
    {
        $previousEnvironment[$name] = [Environment]::GetEnvironmentVariable($name)
    }

    try
    {
        foreach ($name in $environment.Keys)
        {
            [Environment]::SetEnvironmentVariable($name, $environment[$name])
        }
        New-Item -ItemType Directory -Path $env:NUGET_PACKAGES, $env:DOTNET_CLI_HOME -Force | Out-Null

        Write-Verbose "Stage-installing $packagePath into $toolPath"
        Invoke-DotNet -Context $Context -Arguments @(
            "tool", "install", $Context.PackageId,
            "--tool-path", $toolPath,
            "--configfile", $nugetConfigPath,
            "--version", $Context.PackageVersion,
            "--ignore-failed-sources"
        )

        $commandPath = Get-RoslynKitToolCommandPath -ToolPath $toolPath
        Assert-RoslynKitCommandVersion -CommandPath $commandPath -ExpectedVersion $Context.PackageVersion

        # Keep the isolated cache active while the caller tests or globally installs the package.
        # Discard callback output so this helper returns only the validated package details.
        & $Action ([pscustomobject]@{
            CommandPath = $commandPath
            NuGetConfigPath = $nugetConfigPath
            OriginalDotNetCliHome = $previousEnvironment["DOTNET_CLI_HOME"]
        }) | Out-Null

        # Compare against the starting hash before returning the validated package details.
        if ((Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash -ne $packageHash)
        {
            throw "The package changed during validation: $packagePath"
        }

        return [pscustomobject]@{
            PackagePath = $packagePath
            PackageHash = $packageHash
            CommandPath = $commandPath
        }
    }
    finally
    {
        # Restore absent variables as absent; an empty value can change dotnet's home/cache defaults.
        foreach ($name in $previousEnvironment.Keys)
        {
            if ($null -eq $previousEnvironment[$name])
            {
                if (Test-Path -LiteralPath "Env:$name")
                {
                    Remove-Item -LiteralPath "Env:$name"
                }
            }
            else
            {
                [Environment]::SetEnvironmentVariable($name, $previousEnvironment[$name])
            }
        }
    }
}
