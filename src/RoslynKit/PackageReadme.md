# roslynkit

`roslynkit` is a .NET tool for deterministic, read-only Roslyn-powered C# code intelligence in terminal and coding-agent workflows.

## Install from NuGet.org

RoslynKit targets .NET 10. Install the .NET 10 SDK first if `dotnet --version` reports an older SDK or `dotnet` is not available.

```powershell
dotnet --version
```

Install the global tool:

```powershell
dotnet tool install --global roslynkit
roslynkit version
```

`roslynkit version` confirms that the installed tool is callable from the current shell. If the install succeeds but `roslynkit` is not found, open a new shell or add the .NET global tools directory to `PATH`:

- Windows: `%USERPROFILE%\.dotnet\tools`
- macOS/Linux: `$HOME/.dotnet/tools`

To update an existing install:

```powershell
dotnet tool update --global roslynkit
roslynkit version
```

## Install from a local folder feed

```powershell
dotnet tool install --global roslynkit --add-source <local-feed-path> --version <version> --ignore-failed-sources
roslynkit version
```

To update an existing local install:

```powershell
dotnet tool update --global roslynkit --add-source <local-feed-path> --version <version> --ignore-failed-sources
roslynkit version
```

## Set up a repository

After installing the tool, run `init` once in each Git repository where coding agents should use RoslynKit. Run it from the repository root, because `init` checks the current directory for a `.git` directory or file.

```powershell
cd C:\repo\MyApp
Test-Path .git
roslynkit init
```

If `Test-Path .git` prints `False`, change to the repository root before running `roslynkit init`. The command fails from a parent folder or nested source folder that does not contain `.git`.

The default target is Codex at `.agents/skills/roslynkit/`. Use `--agent claude`, `--agent copilot`, or `--agent all` for other supported agent folders:

```powershell
roslynkit init --agent claude
roslynkit init --agent copilot
roslynkit init --agent all
```

Agent targets map to these folders:

- `codex` -> `.agents/skills/roslynkit/`
- `claude` -> `.claude/skills/roslynkit/`
- `copilot` -> `.github/skills/roslynkit/`

Existing generated files are preserved when content is identical and rejected when content differs. Add `--overwrite` only when the scaffolded RoslynKit skill files should be replaced.

## First commands

Confirm RoslynKit can load the repository solution or project:

```powershell
roslynkit workspace --target ./MySolution.slnx
roslynkit diagnostics --target ./MySolution.slnx
```

Targets can be `.slnx`, `.sln`, or `.csproj` files.

## Search by code intent

`search` finds C# declarations from an English-oriented question. It uses SQLite Full-Text Search 5 (FTS5) with internal Best Matching 25 (BM25) ranking. `index` prepares the same persistent index explicitly.

Both commands require `--target` and `--index-path`. Keep the database inside the Git repository and ignore it, for example:

```powershell
roslynkit index --target ./MySolution.slnx --index-path ./artifacts/roslynkit.db
roslynkit search --target ./MySolution.slnx --index-path ./artifacts/roslynkit.db --query "where is request validation"
```

The database has one partition per target. It persists target identities, project paths, and declaration source paths relative to the repository, then reconstructs absolute target and declaration locations from the resolved repository root for public output. `search` refreshes stale records automatically; `index --rebuild` forces a selected target rebuild. SQLite write-ahead logging (WAL) can create adjacent `roslynkit.db-wal` and `roslynkit.db-shm` files while the database is active. The index supports projects with one target framework and requires every indexed project and non-generated source document to have an existing physical path inside the target's Git worktree. It rejects missing project or non-generated source paths, external projects, and external linked non-generated source files. Generated source documents are skipped, including source-generated documents, generated paths below `bin` or `obj`, and sources with standard generated-code markers injected from extracted NuGet packages outside the worktree.

Search output is for agent-mediated follow-up. Inspect several ranked results, then pass a returned `id:` or `loc:` to an existing navigation command. RoslynKit does not read search results from standard input.

## Main workflows

- Print tool metadata with `version` or top-level `--version`.
- Enumerate workspace documents with `workspace`, including generated, additional, or analyzer-config documents when requested.
- Prepare and search a repository-local C# full-text index with `index` and `search`.
- Search, navigate, and inspect C# symbols with commands such as `symbols`, `definition`, `references`, `quick-info`, and `symbol-source`.
- Read resolved documents with `document-text`, `document-lines`, or `document-symbols`.
- Scaffold the RoslynKit skill bundle into a Git repository with `init`.

Document commands use `--file <path>` as the document selector. Relative paths resolve from the current working directory, and ambiguous linked or multi-targeted files can be narrowed with `--project`, `--tfm`, or `--document-kind`.

See [README.md](https://github.com/jgador/roslynkit#readme) for usage guidance and [.agents/skills/roslynkit/references/commands.md](https://github.com/jgador/roslynkit/blob/master/.agents/skills/roslynkit/references/commands.md) for the generated runtime command reference. Side-by-side prerelease dev installs live in [docs/dev-install.md](https://github.com/jgador/roslynkit/blob/master/docs/dev-install.md), and maintainer packaging steps live in [docs/dotnet-tool-release.md](https://github.com/jgador/roslynkit/blob/master/docs/dotnet-tool-release.md) in the same repository:

[RoslynKit on GitHub](https://github.com/jgador/roslynkit)
