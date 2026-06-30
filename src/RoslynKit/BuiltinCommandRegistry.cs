namespace RoslynKit;

/// <summary>
/// Owns the authoritative built-in command table used by parser binding and help output.
/// </summary>
public static class BuiltinCommandRegistry
{
    private static readonly BuiltinCommand[] BaseBuiltins =
    [
        new BuiltinCommand(
            "version",
            "Print the installed RoslynKit version.",
            ["roslynkit version", "roslynkit --version"],
            []),
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
            ["roslynkit document-text --target <target> (--file <path> | --document-key <id>)"],
            [
                TargetOption(),
                FileOption(),
                DocumentKeyOption(),
            ]),
        new BuiltinCommand(
            "document-symbols",
            "List declared symbols in one source or source-generated C# document.",
            ["roslynkit document-symbols --target <target> (--file <path> | --document-key <id>)"],
            [
                TargetOption(),
                FileOption(),
                DocumentKeyOption(),
            ]),
        new BuiltinCommand(
            "definition",
            "Resolve the symbol at a one-based line and column to source definitions.",
            ["roslynkit definition --target <target> (--file <path> | --document-key <id>) --line <n> --column <n>"],
            [
                TargetOption(),
                FileOption(),
                DocumentKeyOption(),
                LineOption(),
                ColumnOption(),
            ]),
        new BuiltinCommand(
            "type-definition",
            "Resolve the type of the symbol at a one-based line and column to source definitions.",
            ["roslynkit type-definition --target <target> (--file <path> | --document-key <id>) --line <n> --column <n>"],
            [
                TargetOption(),
                FileOption(),
                DocumentKeyOption(),
                LineOption(),
                ColumnOption(),
            ]),
        new BuiltinCommand(
            "references",
            "Find source references for the symbol at a one-based line and column.",
            ["roslynkit references --target <target> (--file <path> | --document-key <id>) --line <n> --column <n> [--max-results <n>]"],
            [
                TargetOption(),
                FileOption(),
                DocumentKeyOption(),
                LineOption(),
                ColumnOption(),
                MaxResultsOption(),
            ]),
        new BuiltinCommand(
            "implementations",
            "Find implementations for the symbol at a one-based line and column.",
            ["roslynkit implementations --target <target> (--file <path> | --document-key <id>) --line <n> --column <n> [--max-results <n>]"],
            [
                TargetOption(),
                FileOption(),
                DocumentKeyOption(),
                LineOption(),
                ColumnOption(),
                MaxResultsOption(),
            ]),
        new BuiltinCommand(
            "quick-info",
            "Return Roslyn quick info for the symbol at a one-based line and column.",
            ["roslynkit quick-info --target <target> (--file <path> | --document-key <id>) --line <n> --column <n>"],
            [
                TargetOption(),
                FileOption(),
                DocumentKeyOption(),
                LineOption(),
                ColumnOption(),
            ]),
        new BuiltinCommand(
            "signature-help",
            "Return Roslyn signature help for the position at a one-based line and column.",
            ["roslynkit signature-help --target <target> (--file <path> | --document-key <id>) --line <n> --column <n>"],
            [
                TargetOption(),
                FileOption(),
                DocumentKeyOption(),
                LineOption(),
                ColumnOption(),
            ]),
    ];

    private static readonly BuiltinCommand[] Builtins = BaseBuiltins
        .Select(command => string.Equals(command.Name, "version", StringComparison.Ordinal)
            ? command
            : command with { Options = [.. command.Options, FormatOption()] })
        .ToArray();

    private static readonly IReadOnlyDictionary<string, BuiltinCommand> Lookup =
        Builtins.ToDictionary(command => command.Name, StringComparer.Ordinal);

    public static IReadOnlyList<BuiltinCommand> Commands => Builtins;

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

    private static OptionSpec DocumentKeyOption()
    {
        return OptionSpec.String(null, "document-key", "id", "opaque document key from the workspace command");
    }

    private static OptionSpec LineOption()
    {
        return OptionSpec.Integer(null, "line", "n", "one-based source line", required: true);
    }

    private static OptionSpec ColumnOption()
    {
        return OptionSpec.Integer(null, "column", "n", "one-based source column", required: true);
    }

    private static OptionSpec MaxResultsOption()
    {
        return OptionSpec.Integer(null, "max-results", "n", "maximum results to return");
    }

    private static OptionSpec FormatOption()
    {
        return OptionSpec.String(null, "format", "format", "output format: json (default) or compact");
    }
}
