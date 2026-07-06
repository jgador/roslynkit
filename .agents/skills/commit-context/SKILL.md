---
name: commit-context
description: Refresh the ignored `artifacts/commit-context.md` file from the current git change set. Use as an end-of-session maintenance step, before `$git-commit-push`, or when the user asks to summarize current changes for faster commit preparation.
---

# Commit Context

Update `artifacts/commit-context.md` as a commit-ready message. Run this near the end of a meaningful coding session, similar to post-change formatting. The file is advisory only: future commit/push work must still inspect the live git status and diffs.

## Workflow

1. Verify repo context with read-only git commands:
   - `git rev-parse --show-toplevel`
   - `git branch --show-current`
   - `git remote -v`
   - upstream check with `git rev-parse --abbrev-ref --symbolic-full-name '@{u}'`
   - `git status --short`
   - `git diff --cached --stat`
   - `git diff --stat`
   - `git diff --name-status`
   - `git diff --cached --name-status`
   - `git ls-files --others --exclude-standard`

2. Inspect enough diff detail to describe the whole current change set, not only the most recent edit. Use targeted `git diff -- <path>` reads when stats and filenames are not enough.

3. Inspect recent commit messages with `git log -8 --pretty=format:%s%n%n%b%n---END---` and match the local style. Recent RoslynKit commits use:
   - a Conventional Commit subject such as `feat: surface reference symbol documentation`
   - a blank line after the subject
   - two to four imperative body paragraphs
   - concise paragraphs describing what changed, with no markdown headings or bullets

4. Write or refresh `artifacts/commit-context.md` as a ready-to-use commit message:
   - first line: subject
   - second line: blank
   - remaining lines: body paragraphs matching recent commit structure
   - final trailer: `Co-authored-by: Codex <242516109+Codex@users.noreply.github.com>`
   - keep one blank line between the final body paragraph and the trailer
   - include the whole current change set, not only the latest edit
   - do not include status headings, verification logs, risk lists, or template labels unless they belong in the commit message itself

5. Do not stage, commit, push, stash, reset, or otherwise mutate git state.

6. Do not treat `artifacts/commit-context.md` as authoritative. It accelerates commit-message drafting, but the actual commit workflow must re-check the live diff.

## Template

```markdown
<type>: <short imperative phrase>

<Imperative paragraph describing the main change.>

<Imperative paragraph describing related docs, tests, or workflow updates.>

<Optional imperative paragraph for important constraints or follow-through.>

Co-authored-by: Codex <242516109+Codex@users.noreply.github.com>
```
