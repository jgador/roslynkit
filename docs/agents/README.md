# RoslynKit Agents Documentation

These docs are for guidance that coding agents should discover and apply during normal RoslynKit repo work. Keep root [AGENTS.md](../../AGENTS.md) concise and operational; put longer agent workflow rules here only when they are safe for agents to use without a manual user tag.

## Agent Docs

- [docs/agents/roslynkit-command-reference.md](roslynkit-command-reference.md): generated runtime command names, usage strings, and options from `BuiltinCommandRegistry`.
- [docs/agents/skill-maintenance.md](skill-maintenance.md): ownership and synchronization rules for checked-in RoslynKit skills and Claude skill wrappers.

## Not Agent-Autoloaded

Keep manual references, roadmap inventories, and benchmark procedures outside this folder. They may mention agents, but they should be used only when the user explicitly names or tags them:

- [docs/local-repository-reference.md](../local-repository-reference.md): user-owned local reference repository map.
- [docs/roslyn-lsp-commands.md](../roslyn-lsp-commands.md): Roslyn language-server method inventory and RoslynKit command planning coverage.
- [docs/token-efficiency-benchmark.md](../token-efficiency-benchmark.md): manual Codex token-efficiency benchmark procedure.

## Shared Contracts

Do not duplicate shared runtime facts in hand-written agent docs. Link to these shared contracts instead:

- [docs/markdown-output-format.md](../markdown-output-format.md): deterministic command output contract.
- [docs/dev-install.md](../dev-install.md): side-by-side prerelease development install.

When command metadata changes, regenerate [docs/agents/roslynkit-command-reference.md](roslynkit-command-reference.md) with:

```powershell
dotnet run --file .\tools\RoslynKit.CommandDocs.cs -- --write
dotnet run --file .\tools\RoslynKit.CommandDocs.cs -- --check
```
