# TypeScript Native-Preview Backend

RoslynKit supports TypeScript and JavaScript targets through the native Go-based TypeScript compiler preview. A target named `tsconfig.json` or `jsconfig.json` selects this backend; `.slnx`, `.sln`, and `.csproj` continue to select the existing Roslyn backend.

## Prerequisites

- the .NET 10 runtime required by the RoslynKit tool;
- Node.js 16.20 or later;
- `@typescript/native-preview@latest`, installed in the target repository or globally with npm.

Repository-local installation is the most reproducible option:

```powershell
npm install --save-dev @typescript/native-preview@latest
```

Global installation is also supported:

```powershell
npm install --global @typescript/native-preview@latest
```

RoslynKit's own bridge development dependency is resolved and pinned in `src/RoslynKit/TypeScriptBridge/package-lock.json`. Run `npm ci` in that directory before bridge development or tests.

## Runtime discovery

The packaged .NET tool contains `TypeScriptBridge/bridge.mjs`; it does not depend on the RoslynKit source checkout. At runtime RoslynKit resolves:

1. Node from `ROSLYNKIT_NODE_PATH` or `PATH` and validates its version.
2. The packaged bridge beside the installed tool, or `ROSLYNKIT_TYPESCRIPT_BRIDGE_PATH` for development diagnostics.
3. `@typescript/native-preview` from `ROSLYNKIT_TYPESCRIPT_NATIVE_PREVIEW_ROOT`, an ancestor `node_modules` directory for the target or bridge, or `npm root --global`.

The resolved Node version and path, bridge digest, and native-preview package version and root are part of daemon compatibility identity. Changing any of them selects a fresh daemon instead of reusing a process loaded with the old runtime.

Missing prerequisites fail with the command needed to install the package or the override variable needed to select an existing installation. `ROSLYNKIT_NPM_PATH` selects npm when global discovery cannot use the default executable on `PATH`.

## Architecture and lifecycle

The bridge is a maintained JSON-lines Node process that imports exactly:

```javascript
@typescript/native-preview/unstable/sync
@typescript/native-preview/unstable/ast
```

It does not use the legacy JavaScript TypeScript compiler API, TypeScript 6, tree-sitter, or a custom semantic parser.

One daemon session owns one bridge process, one native-preview `API`, one project, and one current snapshot. Repeated commands reuse all four. When the Git fingerprint changes, RoslynKit calls `updateSnapshot` with an all-file invalidation, disposes the replaced snapshot, and retains the Node process, native compiler process, and API instance. Session shutdown disposes the final snapshot, closes the API, and terminates the bridge process tree. Standalone execution creates the same backend for one invocation and disposes it afterward.

## Commands and files

The native backend supports `.ts`, `.tsx`, `.mts`, `.cts`, `.js`, `.jsx`, `.mjs`, and `.cjs` files configured by `tsconfig.json` or `jsconfig.json`.

It implements `workspace`, `diagnostics`, `symbols`, `document-text`, `document-lines`, `document-symbols`, `definition`, `type-definition`, `references`, `implementations`, `quick-info`, `signature-help`, `symbol-source`, `index`, and `search` with the semantics currently exposed by native-preview.

TypeScript and JavaScript declarations use deterministic `ts:` selectors derived from configured source paths and native declaration spans. These selectors round-trip through `definition`, `references`, and `symbol-source`, including selectors returned by `search`. They are deliberately not represented as C# documentation-comment IDs. Refresh a selector from current output after an edit changes declaration spans.

The SQLite search schema stores a language marker on target metadata and symbol rows. Existing version-1 C# indexes migrate in place to schema version 2 with `csharp` defaults; incompatible future schemas fail with rebuild guidance.

## Current preview limitations

- The upstream native API is explicitly unstable. RoslynKit pins a tested resolved package in its lockfile, but a future `@latest` update can require bridge changes.
- Native snapshots expose configured physical source files; Roslyn source-generated, additional-file, analyzer-config, multi-target-framework, and XML-documentation-specific behavior remains C#-only.
- TypeScript implementation discovery combines native type relations with configured heritage clauses. TypeScript's structural type system can make “implementation” broader than nominal C# inheritance.
- TypeScript selectors encode declaration spans and should be reacquired after edits that move or reshape a declaration.
- Signature help returns the native resolved signature available at the requested call site; candidate-list and rich display-part parity with a full editor language service is not yet exposed by the preview API.
- Search refresh for TypeScript currently rebuilds the selected config partition atomically instead of performing Roslyn's project-level incremental refresh planning.
