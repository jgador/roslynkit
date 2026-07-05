# Repository Atlas

The Atlas is the first-stop navigation layer for this repo.

- Markdown files are for compact architecture context and human/agent routing.
- Feature cards are the only hand-maintained Atlas routing layer.
- Atlas has no generated inventory layer.
- Use `git ls-files`, `rg`, build/test output, and direct file inspection for current file, project, and test facts.
- Use RoslynKit live queries for symbols, definitions, references, implementations, quick-info, and source slices.
- Raw source remains the source of truth.
- Atlas updates should capture durable facts only.
- Atlas must not store file, project, test, symbol, reference, or source-slice snapshots.
- Use `.codex/atlas/scripts/route.ps1` only as a cheap cold-start router over markdown and file names.
- Windows and PowerShell are the primary supported workflow for now.
