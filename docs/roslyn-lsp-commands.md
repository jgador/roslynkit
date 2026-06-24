# Roslyn Language Server Command Inventory

This inventory captures the command-like surfaces found in the local Roslyn checkout at `C:\repo\GitHub\roslyn`, commit `398e4d9cf54`.

Roslyn does not have a CLI command registry equivalent to RoslynKit's `BuiltinCommandRegistry`. For RoslynKit planning, the closest exhaustive command surface is the Roslyn language-server protocol handler table under `src\LanguageServer\Protocol\Handler`, plus LSP `Command` identifiers emitted by handlers.

Use this document as reference material when deciding which Roslyn capabilities RoslynKit may expose as CLI commands. RoslynKit is intended to give coding agents C# language intelligence through a deterministic CLI and accompanying `SKILL.md`; it is not a design commitment to make RoslynKit a JSON-RPC LSP client or server.

## Priority Model

Priorities are assigned for RoslynKit as a C#-specific CLI used by coding agents such as Codex, Claude Code, and Copilot.

- `Implemented`: already exposed by RoslynKit and should remain stable.
- `P0`: core read-only intelligence needed for a useful first agent skill.
- `P1`: high-value edit, refactor, or deeper navigation support after the P0 surface is stable.
- `P2`: useful for specialized workflows, but not required for the first strong agent-facing CLI.
- `Defer`: LSP/editor lifecycle, client UI, Visual Studio-specific, or duplicate protocol plumbing that does not make sense as a direct RoslynKit command.

## Recommended RoslynKit Roadmap

| Priority | RoslynKit command family | Roslyn method or API source | Why it matters for agents |
| --- | --- | --- | --- |
| Implemented | `workspace` | `MSBuildWorkspace`, solution/project/document APIs | Establishes the loaded C# workspace and source set. |
| Implemented | `diagnostics` | compiler diagnostics, `textDocument/diagnostic`, `workspace/diagnostic` | Lets agents verify build/compiler issues before and after edits. |
| Implemented | `symbols` | `workspace/symbol`, `SymbolFinder`, declarations | Lets agents locate named C# declarations without text-only search. |
| Implemented | `document-symbols` | `textDocument/documentSymbol` | Gives compact structure for one file. |
| Implemented | `definition` | `textDocument/definition`, `FindSourceDefinitionAsync` | Essential go-to-definition behavior. |
| Implemented | `references` | `textDocument/references`, `FindReferencesAsync` | Essential impact analysis before changing a symbol. |
| P0 | `type-definition` | `textDocument/typeDefinition` | Useful when the symbol usage points at a variable, property, or interface abstraction. |
| P0 | `implementations` | `textDocument/implementation`, `FindImplementationsAsync` | Critical for interface, abstract member, and override navigation. |
| P0 | `quick-info` | `textDocument/hover`, QuickInfo services | Gives agents exact type, signature, and documentation context at a position. |
| P0 | `signature-help` | `textDocument/signatureHelp` | Helps agents call overloaded C# APIs correctly. |
| P1 | `completion` | `textDocument/completion`, `completionItem/resolve` | Useful for member discovery, importable symbols, and API exploration. |
| P1 | `code-actions` | `textDocument/codeAction`, `codeAction/resolve`, `codeAction/resolveFixAll` | Enables structured fixes such as add using, apply analyzer fix, and fix-all workflows. Prefer previewing edits before applying them. |
| P1 | `format` | `textDocument/formatting`, `textDocument/rangeFormatting` | Lets agents normalize C# edits without relying on editor integration. |
| P1 | `rename` | `textDocument/prepareRename`, `textDocument/rename`, `Renamer` | Enables safe symbol and document rename workflows. |
| P1 | `document-highlights` | `textDocument/documentHighlight` | Helps agents understand local symbol usage inside a file. |
| P1 | `call-hierarchy` | `textDocument/prepareCallHierarchy`, `callHierarchy/incomingCalls`, `callHierarchy/outgoingCalls` | Useful for tracing call flow and impact in service-style C# code. |
| P1 | `type-hierarchy` | `textDocument/prepareTypeHierarchy`, `typeHierarchy/supertypes`, `typeHierarchy/subtypes` | Useful for inheritance, interface, and framework-extension analysis. |
| P1 | `source-generators` | `workspace/_roslyn_refreshSourceGenerators`, source generated document APIs | Important for modern C# projects where generated code affects symbols and diagnostics. |
| P1 | `generated-documents` | `workspace/textDocumentContent`, source generated document APIs | Gives agents access to generated or virtual source when needed for accurate reasoning. |
| P2 | `semantic-tokens` | `textDocument/semanticTokens/full`, `textDocument/semanticTokens/range` | Can expose semantic classification, but agents usually need symbols and diagnostics first. |
| P2 | `folding-ranges` | `textDocument/foldingRange` | Mostly editor UI, but can support coarse document chunking. |
| P2 | `selection-ranges` | `textDocument/selectionRange` | Can help structure-aware selection/edit planning, but lower value than symbols and ranges from syntax. |
| P2 | `inlay-hints` | `textDocument/inlayHint`, `inlayHint/resolve` | Useful for inferred parameter/type hints, but not foundational. |
| P2 | `code-lens` | `textDocument/codeLens`, `codeLens/resolve` | Potentially useful for tests and references, but current command identifiers are editor/client oriented. |
| P2 | `project-contexts` | `textDocument/_vs_getProjectContexts` | Useful for multi-targeted or shared documents, but Visual Studio-specific as exposed through LSP. |
| Defer | lifecycle and synchronization | `initialize`, `initialized`, `textDocument/didOpen`, `textDocument/didChange`, `textDocument/didSave`, `textDocument/didClose` | CLI commands load files and targets directly; editor synchronization does not map cleanly. |
| Defer | workspace execute command plumbing | `workspace/executeCommand` | RoslynKit should expose explicit subcommands instead of a generic LSP command dispatcher. |
| Defer | Visual Studio extension methods | `_vs_*` extension activation, dispatch, spell check, data tips, map code, breakable ranges | Mostly client/editor/VS-specific behavior rather than C# code-intelligence CLI primitives. |
| Defer | client command identifiers | `roslyn.client.*`, `dotnet.test.run`, `_ms_*`, `csharp.showOutputWindow` | These are commands for clients to execute, not RoslynKit server-side intelligence commands. |

## RoslynKit Commands Today

RoslynKit currently registers these commands:

- `workspace`
- `diagnostics`
- `symbols`
- `document-symbols`
- `definition`
- `references`

## Implemented Roslyn LSP Methods

These method names have handlers in `C:\repo\GitHub\roslyn\src\LanguageServer\Protocol\Handler`.

### Server Lifecycle

- `initialize` - `Defer`
- `initialized` - `Defer`
- `window/workDoneProgress/cancel` - `Defer`

### Document Synchronization

- `textDocument/didOpen` - `Defer`
- `textDocument/didChange` - `Defer`
- `textDocument/didSave` - `Defer`
- `textDocument/didClose` - `Defer`

### Diagnostics

- `textDocument/diagnostic` - `Implemented`
- `workspace/diagnostic` - `Implemented`

### Workspace

- `workspace/didChangeConfiguration` - `Defer`
- `workspace/executeCommand` - `Defer`
- `workspace/symbol` - `Implemented`
- `workspace/textDocumentContent` - `P1`
- `workspace/willRenameFiles` - `P1`

### Navigation And Symbols

- `textDocument/definition` - `Implemented`
- `textDocument/typeDefinition` - `P0`
- `textDocument/implementation` - `P0`
- `textDocument/references` - `Implemented`
- `textDocument/documentSymbol` - `Implemented`
- `textDocument/documentHighlight` - `P1`

### Hierarchy

- `textDocument/prepareCallHierarchy` - `P1`
- `callHierarchy/incomingCalls` - `P1`
- `callHierarchy/outgoingCalls` - `P1`
- `textDocument/prepareTypeHierarchy` - `P1`
- `typeHierarchy/supertypes` - `P1`
- `typeHierarchy/subtypes` - `P1`

### Editor Intelligence

- `textDocument/hover` - `P0`
- `textDocument/completion` - `P1`
- `completionItem/resolve` - `P1`
- `textDocument/signatureHelp` - `P0`
- `textDocument/codeAction` - `P1`
- `codeAction/resolve` - `P1`
- `codeAction/resolveFixAll` - `P1`
- `textDocument/codeLens` - `P2`
- `codeLens/resolve` - `P2`

### Editing

- `textDocument/formatting` - `P1`
- `textDocument/rangeFormatting` - `P1`
- `textDocument/onTypeFormatting` - `P2`
- `textDocument/prepareRename` - `P1`
- `textDocument/rename` - `P1`

### Document Structure And Presentation

- `textDocument/selectionRange` - `P2`
- `textDocument/foldingRange` - `P2`
- `textDocument/inlayHint` - `P2`
- `inlayHint/resolve` - `P2`
- `textDocument/semanticTokens/full` - `P2`
- `textDocument/semanticTokens/range` - `P2`

## Implemented Roslyn And Visual Studio Custom Methods

These method names also have Roslyn handlers, but they are Roslyn-specific or Visual Studio-specific protocol extensions.

### Roslyn Custom Methods

- `workspace/_roslyn_refreshSourceGenerators` - `P1`
- `workspace/buildOnlyDiagnosticIds` - `P2`
- `workspace/waitForAsyncOperations` - `Defer`

### Visual Studio Project And Feature Methods

- `textDocument/_vs_getProjectContexts` - `P2`
- `workspace/featureProviders/_vs_refresh` - `Defer`

### Visual Studio Diagnostics And Mapping

- `textdocument/_vs_diagnostic` - `Defer`
- `workspace/_vs_diagnostic` - `Defer`
- `workspace/_vs_mapCode` - `Defer`

### Visual Studio Document Features

- `textDocument/_vs_spellCheckableRanges` - `Defer`
- `workspace/_vs_spellCheckableRanges` - `Defer`
- `textDocument/_vs_inlineCompletion` - `Defer`
- `textDocument/_vs_onAutoInsert` - `Defer`
- `textDocument/_vs_validateBreakableRange` - `Defer`
- `textdocument/_vs_dataTipRange` - `Defer`

### Visual Studio Extension And Snapshot Methods

- `workspace/_vs_registerSolutionSnapshot` - `Defer`
- `server/_vs_activateExtension` - `Defer`
- `server/_vs_deactivateExtension` - `Defer`
- `workspace/_vs_dispatchExtensionMessage` - `Defer`
- `textDocument/_vs_dipatchExtensionMessage` - `Defer`

## LSP Command Identifiers

These are `Command.CommandIdentifier` values emitted by the Roslyn language server or declared as well-known client commands. Most are client-side actions, not server request methods.

- `Roslyn.RunCodeAction` - `Defer`
- `roslyn.client.fixAllCodeAction` - `Defer`
- `roslyn.client.nestedCodeAction` - `Defer`
- `roslyn.client.peekReferences` - `Defer`
- `roslyn.client.completionComplexEdit` - `Defer`
- `dotnet.test.run` - `Defer`
- `csharp.showOutputWindow` - `Defer`
- `_ms_setClipboard` - `Defer`
- `_ms_openUrl` - `Defer`

## High-Value RoslynKit Gaps

These Roslyn command families are not currently exposed by RoslynKit and are plausible future CLI commands.

### P0 Gaps

- type definition
- implementation search
- hover or quick info
- signature help

### P1 Gaps

- completion
- code actions
- formatting
- rename
- document highlights
- call hierarchy
- type hierarchy
- source generators
- generated or virtual document content

### P2 Gaps

- folding range
- selection range
- inlay hints
- semantic tokens

## Verification Notes

The inventory was produced by searching the Roslyn language-server handler and protocol constant surfaces:

```powershell
rg -n "\[Method\(|ProvidesCommand\(|CommandIdentifier|WorkspaceExecuteCommandName" C:\repo\GitHub\roslyn\src\LanguageServer
rg -n "const string .*Name = " C:\repo\GitHub\roslyn\src\LanguageServer\Protocol\Protocol
```

High-signal source areas:

- `C:\repo\GitHub\roslyn\src\LanguageServer\Protocol\Handler`
- `C:\repo\GitHub\roslyn\src\LanguageServer\Protocol\Protocol`
- `C:\repo\GitHub\roslyn\src\Features`
- `C:\repo\GitHub\roslyn\src\Workspaces`
