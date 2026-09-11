# Build, Try, and Release RoslynKit

Build and test the repository, create one local NuGet package, replace the workstation's global tool with that package, try the commands manually, then upload the **same file** to NuGet.org. Packing into [artifacts/packages/roslynkit/](../artifacts/packages/roslynkit/) does not publish anything publicly.

The commands below are for **Bash on Windows Subsystem for Linux (WSL)** or Linux. Start at the repository root and keep the same terminal open so the variables remain available. PowerShell 7 (`pwsh`), the .NET Software Development Kit (SDK) selected by [global.json](../global.json), and Git must be installed. The PowerShell helper scripts run directly from Bash; there is no need to switch shells, use a file manager, or copy a package to install it locally.

The helper scripts also run with PowerShell 7 on Windows. They run .NET commands from the checkout root to use its SDK selection. The portability regression suite at [tests/PowerShell/test-portability.ps1](../tests/PowerShell/test-portability.ps1) runs on Windows and Linux in continuous integration (CI).

Scripts print concise progress and results by default. Add `-Verbose` for command lines, successful .NET output, and per-command smoke-test progress, for example `pwsh -NoProfile ./scripts/test-package.ps1 -Verbose`. Failures always include diagnostic output; verbose mode is not required to see errors.

## 1. Choose the version and build

Set `<Version>` in [Directory.Build.props](../Directory.Build.props) to an unused bare NuGet version, for example `0.2.9`, not `v0.2.9`. Review the package ID, tool command name, repository URL, license, and readme metadata in [src/RoslynKit/RoslynKit.csproj](../src/RoslynKit/RoslynKit.csproj). Update [README.md](../README.md) and [src/RoslynKit/PackageReadme.md](../src/RoslynKit/PackageReadme.md) when the public usage or installation instructions changed.

```bash
set -o pipefail
repo="$(pwd -P)"
version="$(pwsh -NoProfile -Command '([xml](Get-Content -Raw ./Directory.Build.props)).Project.PropertyGroup.Version')"
printf 'Preparing RoslynKit %s from %s\n' "$version" "$repo"
git status --short --branch
```

Check that the version is absent from the public [NuGet version index](https://api.nuget.org/v3-flatcontainer/roslynkit/index.json) before continuing. An unavailable index is not confirmation that the version is free. Published versions cannot be replaced, even if unlisted.

Review any working-tree changes: the package will contain the current checkout, including uncommitted source changes. Then run:

```bash
dotnet restore ./RoslynKit.slnx &&
dotnet build ./RoslynKit.slnx --tl:off --nologo "-clp:ErrorsOnly;NoSummary" &&
dotnet test ./RoslynKit.slnx
```

Stop if any command fails. After C# edits, also complete the formatting steps in [AGENTS.md](../AGENTS.md) before this final build and test run.

## 2. Create the local NuGet package

```bash
pwsh -NoProfile ./scripts/pack.ps1
```

[scripts/pack.ps1](../scripts/pack.ps1) recreates only the release folder feed and packs in `Release`. It produces `artifacts/packages/roslynkit/roslynkit.<version>.nupkg`; it does not install or publish it.

After a successful pack, record the exact file and its SHA-256 fingerprint:

```bash
package="$repo/artifacts/packages/roslynkit/roslynkit.$version.nupkg"
ls -lh "$package" &&
sha256sum "$package" | tee "$repo/artifacts/packages/roslynkit.sha256"
```

**Do not pack again between testing and upload.** If code, metadata, or package contents need changing, restart from the build step and repeat the package tests.

### Optional: test in isolation before global replacement

```bash
pwsh -NoProfile ./scripts/test-package.ps1
```

This installs the existing package under [artifacts/package-validation/roslynkit/](../artifacts/package-validation/roslynkit/), using a local-only package source and an isolated cache. It checks the version, invokes every built-in command with representative arguments, and verifies that the package hash did not change. It leaves the global tool untouched.

## 3. Replace the workstation's global tool

Run this **one command from Bash**, without manually finding, copying, or renaming the package:

```bash
pwsh -NoProfile ./scripts/install-global.ps1
```

[scripts/install-global.ps1](../scripts/install-global.ps1) reads the version automatically and consumes the existing local package. It first installs and checks the candidate in isolation, then uninstalls any existing global `roslynkit` and installs the candidate from the local-only feed with an isolated cache. It also verifies the global command's version and the package hash.

Uninstall/install is intentional, including for the same version: `dotnet tool update` can reuse an existing installation instead of the new package bytes. Do not substitute an ordinary install from NuGet.org.

**This replaces the global tool and leaves the candidate installed.** If installation fails after uninstall, the global command may be unavailable. Resolve the reported error and rerun the same installer against the unchanged package; do not assume rollback occurred.

### Make the WSL command available in this terminal

After the installer succeeds:

```bash
export PATH="${DOTNET_CLI_HOME:-$HOME}/.dotnet/tools:$PATH"
hash -r
command -v roslynkit
roslynkit version
```

The command path should match the installer's `Installed command` path, normally `$HOME/.dotnet/tools/roslynkit`, and the version should match `$version` (an optional `+` build metadata suffix is valid). If an alias, function, or Windows executable takes precedence, resolve it before continuing; `type -a roslynkit` shows the alternatives. The path change lasts for this terminal; repeat it in a new terminal if necessary.

The WSL global installation is separate from a Windows global .NET tool installation. These commands test the Linux tool inside WSL.

## 4. Try every command manually

Run the following groups in order, **one command at a time**, and inspect the output. Run `echo $?` immediately after a command to see its exit code; expect `0` for every valid invocation below. A zero exit code alone is not enough: compare the output with each group's expectations. Stop release preparation on an error or unexpected result.

This checklist covers all 18 current built-in commands plus `help`, with additional search, index, symbol, and init variants. It is a manual regression check, not proof that every option combination is regression-free. The full option reference is [.agents/skills/roslynkit/references/commands.md](../.agents/skills/roslynkit/references/commands.md); output conventions are in [.agents/skills/roslynkit/references/output.md](../.agents/skills/roslynkit/references/output.md).

### 4.1. Prepare the fixture paths

Use the checked-in fixture rather than machine-specific source paths. Semantic commands deliberately target its `App` project; text-only commands search the whole repository, including tests, without loading projects. Separate databases keep these checks away from the normal repository catalog.

```bash
manual="$(mktemp -d "$repo/artifacts/manual-release.XXXXXX")"
project="$repo/tests/FixtureWorkspace/App/App.csproj"
source="$repo/tests/FixtureWorkspace/App/Source.cs"
semantic_index="$manual/semantic.db"
text_index="$manual/text.db"
mkdir -p "$manual/init/.git"
dotnet restore "$project"
```

The empty `.git` directory creates a disposable repository boundary, as in the automated command runner. `init` will write only inside that disposable repository. Keep `$manual` until inspection is complete; rerun this setup to start a fresh session without deleting earlier results.

### 4.2. Help, version, and skill initialization

```bash
roslynkit help
roslynkit version
(cd "$manual/init" && roslynkit init --agent all)
(cd "$manual/init" && roslynkit init --agent all)
(cd "$manual/init" && roslynkit init --agent all --overwrite)
find "$manual/init" -type f -name '*.md' | sort
```

Expect the current command inventory and package version. Initialization should create the stable skill bundle under all three agent roots: `.agents/skills/roslynkit`, `.claude/skills/roslynkit`, and `.github/skills/roslynkit`. The second invocation should leave matching files unchanged; the overwrite invocation should also succeed. The parentheses return Bash to the original directory automatically.

### 4.3. Workspace and diagnostics

```bash
roslynkit workspace --target "$project"
roslynkit workspace --target "$project" --include-generated --include-additional --include-analyzer-config
roslynkit diagnostics --target "$project" --max-results 20
```

Expect the `App` project and its source documents. The expanded workspace should also include generated documents and the fixture's additional/configuration documents. The unchanged fixture should report zero source diagnostics. Inspect diagnostic severity and messages, not just the exit code: reporting compiler diagnostics can itself succeed. Investigate unexpected workspace-load warnings or missing projects.

### 4.4. Build and refresh indexes

```bash
roslynkit index --target "$project" --index-path "$semantic_index" --rebuild
roslynkit index --target "$project" --index-path "$semantic_index"
roslynkit index --target "$repo" --index-path "$text_index" --text-only --rebuild
```

Expect fresh indexes and `rebuilt: true` for explicit rebuilds. The text-only index avoids loading MSBuild. Both databases should be under `$manual`, not overwrite the normal repository catalog.

### 4.5. Search: normal, filtered, compact, balanced, and text-only

```bash
roslynkit search --target "$project" --index-path "$semantic_index" --query "configuration validation performed" --max-results 10
roslynkit search --target "$project" --index-path "$semantic_index" --query "configuration validation performed" --project "$project" --kind method --max-results 10
roslynkit search --target "$project" --index-path "$semantic_index" --query "configuration validation performed" --compact --max-results 10
roslynkit search --target "$project" --index-path "$semantic_index" --query "configuration validation performed" --balanced --max-results 10
roslynkit search --target "$repo" --index-path "$text_index" --query "configuration validation performed" --text-only --max-results 10
roslynkit search --target "$repo" --index-path "$text_index" --query "configuration validation performed" --text-only --compact --balanced --max-results 10
```

Expect configuration-validation declarations such as `FixtureApp.ConfigurationValidationCatalog.ValidateConfigurationRule01` in semantic results. Text-only searches can also return repository test methods and other matching source declarations. Compare excerpts, symbol kinds, locations, and result counts rather than requiring identical ranking across different scopes. Compact output intentionally omits navigation IDs. `--balanced` only reserves results for tests when matching test declarations exist: compare the whole-repository text-only results for that behavior, not just the small `App` project. Do not combine `--text-only` with `--project`.

### 4.6. Symbols: fuzzy, exact, case-sensitive, and kind-filtered

```bash
roslynkit symbols --target "$project" --query Message --max-results 10
roslynkit symbols --target "$project" --query GeneratedMessageSource --exact --kind class --max-results 10
roslynkit symbols --target "$project" --query GeneratedMessageSource --exact --case-sensitive --max-results 10
roslynkit symbols --target "$project" --query generatedmessagesource --exact --case-sensitive --max-results 10
roslynkit symbols --target "$project" --query GetMessage --kind method --max-results 10
```

Expect message-related declarations, the exact `FixtureApp.GeneratedMessageSource` class, and `GetMessage` methods. The intentionally lowercase case-sensitive query should succeed with zero matches, not an error.

### 4.7. Document text, lines, and declarations

```bash
roslynkit document-text --target "$project" --file "$source"
roslynkit document-lines --target "$project" --file "$source" --start-line 36 --end-line 48
roslynkit document-symbols --target "$project" --file "$source"
```

Expect the full source, a bounded excerpt containing `Consumer.Run`, and declarations including `T:FixtureApp.GeneratedMessageSource`. The fixture coordinates in this guide must be updated if [tests/FixtureWorkspace/App/Source.cs](../tests/FixtureWorkspace/App/Source.cs) changes.

### 4.8. Definitions, references, and implementations

```bash
roslynkit definition --target "$project" --symbol T:FixtureApp.GeneratedMessageSource
roslynkit definition --target "$project" --file "$source" --line 46 --column 23
roslynkit type-definition --target "$project" --file "$source" --line 45 --column 13
roslynkit references --target "$project" --symbol 'M:FixtureApp.IMessageSource.GetMessage(System.String)' --max-results 10
roslynkit references --target "$project" --file "$source" --line 46 --column 23 --max-results 10
roslynkit implementations --target "$project" --symbol T:FixtureApp.IMessageSource --max-results 10
```

Expect the class declaration, the interface's `GetMessage` declaration at the call site, `IMessageSource` as the local variable's type, reference locations including the call in `Source.cs`, and `GeneratedMessageSource` as an implementation. Symbol- and position-based reference queries should identify the same member.

### 4.9. Context, quick info, signature help, and declaration source

```bash
roslynkit symbol-context --target "$project" --symbol M:FixtureApp.Consumer.Run
roslynkit quick-info --target "$project" --file "$source" --line 46 --column 23
roslynkit signature-help --target "$project" --file "$source" --line 46 --column 34
roslynkit symbol-source --target "$project" --symbol M:FixtureApp.Consumer.Run
```

Expect `Consumer.Run` context and its `GetMessage` invocation, quick info for `GetMessage`, a signature with its string parameter, and the full `Run` method body. These positions select the checked-in fixture call, not arbitrary cursor offsets.

### 4.10. Finish the manual review

Compare `roslynkit help` with the groups above so a newly added command is not missed. Review any changed behavior on a real project too, especially options or output shapes touched by the release.

The existing automated runner remains available as a final coverage guard:

```bash
pwsh -NoProfile ./scripts/test-global.ps1
```

It compares its command cases with runtime help and fails for missing or stale cases, then checks representative output for every built-in command. It does not replace manual inspection or cover every option permutation. If it fails, do not upload.

The Bash examples above are the manual checklist. The scripts only run automated checks; they no longer generate a separate PowerShell checklist.

## 5. Approve and copy the exact package

Proceed only after the repository tests passed, the global version/path were correct, all manual groups behaved as expected, and any optional automated checks passed. Do not change the source or package metadata while assessing the candidate.

```bash
sha256sum --check "$repo/artifacts/packages/roslynkit.sha256"
git status --short --branch
printf 'Upload this file: %s\n' "$package"
```

Expect `OK` from the hash check. A changed package must be tested again; do not merely replace the recorded hash to make the check pass. Preserve the tested file before any later pack, because the packaging helper recreates the local feed.

### Copy from WSL to Windows without a file manager

With Windows interoperability enabled, this discovers the Windows profile and copies the file into a dedicated folder without hard-coding a username:

```bash
windows_profile="$(cmd.exe /C 'echo %USERPROFILE%' | tr -d '\r')" &&
test -n "$windows_profile" &&
windows_home="$(wslpath -u "$windows_profile")" &&
test -d "$windows_home" &&
upload_dir="$windows_home/RoslynKitUpload" &&
mkdir -p "$upload_dir" &&
cp -i -- "$package" "$upload_dir/" &&
cmp -- "$package" "$upload_dir/$(basename "$package")" &&
wslpath -w "$upload_dir/$(basename "$package")"
```

`cp -i` asks before overwriting an existing copy. A successful `cmp` prints nothing and confirms identical bytes. The last line prints the Windows path to choose in the browser's upload dialog. If `cmd.exe` or `wslpath` is unavailable, copy to an accessible mounted Windows directory instead, then compare the copied file with `cmp` before uploading.

## 6. Upload to NuGet.org

1. Recheck the [NuGet version index](https://api.nuget.org/v3-flatcontainer/roslynkit/index.json). Stop if this version already exists.
2. Sign in to [NuGet.org's upload page](https://www.nuget.org/packages/manage/upload) using an account that owns the `roslynkit` package.
3. Select the exact tested `.nupkg`, or the byte-identical Windows copy from section 5.
4. Review the package ID, version, description, license, repository links, and readme preview. If anything needs changing, stop and rebuild/retest a candidate rather than uploading an untested replacement.
5. Select **Submit**. This is the public publication step; none of the earlier commands publish anything.
6. Wait for NuGet validation and indexing, then confirm the expected version appears on the [RoslynKit package page](https://www.nuget.org/packages/roslynkit) and in the version index.

NuGet versions are immutable. If a published package is bad, fix the repository, choose a new version, and repeat the workflow. Unlisting a version does not make it reusable. See [NuGet's publishing guide](https://learn.microsoft.com/en-us/nuget/nuget-org/publish-a-package) for portal details.

## Related workflows

For a side-by-side prerelease installation that leaves the stable global tool alone, use [docs/dev-install.md](dev-install.md). The release preparation scripts do not commit, tag, push Git changes, or create GitHub releases; those are separate maintainer actions.
