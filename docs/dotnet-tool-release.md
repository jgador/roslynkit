# RoslynKit Dotnet Tool Packaging

Run every command from the repo root.

## What gets produced

RoslynKit currently produces one .NET tool package:

- `roslynkit`

The release version comes from `Directory.Build.props`. The public package metadata lives in `src/RoslynKit/RoslynKit.csproj`, and the NuGet package readme lives in [src/RoslynKit/PackageReadme.md](../src/RoslynKit/PackageReadme.md).

## Skill-assisted workflow

The repo-local [.agents/skills/dotnet-tool-release/SKILL.md](../.agents/skills/dotnet-tool-release/SKILL.md) turns the manual steps below into an explicit agent workflow:

- `$dotnet-tool-release`: run the complete validation, local packaging, isolated installed-tool smoke test, and upload-readiness checks.
- `$dotnet-tool-release pack`: validate the repo and create the local folder-feed package without installing it.
- `$dotnet-tool-release smoke`: install and exhaustively smoke-test the existing local package without repacking or running the full validation lane.
- `$dotnet-tool-release install-global`: replace the global `roslynkit` tool with the exact package already in the local folder feed.
- `$dotnet-tool-release smoke-global`: exhaustively test every command through the current global `roslynkit` installation.
- `$dotnet-tool-release local-release`: validate, pack, test in isolation, replace the global tool, and test every command globally.
- `$dotnet-tool-release manual-release`: validate, pack, replace the global tool, and print every exhaustive command for the user to copy and run manually.
- `$dotnet-tool-release status`: inspect the current version and any existing package without changing local state.

One invocation may contain any ordered combination of actions, with one optional expected version applied to the whole batch:

```text
$dotnet-tool-release pack smoke
$dotnet-tool-release pack and manual-release
$dotnet-tool-release pack, smoke, install-global, smoke-global 0.2.8
```

The skill validates the complete action sequence before starting and stops at the first failure. Successful phases from earlier actions are reused within the same invocation only while the Git snapshot, version, package hash, and installed command state remain unchanged. For example, `pack manual-release` validates and packs once, then installs that exact package globally and prints the manual checklist. Testing or installing a package that existed before the invocation records it for later package-consuming actions, but does not replace validation and packing when a later action requires a package from the current checkout. Actions retain their requested order; the skill does not move a later `pack` ahead of an earlier `smoke`.

The skill never publishes to NuGet.org and never commits, tags, or pushes Git state. The default `ready` action never changes the global tool. Global replacement occurs only through the explicit `install-global`, `local-release`, and `manual-release` actions. The automated complete workflows leave the exact installed and smoke-tested `.nupkg` in `./artifacts/packages/roslynkit`; do not repack after the smoke test, because that would produce an artifact that was not tested. `manual-release` leaves the exact installed package in the same folder but reports it as not upload-ready until the printed checklist has been run and assessed manually.

The same actions can still be run as separate invocations around one immutable local package:

```text
$dotnet-tool-release pack
$dotnet-tool-release smoke
$dotnet-tool-release install-global
$dotnet-tool-release smoke-global
```

`smoke` and `install-global` consume the package produced by `pack` without recreating it.

## 1. Update package metadata

1. Set the new `<Version>` in `Directory.Build.props` using a bare NuGet version such as `0.2.0` or a prerelease such as `0.2.0-dev.1`. Use the leading `v` only for Git tags or release titles such as `v0.2.0`.
2. Confirm `src/RoslynKit/RoslynKit.csproj` still has the correct public package metadata: `PackageId` is `roslynkit`, `ToolCommandName` is `roslynkit`, and the repository URL, license, tags, and package readme values are still correct.
3. If the public CLI surface, repo-local skill workflow, or install story changed, update [README.md](../README.md), [docs/agents/skill-maintenance.md](agents/skill-maintenance.md), and [docs/dev-install.md](dev-install.md) in the same change when applicable.
4. Confirm the selected version is absent from the public [NuGet package version index](https://api.nuget.org/v3-flatcontainer/roslynkit/index.json). Published NuGet versions are immutable and cannot be reused.

## 2. Validate the repo before packing

Run the standard validation lane first:

```powershell
dotnet restore ./RoslynKit.slnx
dotnet build ./RoslynKit.slnx --tl:off --nologo "-clp:ErrorsOnly;NoSummary"
dotnet test ./RoslynKit.slnx
```

## 3. Build the local folder feed

Use the helper script:

```powershell
pwsh ./scripts/prepare-roslynkit-package.ps1
```

That script:

1. Resolves the repo root and `dotnet` executable.
2. Reads and validates `<Version>` from `Directory.Build.props`.
3. Recreates the local folder feed at `./artifacts/packages/roslynkit`.
4. Packs [src/RoslynKit/RoslynKit.csproj](../src/RoslynKit/RoslynKit.csproj) in `Release` into that folder feed.
5. Verifies that `roslynkit.<version>.nupkg` exists.
6. Prints the exact global replacement, automated smoke-test, and manual-checklist commands for the packed version and, when the packed version is prerelease, the side-by-side dev install command.

If you want the raw command instead of the helper script, this is the equivalent pack step:

```powershell
dotnet pack ./src/RoslynKit/RoslynKit.csproj -c Release -o ./artifacts/packages/roslynkit
```

## 4. Replace the stable global tool with the local package

Use the replacement script after packing:

```powershell
pwsh ./scripts/install-roslynkit-global.ps1
```

The script:

1. Consumes the existing `roslynkit.<version>.nupkg` without packing again.
2. Uses a local-only NuGet configuration and isolated package cache.
3. Stage-installs and version-checks the exact package before changing the global tool.
4. Uninstalls any existing global `roslynkit`, even when it has the same version.
5. Installs the package into the active global tool location, normally `$HOME/.dotnet/tools` on Linux and macOS or `%USERPROFILE%/.dotnet/tools` on Windows, and otherwise the global path rooted at `DOTNET_CLI_HOME` when that variable is configured.
6. Verifies the global command version and confirms that the package hash did not change.

The uninstall/install sequence guarantees that the global command comes from the current local package. A same-version `dotnet tool update` may reuse the existing installation and therefore does not prove that the current package bytes were installed.

This is an explicit state-changing operation. The candidate remains installed globally. If installation fails after uninstalling the previous version, the script reports the failure and the global command may be unavailable.

## 5. Smoke-test the packaged tool

The automated package test installs the freshly packed release into an isolated tool path under `./artifacts/package-validation/roslynkit`. It also uses an isolated NuGet package cache and a local-only NuGet configuration so a previously cached package cannot replace the candidate being tested:

```powershell
pwsh ./scripts/test-roslynkit-package.ps1
```

The script verifies the installed version and delegates command coverage to [scripts/test-roslynkit-commands.ps1](../scripts/test-roslynkit-commands.ps1). That runner discovers built-in commands from `roslynkit help`, invokes every discovered command once with representative valid arguments, checks meaningful output and artifacts, and fails if a runtime command has no smoke case. It continues after individual failures and reports each failed invocation, exit code or timeout, standard output, and standard error before returning failure.

The exhaustive scope covers every built-in command, not every possible option combination. The checked-in fixture workspace provides deterministic semantic targets, while `init` runs against a disposable repository under `artifacts`.

After replacing the global tool, run the same exhaustive checks through the global command:

```powershell
pwsh ./scripts/test-roslynkit-global.ps1
```

This wrapper resolves the command in the active global `.dotnet/tools` directory, including a configured `DOTNET_CLI_HOME`, verifies that it reports the version from [Directory.Build.props](../Directory.Build.props), and runs [scripts/test-roslynkit-commands.ps1](../scripts/test-roslynkit-commands.ps1) against that exact path.

To pack, validate in isolation, replace the global tool, and exhaustively test the global installation in one skill action, run `$dotnet-tool-release local-release`.

To validate, pack, replace the global tool, and perform the exhaustive command checks manually, run `$dotnet-tool-release manual-release`. The agent runs:

```powershell
pwsh ./scripts/test-roslynkit-global.ps1 -PrintManualCommands
```

This mode verifies the installed version, invokes `help` only to ensure the checklist still covers the current command inventory, prepares a disposable fixture workspace, and prints one ordered PowerShell block. The block contains `help` and every representative built-in command invocation, with each runnable line beginning with the global command name `roslynkit` so it can be copied and pasted directly. Comments identify the expected zero exit code, output text, package version, and created paths to inspect. The resolved global executable path is still verified before the checklist is printed. The agent prints the block without executing it so it can be run manually. Run the whole block in order because later commands reuse artifacts such as the search index created by earlier commands.

## 6. Install or update the side-by-side prerelease dev tool

Use a prerelease `<Version>` such as `0.2.0-dev.1` and run the dev installer from the current checkout:

```powershell
pwsh ./scripts/install-roslynkit-dev.ps1 -Version <prerelease>
```

That script:

1. Resolves the repo root and `dotnet` executable.
2. Verifies that the requested version is prerelease.
3. Builds the current checkout before packing.
4. Packs [src/RoslynKit/RoslynKit.csproj](../src/RoslynKit/RoslynKit.csproj) with `/p:Version=<prerelease>`.
5. Uses `./artifacts/packages/roslynkit-dev` as the default dev-only folder feed, unless `-PackageFeedPath` is supplied.
6. Installs or updates `roslynkit` into the fixed tool path `$HOME/.roslynkit/tools/roslynkit-dev`.
7. Prints the exact command path and smoke-test command for the installed dev tool.

The stable global `roslynkit` install can remain in place. The dev tool path is intentionally separate so stable and prerelease builds stay side-by-side, and `Directory.Build.props` can stay on the stable release version while the installer packs a temporary prerelease override.

See [docs/dev-install.md](dev-install.md) for the operator-facing dev install flow and [docs/agents/skill-maintenance.md](agents/skill-maintenance.md) for the checked-in `roslynkit` and `roslynkit-dev` skill update rules.

## 7. Publish later if needed

When you are ready to push a public package, upload the `.nupkg` from `./artifacts/packages/roslynkit` or run `dotnet nuget push` against that file.

Upload the exact package that passed the local installation and smoke test. Do not run `dotnet pack` again between testing and upload.

Do not reuse a version number after a bad package. Fix the repo, bump `<Version>`, rebuild the package, and publish a new version instead.
