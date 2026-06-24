# Repository Guidelines

## Canonical Docs

Use one source of truth per topic:

- `README.md`: user-facing command list, JSON envelope shape, packaging, and quick-start commands.
- `docs/local-repository-reference.md`: local reference repositories for Roslyn APIs, Git CLI style, EF Core tooling conventions, and VS Code C# language-server wiring.
- `AGENTS.md`: agent-specific working rules, safety rules, and repo workflow expectations.

Keep this file focused on contributor and agent workflow rules. Do not restate full command reference material here.

## Project Structure & Module Organization

RoslynKit is a .NET 10 command-line tool. Production code lives under `src/RoslynKit`, with the console entrypoint in `Program.cs`, CLI parsing in `CliParser.cs`, command metadata in `BuiltinCommandRegistry.cs`, and Roslyn execution logic in `RoslynCommandExecutor.cs`. Tests live under `tests/RoslynKit.Tests`. Repository documentation lives under `docs/`; use `docs/local-repository-reference.md` before searching remote repositories for Roslyn, Git, EF Core, or VS Code C# implementation references.

## Build, Test, and Development Commands

- `dotnet restore .\RoslynKit.slnx` restores packages for the solution.
- `dotnet build .\RoslynKit.slnx --tl:off --nologo "-clp:ErrorsOnly;NoSummary"` builds the CLI and tests with concise output.
- `dotnet test .\RoslynKit.slnx --nologo` runs the xUnit test suite.
- `dotnet run --project .\src\RoslynKit -- help` runs the CLI locally.
- `dotnet pack .\src\RoslynKit\RoslynKit.csproj` creates the .NET tool package.

## Agent Workflow

Use Repository Synapse first when available, then verify all conclusions against current files, tests, and command output. Treat Synapse as recall only, not source of truth.

### Scout-First Repo Search

Use the `scout` sub-agent for repo discovery when the current agent environment exposes it and any of these are true:

- the user asks to find, trace, investigate, or discover which files matter;
- the user did not name an exact target file;
- more than one top-level area may be relevant;
- the likely read set is more than 3 files.

Skip `scout` when one obvious target file is already known or the task is a single-file explanation or edit.

When using `scout`:

- ground first in `AGENTS.md`, `README.md`, `docs/local-repository-reference.md`, and any directly named files;
- normalize and de-overlap scopes before spawning;
- spawn one `scout` per disjoint scope;
- prefer these default scopes when the area is unclear: `src/RoslynKit/`, `tests/RoslynKit.Tests/`, `docs/`, repo-root config files, and `.agents/` or `.codex/` if agent packaging is involved.

Every scout prompt must include `assigned_scope`, `search_goal`, known keywords or filenames, and the required response format. Every scout must return `assigned_scope`, `files_examined`, `likely_relevant_files`, `evidence`, `handoff_paths`, `short_summary`, and `confidence`. After scouts return, inspect `likely_relevant_files` locally before deeper tracing.

## State-Changing Git Command Safety

Git read commands such as `git status`, `git log`, `git diff`, `git show`, and `git branch --show-current` are allowed for inspection.

Do not run state-changing Git commands unless the user explicitly asks for that exact action in the current task, or unless you ask for permission in chat and receive approval first. State-changing Git commands include `git commit`, `git push`, `git merge`, `git rebase`, `git cherry-pick`, `git checkout -b`, `git switch -c`, `git tag`, `git reset`, `git revert`, `git stash`, branch deletion, and any command that changes refs, the index, or the working tree.

Before any commit or push:

- verify there is a real diff, not just stat or line-ending noise;
- inspect staged, unstaged, and untracked files;
- stage only intended files unless the user explicitly asks to stage all files;
- re-check branch, upstream, and `git status`;
- stop cleanly if the tree is already synced.

## Coding Style & Naming Conventions

Follow `.editorconfig`: UTF-8, spaces, final newline, 4-space indentation for C# and PowerShell, 2-space indentation for XML/JSON/YAML. C# uses file-scoped namespaces, nullable reference types, implicit usings, latest language version, and warnings as errors. Use `Cli`, not `CLI`, in C# identifiers. Keep command output deterministic and JSON-first.

## C# Working Rules

Follow the existing style in touched files. Keep comments sparse; prefer clear names and add comments only for non-obvious behavior.

- Preserve the CLI-first architecture: no MCP server, no LSP client, no background daemon, and no editor-specific protocol coupling.
- Prefer direct Roslyn/MSBuild APIs over shelling out to editors, language servers, or IDEs.
- Keep command execution separate from argument parsing.
- Keep JSON contracts stable, deterministic, camelCase, and covered by parser/envelope tests when changed.
- Pass `CancellationToken` through async Roslyn operations when available.
- If a public CLI option, command, JSON shape, package surface, or documented workflow changes, update `README.md` or the relevant docs in the same change.

## Testing Guidelines

Tests use xUnit in `tests/RoslynKit.Tests`. Name test methods as behavior statements, for example `Parse_RejectsDuplicateOption`. Add parser/envelope tests for CLI contract changes and focused Roslyn execution tests when command behavior changes. Run `dotnet test .\RoslynKit.slnx --nologo` before publishing changes.

## Commit & Pull Request Guidelines

Recent history uses short imperative subjects and conventional prefixes such as `docs:` and `chore:`. Match the surrounding history and keep subjects imperative.

- Keep commits focused. Separate behavior changes, docs-only edits, generated output refreshes, and repo maintenance when practical.
- Before committing, confirm the diff is real and relevant. Do not commit CRLF-only or stat-only churn just because `git status` mentions a file.
- Pull requests should explain what command, parser behavior, Roslyn behavior, or docs surface changed.
- Note JSON contract impacts explicitly and list the exact validation commands that were run.
- For this CLI-first repo, command output snippets and JSON examples are usually more useful than screenshots.

## Security & Configuration Tips

Do not commit secrets, local credentials, generated caches, package outputs, or accidental binaries. Keep only `.synapse/ignored.json` tracked under `.synapse/`; generated Synapse files such as `graph.json`, `memories.md`, and `synapse-memory.json` must remain local-only.

Treat local reference repositories in `docs/local-repository-reference.md` as read-only references. Do not edit `C:\repo\GitHub\efcore`, `C:\repo\GitHub\git`, `C:\repo\GitHub\roslyn`, or `C:\repo\GitHub\vscode-csharp` while working in RoslynKit unless the user explicitly asks for changes in those repos.
