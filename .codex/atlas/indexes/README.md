# Atlas Indexes

`rebuild-atlas.ps1` writes compact JSON indexes here.

- Schemas in this folder are the stable contract.
- Generated JSON is for deterministic routing, not for prose summaries.
- Prompt-cached Atlas probes should pass compact selected rows from these indexes in the dynamic suffix and should not treat volatile metadata such as timestamps or tool paths as part of the reusable prefix.
- Raw source, tests, docs, and scripts remain the source of truth.
- Expected generated files: `file-index.json`, `project-index.json`, `test-index.json`, and `symbol-index.json` when RoslynKit is available.
