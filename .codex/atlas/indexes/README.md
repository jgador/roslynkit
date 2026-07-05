# Atlas Indexes

`rebuild-atlas.ps1` writes compact JSON indexes here.

- Schemas in this folder are the stable contract.
- Generated JSON is for deterministic routing, not for prose summaries.
- Raw source, tests, docs, and scripts remain the source of truth.
- Expected generated files: `file-index.json`, `project-index.json`, `test-index.json`, and `symbol-index.json` when RoslynKit is available.
- `symbol-index.json` schemaVersion 2 is parsed from RoslynKit's markdown output: each symbol row carries `name` (the fully qualified display name), `kind`, `displayName`, `line`, and `column`; the JSON-era `containingType` and `containingNamespace` fields are no longer emitted.
