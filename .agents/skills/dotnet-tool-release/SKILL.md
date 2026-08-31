---
name: dotnet-tool-release
description: Build, package, exhaustively smoke-test, and optionally install RoslynKit as the global .NET tool, while leaving the exact tested package ready for manual NuGet.org upload. Use only when the user invokes `$dotnet-tool-release` or explicitly asks to prepare or locally install a RoslynKit release; never publish it.
---

# RoslynKit .NET Tool Release

Treat the text after `$dotnet-tool-release` as a forgiving command-like request. A bare invocation behaves as `ready`. Accept at most one action and one optional expected version; reject unknown, duplicate, or conflicting values with a compact usage correction.

Read [docs/dotnet-tool-release.md](../../../docs/dotnet-tool-release.md) before packaging or testing. That document is the source of truth for release metadata, commands, local installation, smoke tests, and publication constraints. This skill only selects and enforces the workflow.

## Actions

- `help`, `?`: show the actions and current version from [Directory.Build.props](../../../Directory.Build.props). Start nothing.
- `status`, `inspect`: report the current version, Git status, expected package path, and package hash when the expected file exists. Do not infer that an existing package passed prior validation.
- `pack`, `candidate`: run the shared preflight, the standard validation lane in section 2 of the release guide, and the local folder-feed build in section 3. Stop after reporting the package path, size, and SHA-256 hash.
- `smoke`, `test`, `test-package`: run the shared preflight, require the current package to exist without repacking it, and exhaustively test it in an isolated tool path with [scripts/test-roslynkit-package.ps1](../../../scripts/test-roslynkit-package.ps1). Do not run repository validation or claim full release readiness.
- `install-global`, `replace-global`, `global`: run the shared preflight, require the current package to exist without repacking it, and replace the global `roslynkit` tool with that exact local package by running [scripts/install-roslynkit-global.ps1](../../../scripts/install-roslynkit-global.ps1). Do not run repository validation or command smoke tests.
- `smoke-global`, `test-global`: run the shared preflight, require the globally installed command to report the current version, and exercise every runtime command through that global command with [scripts/test-roslynkit-global.ps1](../../../scripts/test-roslynkit-global.ps1). Do not pack or replace the global tool.
- `local-release`, `local`, `dogfood`: run the standard validation lane, pack and exhaustively test the isolated package, replace the global tool with that exact package, and exhaustively test every command through the global installation. This action does not check public NuGet version availability and is not upload-ready.
- `ready`, `release`, `all`: run the complete release-candidate workflow below. This is the default.

`publish`, `push`, `upload`, `tag`, and `commit` are outside this skill. Never run `dotnet nuget push`, create a GitHub release, change Git refs, commit, or push. Report the ready package path so a separate explicit publication action can use it.

Global installation is allowed only for `install-global` and `local-release` actions and their aliases. `pack`, `smoke`, `ready`, `status`, and `smoke-global` must leave the global tool untouched.

## Shared Preflight

1. Verify the repository root with read-only Git commands and inspect:
   - `git branch --show-current`
   - `git status --short --branch`
   - `git diff --stat`
   - `git diff --cached --stat`
   - `git ls-files --others --exclude-standard`
2. Read `<Version>` from [Directory.Build.props](../../../Directory.Build.props). Require a bare NuGet version without a leading `v`. If the invocation supplies an expected version, require an exact match and stop on mismatch.
3. Confirm the package metadata called out in section 1 of the release guide in [src/RoslynKit/RoslynKit.csproj](../../../src/RoslynKit/RoslynKit.csproj). Do not edit the version or package metadata unless the user explicitly requests that edit.
4. Capture the initial Git status. Local validation may run from a dirty checkout, but the final result must disclose pre-existing and newly created non-ignored changes.
5. For `ready`, query the public NuGet version index for package ID `roslynkit`. Stop if the current version already exists because NuGet versions are immutable. If the check cannot complete, mark upload readiness as unverified rather than silently treating the version as available. Local global-install actions do not need this public availability check.

## Exhaustive Command Smoke Test

[scripts/test-roslynkit-commands.ps1](../../../scripts/test-roslynkit-commands.ps1) owns exhaustive command coverage. It must:

1. Discover the current built-in command names from `roslynkit help`.
2. Require exactly one representative successful invocation for every discovered command.
3. Exercise commands against the checked-in fixture workspace and use an isolated repository under `artifacts` for `init`.
4. Continue after individual command failures so the final report includes every failing command.
5. Report each failure with its command name, full invocation, exit code or timeout, missing output expectations, standard output, and standard error.
6. Fail when runtime help adds or removes a command without a matching smoke case.

Exhaustive means every built-in command is invoked with representative valid arguments. It does not mean every option permutation is tested.

## Global Replacement Safety

[scripts/install-roslynkit-global.ps1](../../../scripts/install-roslynkit-global.ps1) consumes the existing package and must not pack again. It must:

1. Require `artifacts/packages/roslynkit/roslynkit.<version>.nupkg`.
2. Record the package SHA-256 hash.
3. Stage-install and version-check the package through a local-only NuGet configuration and isolated package cache before changing the global tool.
4. Detect an existing global `roslynkit`, uninstall it when present, and install the current package from the same local-only source. The uninstall/install sequence is intentional even when the version matches, because `dotnet tool update` may reuse an already installed package.
5. Verify the command in the active global tool directory, including a configured `DOTNET_CLI_HOME`, reports the expected version.
6. Require the package hash to remain unchanged.

This is an explicit destructive local action: the candidate remains installed globally. If installation fails after the previous tool is uninstalled, report that the global command may be unavailable; do not silently claim rollback or success.

## Complete Release-Candidate Workflow

1. Run the standard restore, build, and test commands from section 2 of the release guide. Stop at the first failure.
2. Run `pwsh ./scripts/prepare-roslynkit-package.ps1`. It must:
   - recreate `artifacts/packages/roslynkit`;
   - produce exactly `roslynkit.<version>.nupkg`.
3. Record the package size and SHA-256 hash before the command smoke test.
4. Run `pwsh ./scripts/test-roslynkit-package.ps1`. It must:
   - reset only the package-validation path under `artifacts`;
   - use a local-only NuGet configuration and isolated package cache;
   - install the package into an isolated tool path under `artifacts`;
   - verify that the installed command reports the expected version;
   - run the exhaustive command smoke test through that installed command;
   - require the package SHA-256 hash to remain unchanged.
5. Recompute the package SHA-256 hash and require it to match both the pre-test hash and the hash reported by the smoke-test script.
6. Do not pack again after the smoke test. The exact package that passed installation and command testing is the NuGet upload candidate.
7. Re-check Git status and report any newly created non-ignored files. Do not stage, delete, or commit them.

## Local Global-Tool Workflow

For `local-release`:

1. Run the shared preflight and the standard restore, build, and test commands from section 2 of the release guide. Stop at the first failure.
2. Run `pwsh ./scripts/prepare-roslynkit-package.ps1`.
3. Record the package size and SHA-256 hash.
4. Run `pwsh ./scripts/test-roslynkit-package.ps1` and require all discovered commands to pass through the isolated installation.
5. Recompute and require the package hash to match.
6. Run `pwsh ./scripts/install-roslynkit-global.ps1`. This deliberately replaces the existing global RoslynKit installation.
7. Run `pwsh ./scripts/test-roslynkit-global.ps1` and require all discovered commands to pass through the global command path.
8. Recompute and require the package hash to match the original hash. Do not pack again.
9. Re-check Git status and report any newly created non-ignored files. Do not stage, delete, or commit them.

For `smoke`, skip packing and repository validation; consume the existing package and run only the isolated installed-package test. For `install-global`, skip packing, repository validation, and exhaustive smoke testing; consume the existing package and run only the global replacement workflow. For `smoke-global`, do not pack or install; test the existing global command.

## Result Contract

Return these fields in this order:

```text
Action: <status|pack|smoke|install-global|smoke-global|local-release|ready>
Version: <version>
Package: <repo-relative path or missing>
Size: <bytes or unavailable>
SHA-256: <hash or unavailable>
Installed command: <repo-relative validation path or unavailable>
Global command: <absolute global command path or unavailable>
Repository validation: <passed|not run|failed>
Installed-package smoke test: <passed|not run|failed>
Global installation: <installed|replaced|not run|failed>
Global command smoke test: <passed|not run|failed>
Commands exercised: <passed/total or not run>
NuGet version availability: <available|already published|unverified|not checked>
Upload readiness: <ready|not ready>
Publication: not performed
Working tree: <unchanged by workflow or concise status summary>
```

Only `ready` may report `Upload readiness: ready`, and only after every required command succeeds, the version is not already published, and the package hash remains unchanged.

When an exhaustive smoke test fails, include the detailed per-command failure report emitted by the script before the result contract. Do not replace it with a generic failure summary.
