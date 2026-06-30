# CLAUDE.md

This repository's working rules, safety rules, and conventions live in **@AGENTS.md**. Treat it as the single source of truth and follow it in full — it is not restated here.

`AGENTS.md` also points to the other canonical docs (`README.md`, `docs/dev-install.md`, `docs/local-repository-reference.md`, `docs/skill-maintenance.md`). Use those rather than duplicating their content.

## Claude Code specifics

Only the items below are specific to Claude Code and not already covered by `AGENTS.md`:

- `AGENTS.md` references a `scout` sub-agent for repo discovery. In Claude Code the equivalent is the **`Explore`** agent — apply the same "Scout-First Repo Search" rules from `AGENTS.md` to it.
- For ordinary C# semantic inspection in this repo, use the **`roslynkit-dev`** skill (`.claude/skills/roslynkit-dev` → `.agents/skills/roslynkit-dev/SKILL.md`) first, exactly as the "RoslynKit Default Semantic Inspection" section of `AGENTS.md` describes.
