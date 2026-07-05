---
name: git-commit-push
description: Stage all non-ignored RoslynKit repo changes, commit with `artifacts/commit-context.md`, and push non-interactively. Use when the user invokes `$git-commit-push` or asks Codex to commit and push current RoslynKit changes using the prepared commit context.
---

# Git Commit and Push

Use this repo-local workflow only after the user explicitly asks for `$git-commit-push` or an equivalent commit-and-push action. It changes git state.

This RoslynKit skill is intentionally narrower than the global workflow: do not draft a new commit message during commit/push. Use the prepared `artifacts/commit-context.md` file as the commit message.

## Workflow

1. Verify repo context before mutating git:
   - `git rev-parse --show-toplevel`
   - `git branch --show-current`
   - `git remote -v`
   - upstream check with `git rev-parse --abbrev-ref --symbolic-full-name '@{u}'`
   - `git status --short --branch`
   - `git diff --stat`
   - `git diff --name-status`
   - `git diff --cached --stat`
   - `git diff --cached --name-status`
   - `git ls-files --others --exclude-standard`

2. Validate `artifacts/commit-context.md` before staging:
   - require the file to exist and be non-empty
   - require it to be ignored by git
   - require it to look like a commit message: Conventional Commit subject, blank line, concise body paragraphs
   - do not append trailers or generated sections unless they are already in the file or the user explicitly asks
   - if the file is missing, empty, or obviously stale, stop and refresh it with `.agents\skills\commit-context\SKILL.md` before committing

3. Stop before staging if there is no real diff to commit, or if the visible diff includes secrets, accidental binaries, package outputs, local credentials, generated caches, or unrelated changes that should not be part of one commit.

4. Stage all current non-ignored changes with `git add -A`.

5. Re-check the staged result:
   - `git status --short --branch`
   - `git diff --cached --stat`
   - `git diff --cached --name-status`
   - `git diff --cached --check`

6. Commit using the ignored context file directly:

   ```powershell
   git commit -F artifacts/commit-context.md
   ```

7. Push the current branch:
   - if the branch has an upstream, run `git push`
   - if the branch has no upstream and `origin` exists, run `git push -u origin <branch>`
   - if there is no upstream and no `origin`, stop and report the available remotes

## Output Contract

When using this skill, respond in this order:

```text
Branch: <branch>
Upstream: <upstream or none>

Commit message:
<fenced block with artifacts/commit-context.md content>

Execution:
- Staged with git add -A
- Committed <hash>
- Pushed to <remote/branch>

Notes:
- <only include if there is risk, upstream creation, skipped verification, or no-op>
```

## Defaults

- Default to all current tracked and untracked non-ignored files.
- Default to `artifacts/commit-context.md` as the only commit message source.
- Default to pushing the current branch.
- Default to setting upstream on `origin` when no upstream exists.
- Never use interactive git commands.
