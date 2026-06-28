# Repository Atlas

The Atlas is the first-stop navigation layer for this repo.

- Markdown files are for human and agent routing.
- Feature cards are the only hand-maintained Atlas routing layer.
- JSON files are for deterministic scripts and compact indexes.
- Repeated Atlas queries can use the repo-local prompt-cache probe with a stable prefix made from the lane config, `repo-map.md`, `test-index.md`, and concise feature cards.
- Keep volatile route output and selected generated-index slices in the dynamic suffix, not in the cached prefix.
- Raw source remains the source of truth.
- Atlas updates should capture durable facts only.
- Atlas must not store full source dumps.
- Windows and PowerShell are the primary supported workflow for now.
