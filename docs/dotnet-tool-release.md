# RoslynKit Dotnet Tool Packaging

Run every command from the repo root.

## What gets produced

RoslynKit currently produces one .NET tool package:

- `roslynkit`

The release version comes from `Directory.Build.props`. The public package metadata lives in `src/RoslynKit/RoslynKit.csproj`, and the NuGet package readme lives in [src/RoslynKit/PackageReadme.md](../src/RoslynKit/PackageReadme.md).

## 1. Update package metadata

1. Set the new `<Version>` in `Directory.Build.props` using a bare NuGet version such as `0.2.0` or a prerelease such as `0.2.0-dev.1`. Use the leading `v` only for Git tags or release titles such as `v0.2.0`.
2. Confirm `src/RoslynKit/RoslynKit.csproj` still has the correct public package metadata: `PackageId` is `roslynkit`, `ToolCommandName` is `roslynkit`, and the repository URL, license, tags, and package readme values are still correct.
3. If the public CLI surface, repo-local skill workflow, or install story changed, update [README.md](../README.md), [docs/agents/skill-maintenance.md](agents/skill-maintenance.md), and [docs/dev-install.md](dev-install.md) in the same change when applicable.

## 2. Validate the repo before packing

Run the standard validation lane first:

```powershell
dotnet restore .\RoslynKit.slnx
dotnet build .\RoslynKit.slnx --tl:off --nologo "-clp:ErrorsOnly;NoSummary"
dotnet test .\RoslynKit.slnx
```

## 3. Build the local folder feed

Use the helper script:

```powershell
pwsh .\scripts\prepare-roslynkit-package.ps1
```

That script:

1. Resolves the repo root and `dotnet` executable.
2. Reads and validates `<Version>` from `Directory.Build.props`.
3. Recreates the local folder feed at `.\artifacts\packages\roslynkit`.
4. Packs `src\RoslynKit\RoslynKit.csproj` in `Release` into that folder feed.
5. Verifies that `roslynkit.<version>.nupkg` exists.
6. Prints the exact global install commands for the packed version and, when the packed version is prerelease, the side-by-side dev install command.

If you want the raw command instead of the helper script, this is the equivalent pack step:

```powershell
dotnet pack .\src\RoslynKit\RoslynKit.csproj -c Release -o .\artifacts\packages\roslynkit
```

## 4. Install or update the stable global tool

Install from the local folder feed into the standard global tool location such as `%USERPROFILE%\.dotnet\tools`:

```powershell
dotnet tool install --global roslynkit --add-source .\artifacts\packages\roslynkit --version <version> --ignore-failed-sources
roslynkit version
```

If `roslynkit` is already installed globally, update it in place:

```powershell
dotnet tool update --global roslynkit --add-source .\artifacts\packages\roslynkit --version <version> --ignore-failed-sources
roslynkit version
```

## 5. Manually smoke-test the stable global tool

The following test installs or updates the freshly packed release in the standard global .NET tool folder, which is `%USERPROFILE%\.dotnet\tools` on Windows. It then uses [RoslynKit.slnx](../RoslynKit.slnx) as a real target for indexing, search, daemon lifecycle checks, and representative navigation commands.

Run the blocks from the repository root in a committed Git worktree. The test database stays under the Git-ignored `artifacts\release-smoke` folder. There is no public daemon start command; the first daemon-eligible workspace command starts it on demand.

### 5.1 Pack and install the release globally

Paste this entire block into PowerShell. It reads the release version from [Directory.Build.props](../Directory.Build.props), recreates the local package feed, chooses `dotnet tool install` or `dotnet tool update`, adds the global tool folder to the current terminal's `PATH`, and verifies the installed version.

```powershell
Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path ".").Path
[xml]$versionXml = Get-Content (Join-Path $repoRoot "Directory.Build.props")
$version = [string]$versionXml.Project.PropertyGroup.Version
$packageFeed = Join-Path $repoRoot "artifacts\packages\roslynkit"

pwsh (Join-Path $repoRoot "scripts\prepare-roslynkit-package.ps1")
if ($LASTEXITCODE -ne 0)
{
    throw "Package preparation failed with exit code $LASTEXITCODE."
}

$packagePath = Join-Path $packageFeed "roslynkit.$version.nupkg"
if (-not (Test-Path -LiteralPath $packagePath -PathType Leaf))
{
    throw "Expected package was not found: $packagePath"
}

$toolListJson = & dotnet tool list --global --format json
if ($LASTEXITCODE -ne 0)
{
    throw "Unable to list global .NET tools."
}

$installedTool = @(($toolListJson | ConvertFrom-Json).data) |
    Where-Object packageId -EQ "roslynkit" |
    Select-Object -First 1
$toolAction = if ($null -eq $installedTool) { "install" } else { "update" }

& dotnet tool $toolAction --global roslynkit `
    --add-source $packageFeed `
    --version $version `
    --ignore-failed-sources
if ($LASTEXITCODE -ne 0)
{
    throw "Global RoslynKit $toolAction failed with exit code $LASTEXITCODE."
}

$globalToolFolder = Join-Path ([Environment]::GetFolderPath("UserProfile")) ".dotnet\tools"
$pathSeparator = [IO.Path]::PathSeparator
$env:PATH = "$globalToolFolder$pathSeparator$env:PATH"
$commandPath = (Get-Command roslynkit -ErrorAction Stop).Source
$versionOutput = roslynkit --version
if ($LASTEXITCODE -ne 0)
{
    throw "The installed roslynkit command failed."
}

if (-not ($versionOutput -match "roslynkit version $([Regex]::Escape($version))"))
{
    throw "Expected RoslynKit $version, but received: $versionOutput"
}

Write-Host "Installed command: $commandPath"
$versionOutput
```

The final path should resolve inside the global `.dotnet\tools` folder, and the version output should contain the version from [Directory.Build.props](../Directory.Build.props). Informational build metadata after the version is expected.

### 5.2 Run the end-to-end command test

Paste this block into the same terminal or a new PowerShell terminal from the repository root. Every product check invokes the `roslynkit` command directly. The native-command preference makes PowerShell stop when a command returns a nonzero exit code, and the daemon wait helper stops when the expected state is not reached.

```powershell
Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$PSNativeCommandUseErrorActionPreference = $true

$repoRoot = (Resolve-Path ".").Path
[xml]$versionXml = Get-Content (Join-Path $repoRoot "Directory.Build.props")
$version = [string]$versionXml.Project.PropertyGroup.Version
$target = Join-Path $repoRoot "RoslynKit.slnx"
$indexFolder = Join-Path $repoRoot "artifacts\release-smoke"
$indexPath = Join-Path $indexFolder "roslynkit.db"
$globalToolFolder = Join-Path ([Environment]::GetFolderPath("UserProfile")) ".dotnet\tools"
$pathSeparator = [IO.Path]::PathSeparator
$env:PATH = "$globalToolFolder$pathSeparator$env:PATH"

New-Item -ItemType Directory -Path $indexFolder -Force | Out-Null

function Wait-RoslynKitDaemonState
{
    param(
        [Parameter(Mandatory = $true)]
        [string]$Target,
        [Parameter(Mandatory = $true)]
        [string]$State
    )

    for ($attempt = 1; $attempt -le 120; $attempt++)
    {
        $status = roslynkit daemon status --target $Target
        if ($LASTEXITCODE -ne 0)
        {
            throw "Unable to read RoslynKit daemon status."
        }

        if (($status -join "`n") -match "(?m)^state: $([Regex]::Escape($State))$")
        {
            $status
            return
        }

        Start-Sleep -Milliseconds 250
    }

    throw "The RoslynKit daemon did not reach state '$State'."
}

# Confirm the globally installed command and its local help surface.
roslynkit --version
roslynkit help

# Build a clean search index and run an English-oriented declaration search.
roslynkit index --target $target --index-path $indexPath --rebuild
roslynkit search --target $target --index-path $indexPath `
    --query "how does workspace daemon reload after source changes" `
    --max-results 5

# Reset any daemon started by index or search, then test on-demand startup.
roslynkit daemon stop --target $target
Wait-RoslynKitDaemonState -Target $target -State "not-running"
roslynkit workspace --target $target
Wait-RoslynKitDaemonState -Target $target -State "running"

# A second workspace command should reuse the running daemon.
roslynkit workspace --target $target
roslynkit daemon status --target $target

# Exercise representative discovery, navigation, source-read, and diagnostic commands.
roslynkit symbols --target $target --query PositionResolver --exact --kind class
roslynkit definition --target $target --symbol "T:RoslynKit.PositionResolver"
roslynkit references --target $target `
    --symbol "RoslynKit.PositionResolver.GetPositionAsync" `
    --max-results 3
roslynkit document-lines --target $target `
    --file (Join-Path $repoRoot "src\RoslynKit\PositionResolver.cs") `
    --start-line 1 `
    --end-line 25
roslynkit diagnostics --target $target --max-results 20

# Stop the daemon and confirm that the background process exits.
roslynkit daemon stop --target $target
Wait-RoslynKitDaemonState -Target $target -State "not-running"

Write-Host "RoslynKit $version global-tool smoke test passed."
```

Expected success markers include:

- `index-state: fresh` and `rebuilt: true` from `index`;
- one or more ranked results from `search`;
- `state: running`, `workspace: ready`, a process ID, and a positive generation after `workspace` starts the daemon;
- `command: symbols`, `command: definition`, `command: references`, `command: document-lines`, and `command: diagnostics`; and
- `state: stopping` followed by `state: not-running` during final daemon shutdown.

## 6. Install or update the side-by-side prerelease dev tool

Use a prerelease `<Version>` such as `0.2.0-dev.1` and run the dev installer from the current checkout:

```powershell
pwsh .\scripts\install-roslynkit-dev.ps1 -Version <prerelease>
```

That script:

1. Resolves the repo root and `dotnet` executable.
2. Verifies that the requested version is prerelease.
3. Builds the current checkout before packing.
4. Packs `src\RoslynKit\RoslynKit.csproj` with `/p:Version=<prerelease>`.
5. Uses `.\artifacts\packages\roslynkit-dev` as the default dev-only folder feed, unless `-PackageFeedPath` is supplied.
6. Installs or updates `roslynkit` into the fixed tool path `$HOME\.roslynkit\tools\roslynkit-dev`.
7. Prints the exact command path and smoke-test command for the installed dev tool.

The stable global `roslynkit` install can remain in place. The dev tool path is intentionally separate so stable and prerelease builds stay side-by-side, and `Directory.Build.props` can stay on the stable release version while the installer packs a temporary prerelease override.

See [docs/dev-install.md](dev-install.md) for the operator-facing dev install flow and [docs/agents/skill-maintenance.md](agents/skill-maintenance.md) for the checked-in `roslynkit` and `roslynkit-dev` skill update rules.

## 7. Publish later if needed

When you are ready to push a public package, upload the `.nupkg` from `.\artifacts\packages\roslynkit` or run `dotnet nuget push` against that file.

Do not reuse a version number after a bad package. Fix the repo, bump `<Version>`, rebuild the package, and publish a new version instead.
