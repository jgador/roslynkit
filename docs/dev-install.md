# RoslynKit Dev Install

This document is the operator-facing source of truth for the side-by-side RoslynKit development install.

It is intentionally separate from `.agents\skills\roslynkit-dev\SKILL.md`. The skill file tells Codex how to use an already-installed dev tool. This document tells a human how to install or update that tool.

## Install location

The default side-by-side dev install path is:

```text
$HOME\.roslynkit\tools\roslynkit-dev
```

The installed command path is:

- Windows: `$HOME\.roslynkit\tools\roslynkit-dev\roslynkit.exe`
- macOS/Linux: `$HOME/.roslynkit/tools/roslynkit-dev/roslynkit`

This install is intentionally separate from the stable global `roslynkit` tool so both can exist side by side.

## Prerequisites

- .NET 10 SDK installed
- the current RoslynKit checkout
- the install script at `scripts\install-roslynkit-dev.ps1`

## One-command dev install

Run the installer with the prerelease version you want to dogfood:

```powershell
pwsh .\scripts\install-roslynkit-dev.ps1 -Version 0.1.1-dev.1
```

The script now does the full side-by-side prerelease flow from the current checkout:

1. Verifies that `-Version` is a prerelease such as `0.1.1-dev.1`.
2. Builds the repo.
3. Packs `src\RoslynKit\RoslynKit.csproj` with `/p:Version=<prerelease>`.
4. Uses the dedicated dev-only folder feed `.\artifacts\packages\roslynkit-dev` by default.
5. Installs or updates `roslynkit` into `$HOME\.roslynkit\tools\roslynkit-dev`.
6. Prints the exact smoke-test command for the installed dev tool.

This flow does not edit `Directory.Build.props`. The requested prerelease is a pack-time override for the current checkout.

## Updating an existing dev install

Re-run the install script with the target prerelease version:

```powershell
pwsh .\scripts\install-roslynkit-dev.ps1 -Version <prerelease>
```

If the dev tool already exists at the target `--tool-path`, the script uses `dotnet tool update`.

## Optional overrides

Use `-PackageFeedPath` to pack into and install from a different local folder feed:

```powershell
pwsh .\scripts\install-roslynkit-dev.ps1 -Version 0.1.1-dev.1 -PackageFeedPath .\artifacts\packages\roslynkit-dev-alt
```

Use `-ToolPath` to install the side-by-side tool somewhere other than the default user-profile path:

```powershell
pwsh .\scripts\install-roslynkit-dev.ps1 -Version 0.1.1-dev.1 -ToolPath .\artifacts\tool-install\roslynkit-dev
```

When `-PackageFeedPath` is supplied, the script packs the requested prerelease into that explicit feed before installing from it.

## Smoke test

After install or update, verify the side-by-side tool:

```powershell
$roslynkitDev = Join-Path (Join-Path (Join-Path $HOME ".roslynkit") "tools") "roslynkit-dev"
$roslynkitDev = Join-Path $roslynkitDev ($(if ($IsWindows) { "roslynkit.exe" } else { "roslynkit" }))
& $roslynkitDev version
& $roslynkitDev help
```

The reported version should include the prerelease suffix, for example `0.1.1-dev.1` or `0.1.1-dev.1+<build-metadata>`.

## Relationship to the checked-in skills

- `.agents\skills\roslynkit-dev\SKILL.md` is the repo-default usage guide for Codex inside this repo.
- `.agents\skills\roslynkit\SKILL.md` is the stable/reference skill.
- `AGENTS.md` is the source of truth for which skill is the repo-default.

Do not move these install steps into the dev skill file. Keep installation here and usage guidance in the skill.
