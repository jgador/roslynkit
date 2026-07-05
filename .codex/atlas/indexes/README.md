# Atlas Indexes

`rebuild-atlas.ps1` writes compact JSON indexes here.

- Schemas in this folder are the stable contract.
- Generated JSON is file/project/test metadata for deterministic routing, not prose summaries or semantic context.
- Raw source, tests, docs, and scripts remain the source of truth.
- Expected generated files: `file-index.json`, `project-index.json`, and `test-index.json`.
- Atlas does not maintain a repo-wide symbol index. Use RoslynKit live commands for symbols, definitions, references, implementations, quick-info, and exact source slices.
