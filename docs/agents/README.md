# RoslynKit Agents Documentation

These docs are for repo-maintenance guidance that coding agents should discover and apply during normal RoslynKit repo work. Keep root [AGENTS.md](../../AGENTS.md) concise and operational; put reusable RoslynKit command-skill references under [.agents/skills/roslynkit/references/](../../.agents/skills/roslynkit/references/) so `roslynkit init` can scaffold the same files into other repositories.

## Agent Docs

- [docs/agents/skill-maintenance.md](skill-maintenance.md): ownership and synchronization rules for checked-in RoslynKit skills and init-scaffolded bundles.

## Skill Bundle References

- [.agents/skills/roslynkit/references/commands.md](../../.agents/skills/roslynkit/references/commands.md): generated runtime command names, usage strings, and options from `BuiltinCommandRegistry`.
- [.agents/skills/roslynkit/references/output.md](../../.agents/skills/roslynkit/references/output.md): deterministic command output contract, including documentation-comment ID prefix meanings.

## Not Agent-Autoloaded

Keep manual references, roadmap inventories, and benchmark procedures outside this folder. They may mention agents, but they should be used only when the user explicitly names or tags them:

- [docs/local-repository-reference.md](../local-repository-reference.md): user-owned local reference repository map.
- [docs/roslyn-lsp-commands.md](../roslyn-lsp-commands.md): Roslyn language-server method inventory and RoslynKit command planning coverage.
- [docs/benchmark-codex.md](../benchmark-codex.md): manual Codex token-efficiency benchmark procedure.

## Linked Shared Docs

Do not duplicate shared runtime or workflow facts in hand-written agent docs. Link to the canonical source instead:

- [docs/dev-install.md](../dev-install.md): side-by-side prerelease development install.

When command metadata changes, regenerate [.agents/skills/roslynkit/references/commands.md](../../.agents/skills/roslynkit/references/commands.md) with:

```powershell
dotnet run --file ./tools/RoslynKit.CommandDocs.cs -- --write
dotnet run --file ./tools/RoslynKit.CommandDocs.cs -- --check
```
