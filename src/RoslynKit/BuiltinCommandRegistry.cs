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
            "List projects and repository-relevant documents in the inferred repository or explicit target.",
            ["roslynkit workspace [--target <solution.slnx|solution.sln|solution.slnf|project.csproj|repository>] [--include-generated] [--include-additional] [--include-analyzer-config]"],
            [
                TargetOption(),
                OptionSpec.Flag(null, "include-generated", "include source-generated and generated source documents"),
                OptionSpec.Flag(null, "include-additional", "include additional files"),
                OptionSpec.Flag(null, "include-analyzer-config", "include analyzer config documents such as .editorconfig"),
            ]),
        new BuiltinCommand(
            "diagnostics",
            "Return source compiler diagnostics for the loaded target.",
            ["roslynkit diagnostics [--target <target>] [--max-results <n>] [--include-hidden] [--include-generated]"],
            [
                TargetOption(),
                MaxResultsOption(),
                OptionSpec.Flag(null, "include-hidden", "include hidden diagnostics"),
                OptionSpec.Flag(null, "include-generated", "include diagnostics from generated, bin, and obj documents"),
            ]),
        new BuiltinCommand(
            "index",
            "Build or refresh the repository-local search and semantic catalog.",
            ["roslynkit index [--target <target>] [--index-path <path>] [--rebuild] [--text-only]"],
            [
                TargetOption(),
                IndexPathOption(),
                OptionSpec.Flag(null, "rebuild", "discard the selected partition before indexing"),
                OptionSpec.Flag(null, "text-only", "index repository C# source in-process without loading MSBuild"),
            ]),
        new BuiltinCommand(
            "search",
            "Search the repository-local C# catalog using English-oriented text matching and ranking.",
            ["roslynkit search --query <text> [--target <target>] [--index-path <path>] [--project <path>] [--kind <kind>] [--max-results <n>] [--text-only] [--compact] [--balanced]"],
            [
                TargetOption(),
                IndexPathOption(),
                OptionSpec.String('q', "query", "text", "English-oriented text to search for", required: true),
                SearchProjectOption(),
                SymbolKindOption(),
                MaxResultsOption(),
                OptionSpec.Flag(null, "text-only", "search repository C# source in-process without loading MSBuild"),
                OptionSpec.Flag(null, "compact", "emit concise ranked evidence with repository-relative locations"),
                OptionSpec.Flag(null, "balanced", "reserve half of bounded results for focused test declarations when both source and tests match"),
            ]),
        new BuiltinCommand(
            "symbols",
            "Search source declarations by symbol name.",
            ["roslynkit symbols --query <text> [--target <target>] [--max-results <n>] [--case-sensitive] [--exact] [--kind <kind>]"],
            [
                TargetOption(),
                OptionSpec.String('q', "query", "text", "symbol name text to search for", required: true),
                MaxResultsOption(),
                OptionSpec.Flag(null, "case-sensitive", "match query text case-sensitively"),
                OptionSpec.Flag(null, "exact", "match the declaration name exactly"),
                SymbolKindOption(),
            ]),
        new BuiltinCommand(
            "document-text",
            "Read the full text of one resolved document.",
            ["roslynkit document-text --file <path> [--target <target>] [--project <path>] [--tfm <framework>] [--document-kind <kind>]"],
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
            ["roslynkit document-lines --file <path> --start-line <n> --end-line <n> [--target <target>] [--project <path>] [--tfm <framework>] [--document-kind <kind>]"],
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
            ["roslynkit document-symbols --file <path> [--target <target>] [--project <path>] [--tfm <framework>] [--document-kind <kind>]"],
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
                "roslynkit definition --file <path> --line <n> --column <n> [--target <target>] [--project <path>] [--tfm <framework>] [--document-kind <kind>]",
                "roslynkit definition --symbol <selector> [--target <target>]",
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
            ["roslynkit type-definition --file <path> --line <n> --column <n> [--target <target>] [--project <path>] [--tfm <framework>] [--document-kind <kind>]"],
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
                "roslynkit references --file <path> --line <n> --column <n> [--target <target>] [--project <path>] [--tfm <framework>] [--document-kind <kind>] [--max-results <n>]",
                "roslynkit references --symbol <selector> [--target <target>] [--max-results <n>]",
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
                "roslynkit implementations --file <path> --line <n> --column <n> [--target <target>] [--project <path>] [--tfm <framework>] [--document-kind <kind>] [--max-results <n>]",
                "roslynkit implementations --symbol <selector> [--target <target>] [--max-results <n>]",
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
            "symbol-context",
            "Return the local syntax node, resolved symbol, ordinary comments, and bounded semantic context.",
            [
                "roslynkit symbol-context --file <path> --line <n> --column <n> [--target <target>] [--project <path>] [--tfm <framework>] [--document-kind <kind>] [--max-results <n>] [--max-comments <n>]",
                "roslynkit symbol-context --symbol <selector> [--target <target>] [--max-results <n>] [--max-comments <n>]",
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
                SymbolContextMaxResultsOption(),
                MaxCommentsOption(),
            ]),
        new BuiltinCommand(
            "quick-info",
            "Return Roslyn quick info for the symbol at a one-based line and column.",
            ["roslynkit quick-info --file <path> --line <n> --column <n> [--target <target>] [--project <path>] [--tfm <framework>] [--document-kind <kind>]"],
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
            ["roslynkit signature-help --file <path> --line <n> --column <n> [--target <target>] [--project <path>] [--tfm <framework>] [--document-kind <kind>]"],
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
            ["roslynkit symbol-source --symbol <selector> [--target <target>]"],
            [
                TargetOption(),
                SymbolOption(required: true),
            ]),
    ];

    private static readonly IReadOnlyDictionary<string, BuiltinCommand> Lookup =
        Builtins.ToDictionary(command => command.Name, StringComparer.Ordinal);

    private static readonly BuiltinCommand[] ResolutionOrder =
        Builtins.OrderByDescending(command => command.Path.Count).ToArray();

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

    /// <summary>
    /// Resolves the longest registered command path at the requested token offset.
    /// </summary>
    internal static (BuiltinCommand Command, int TokenCount)? GetLongestPrefix(
        IReadOnlyList<string> args,
        int startIndex = 0)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentOutOfRangeException.ThrowIfNegative(startIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(startIndex, args.Count);

        foreach (var command in ResolutionOrder)
        {
            if (command.Path.Count > args.Count - startIndex)
            {
                continue;
            }

            var matches = true;
            for (var pathIndex = 0; pathIndex < command.Path.Count; pathIndex++)
            {
                if (!string.Equals(command.Path[pathIndex], args[startIndex + pathIndex], StringComparison.Ordinal))
                {
                    matches = false;
                    break;
                }
            }

            if (matches)
            {
                return (command, command.Path.Count);
            }
        }

        return null;
    }

    private static OptionSpec TargetOption()
    {
        return OptionSpec.String('t', "target", "target", "optional solution, project, or repository-directory scope; defaults to the nearest repository");
    }

    private static OptionSpec FileOption()
    {
        return OptionSpec.String('f', "file", "path", "document file path in the loaded target");
    }

    private static OptionSpec IndexPathOption()
    {
        return OptionSpec.String(null, "index-path", "path", "optional SQLite database override; defaults to .roslynkit/roslynkit.db");
    }

    private static OptionSpec ProjectOption()
    {
        return OptionSpec.String(null, "project", "path", "owning project file path when a document path is ambiguous");
    }

    private static OptionSpec SearchProjectOption()
    {
        return OptionSpec.String(null, "project", "path", "limit search to one project file within the loaded target");
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
        return OptionSpec.Integer(null, "max-results", "n", $"maximum results to return (default: {CommandDefaults.MaxResults})");
    }

    private static OptionSpec SymbolContextMaxResultsOption()
    {
        return OptionSpec.Integer(null, "max-results", "n", $"maximum semantic descendants to return (default: {CommandDefaults.MaxResults})");
    }

    private static OptionSpec MaxCommentsOption()
    {
        return OptionSpec.Integer(null, "max-comments", "n", "maximum ordinary comments to return (default: 3)");
    }

    private static OptionSpec SymbolKindOption()
    {
        return OptionSpec.String(null, "kind", "kind", "filter symbols by kind: namespace, type, member, method, property, field, event, class, interface, struct, enum, delegate");
    }

}
