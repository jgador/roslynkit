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
    $packageProjectPath = Join-Path $repoRoot "src/RoslynKit/RoslynKit.csproj"
    $packageFeedPath = Join-Path $repoRoot "artifacts/packages/roslynkit"
    $devPackageFeedPath = Join-Path $repoRoot "artifacts/packages/roslynkit-dev"
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

function Get-RoslynKitGlobalToolPath
{
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

    return Join-Path (Join-Path $globalToolHome ".dotnet") "tools"
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

    return [System.IO.Path]::GetFullPath($Path)
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

    Write-Verbose "$($Context.DotNet) $($Arguments -join ' ')"
    $output = @(& $Context.DotNet @Arguments 2>&1)
    if ($LASTEXITCODE -ne 0)
    {
        throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE.`n$($output -join [Environment]::NewLine)"
    }

    $output | ForEach-Object { Write-Verbose "$_" }
}

function Test-RoslynKitCommandVersionOutput
{
    param(
        [Parameter(Mandatory = $true)]
        [string]$VersionText,
        [Parameter(Mandatory = $true)]
        [string]$ExpectedVersion
    )

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

    $versionOutput = @(& $CommandPath "--version" 2>&1)
    if ($LASTEXITCODE -ne 0)
    {
        throw "The installed roslynkit command failed with exit code $LASTEXITCODE.`n$($versionOutput -join [Environment]::NewLine)"
    }

    $versionOutput | ForEach-Object { Write-Verbose "$_" }
    $versionText = $versionOutput -join [Environment]::NewLine
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

        # Keep the isolated cache active while the caller tests or promotes the staged package.
        & $Action ([pscustomobject]@{
            CommandPath = $commandPath
            NuGetConfigPath = $nugetConfigPath
            OriginalDotNetCliHome = $previousEnvironment["DOTNET_CLI_HOME"]
        }) | Out-Null

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
