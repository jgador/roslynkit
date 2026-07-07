namespace RoslynKit;

/// <summary>
/// Owns the authoritative built-in command table used by parser binding and help output.
/// </summary>
public static class BuiltinCommandRegistry
{
    private static readonly BuiltinCommand[] Builtins =
    [
        new BuiltinCommand(
            "version",
            "Print the installed RoslynKit version.",
            ["roslynkit version", "roslynkit --version"],
            []),
        new BuiltinCommand(
            "init",
            "Scaffold the RoslynKit coding-agent skill bundle into the current Git repository.",
            ["roslynkit init [--agent <codex|claude|copilot|all>] [--overwrite]"],
            [
                OptionSpec.String(null, "agent", "agent", "agent target: codex, claude, copilot, or all"),
                OptionSpec.Flag(null, "overwrite", "replace existing scaffolded skill files when content differs"),
            ]),
        new BuiltinCommand(
            "workspace",
            "List projects and repo-relevant documents loaded from a solution or project.",
            ["roslynkit workspace --target <solution.slnx|solution.sln|project.csproj> [--include-generated] [--include-additional] [--include-analyzer-config]"],
            [
                TargetOption(),
                OptionSpec.Flag(null, "include-generated", "include source-generated and generated source documents"),
                OptionSpec.Flag(null, "include-additional", "include additional files"),
                OptionSpec.Flag(null, "include-analyzer-config", "include analyzer config documents such as .editorconfig"),
            ]),
        new BuiltinCommand(
            "diagnostics",
            "Return source compiler diagnostics for the loaded target.",
            ["roslynkit diagnostics --target <target> [--max-results <n>] [--include-hidden] [--include-generated]"],
            [
                TargetOption(),
                MaxResultsOption(),
                OptionSpec.Flag(null, "include-hidden", "include hidden diagnostics"),
                OptionSpec.Flag(null, "include-generated", "include diagnostics from generated and obj documents"),
            ]),
        new BuiltinCommand(
            "symbols",
            "Search source declarations by symbol name.",
            ["roslynkit symbols --target <target> --query <text> [--max-results <n>] [--case-sensitive] [--exact] [--kind <kind>]"],
            [
                TargetOption(),
                OptionSpec.String('q', "query", "text", "symbol name text to search for", required: true),
                MaxResultsOption(),
                OptionSpec.Flag(null, "case-sensitive", "match query text case-sensitively"),
                OptionSpec.Flag(null, "exact", "match the declaration name exactly"),
                OptionSpec.String(null, "kind", "kind", "filter declarations by kind: namespace, type, member, method, property, field, event, class, interface, struct, enum, delegate"),
            ]),
        new BuiltinCommand(
            "document-text",
            "Read the full text of one resolved document.",
            ["roslynkit document-text --target <target> --file <path> [--project <path>] [--tfm <framework>] [--document-kind <kind>]"],
            [
                TargetOption(),
                FileOption(),
                ProjectOption(),
                TargetFrameworkOption(),
                DocumentKindOption(),
            ]),
        new BuiltinCommand(
            "document-lines",
            "Read a bounded one-based line range from one resolved document.",
            ["roslynkit document-lines --target <target> --file <path> [--project <path>] [--tfm <framework>] [--document-kind <kind>] --start-line <n> --end-line <n>"],
            [
                TargetOption(),
                FileOption(),
                ProjectOption(),
                TargetFrameworkOption(),
                DocumentKindOption(),
                StartLineOption(),
                EndLineOption(),
            ]),
        new BuiltinCommand(
            "document-symbols",
            "List declared symbols in one source or source-generated C# document.",
            ["roslynkit document-symbols --target <target> --file <path> [--project <path>] [--tfm <framework>] [--document-kind <kind>]"],
            [
                TargetOption(),
                FileOption(),
                ProjectOption(),
                TargetFrameworkOption(),
                DocumentKindOption(),
            ]),
        new BuiltinCommand(
            "definition",
            "Resolve a symbol selector or the symbol at a one-based line and column to source definitions.",
            [
                "roslynkit definition --target <target> --file <path> [--project <path>] [--tfm <framework>] [--document-kind <kind>] --line <n> --column <n>",
                "roslynkit definition --target <target> --symbol <selector>",
            ],
            [
                TargetOption(),
                FileOption(),
                ProjectOption(),
                TargetFrameworkOption(),
                DocumentKindOption(),
                LineOption(required: false),
                ColumnOption(required: false),
                SymbolOption(),
            ]),
        new BuiltinCommand(
            "type-definition",
            "Resolve the type of the symbol at a one-based line and column to source definitions.",
            ["roslynkit type-definition --target <target> --file <path> [--project <path>] [--tfm <framework>] [--document-kind <kind>] --line <n> --column <n>"],
            [
                TargetOption(),
                FileOption(),
                ProjectOption(),
                TargetFrameworkOption(),
                DocumentKindOption(),
                LineOption(),
                ColumnOption(),
            ]),
        new BuiltinCommand(
            "references",
            "Find source references for a symbol selector or the symbol at a one-based line and column.",
            [
                "roslynkit references --target <target> --file <path> [--project <path>] [--tfm <framework>] [--document-kind <kind>] --line <n> --column <n> [--max-results <n>]",
                "roslynkit references --target <target> --symbol <selector> [--max-results <n>]",
            ],
            [
                TargetOption(),
                FileOption(),
                ProjectOption(),
                TargetFrameworkOption(),
                DocumentKindOption(),
                LineOption(required: false),
                ColumnOption(required: false),
                SymbolOption(),
                MaxResultsOption(),
            ]),
        new BuiltinCommand(
            "implementations",
            "Find implementations for a symbol selector or the symbol at a one-based line and column.",
            [
                "roslynkit implementations --target <target> --file <path> [--project <path>] [--tfm <framework>] [--document-kind <kind>] --line <n> --column <n> [--max-results <n>]",
                "roslynkit implementations --target <target> --symbol <selector> [--max-results <n>]",
            ],
            [
                TargetOption(),
                FileOption(),
                ProjectOption(),
                TargetFrameworkOption(),
                DocumentKindOption(),
                LineOption(required: false),
                ColumnOption(required: false),
                SymbolOption(),
                MaxResultsOption(),
            ]),
        new BuiltinCommand(
            "quick-info",
            "Return Roslyn quick info for the symbol at a one-based line and column.",
            ["roslynkit quick-info --target <target> --file <path> [--project <path>] [--tfm <framework>] [--document-kind <kind>] --line <n> --column <n>"],
            [
                TargetOption(),
                FileOption(),
                ProjectOption(),
                TargetFrameworkOption(),
                DocumentKindOption(),
                LineOption(),
                ColumnOption(),
            ]),
        new BuiltinCommand(
            "signature-help",
            "Return Roslyn signature help for the position at a one-based line and column.",
            ["roslynkit signature-help --target <target> --file <path> [--project <path>] [--tfm <framework>] [--document-kind <kind>] --line <n> --column <n>"],
            [
                TargetOption(),
                FileOption(),
                ProjectOption(),
                TargetFrameworkOption(),
                DocumentKindOption(),
                LineOption(),
                ColumnOption(),
            ]),
        new BuiltinCommand(
            "symbol-source",
            "Return the full declaration source text for a symbol selector.",
            ["roslynkit symbol-source --target <target> --symbol <selector>"],
            [
                TargetOption(),
                SymbolOption(required: true),
            ]),
    ];

    private static readonly IReadOnlyDictionary<string, BuiltinCommand> Lookup =
        Builtins.ToDictionary(command => command.Name, StringComparer.Ordinal);

    /// <summary>
    /// Ordered built-in command metadata used by top-level help and parser lookup.
    /// </summary>
    public static IReadOnlyList<BuiltinCommand> Commands => Builtins;

    /// <summary>
    /// Resolves command metadata by the exact command name accepted on the CLI.
    /// </summary>
    public static BuiltinCommand? GetBuiltin(string name)
    {
        return Lookup.TryGetValue(name, out var command) ? command : null;
    }

    private static OptionSpec TargetOption()
    {
        return OptionSpec.String('t', "target", "target", "solution or project file to load", required: true);
    }

    private static OptionSpec FileOption()
    {
        return OptionSpec.String('f', "file", "path", "document file path in the loaded target");
    }

    private static OptionSpec ProjectOption()
    {
        return OptionSpec.String(null, "project", "path", "owning project file path when a document path is ambiguous");
    }

    private static OptionSpec TargetFrameworkOption()
    {
        return OptionSpec.String(null, "tfm", "framework", "target framework when a document path is ambiguous across project contexts");
    }

    private static OptionSpec DocumentKindOption()
    {
        return OptionSpec.String(null, "document-kind", "kind", "document kind when a path maps to source, sourceGenerated, additional, or analyzerConfig");
    }

    private static OptionSpec LineOption(bool required = true)
    {
        return OptionSpec.Integer(null, "line", "n", "one-based source line", required);
    }

    private static OptionSpec StartLineOption()
    {
        return OptionSpec.Integer(null, "start-line", "n", "one-based first document line", required: true);
    }

    private static OptionSpec EndLineOption()
    {
        return OptionSpec.Integer(null, "end-line", "n", "one-based last document line", required: true);
    }

    private static OptionSpec ColumnOption(bool required = true)
    {
        return OptionSpec.Integer(null, "column", "n", "one-based source column", required);
    }

    private static OptionSpec SymbolOption(bool required = false)
    {
        return OptionSpec.String(null, "symbol", "selector", "documentation-comment ID or qualified symbol name", required);
    }

    private static OptionSpec MaxResultsOption()
    {
        return OptionSpec.Integer(null, "max-results", "n", "maximum results to return");
    }

}
