---
name: git-commit-push
description: Stage all non-ignored RoslynKit repo changes, commit with the prepared commit context file, and push non-interactively. Use when the user invokes `$git-commit-push` or asks to commit and push current RoslynKit changes using the prepared commit context.
---

The canonical skill is [.agents/skills/git-commit-push/SKILL.md](../../../.agents/skills/git-commit-push/SKILL.md); its full content is inlined below. If the content is missing or replaced by a policy notice, read that file directly. Do not add normative guidance to this wrapper.

This repo-local skill takes precedence over any user-level `git-commit-push` skill: it commits from the prepared [artifacts/commit-context.md](../../../artifacts/commit-context.md) instead of drafting a new message.

!`powershell.exe -NoProfile -Command "Get-Content -Raw '.agents/skills/git-commit-push/SKILL.md'"`
