namespace RoslynKit;

public static class BuiltinCommandRegistry
{
    private static readonly BuiltinCommand[] Builtins =
    [
        new BuiltinCommand(
            "workspace",
            "List projects and source documents loaded from a solution or project.",
            ["roslynkit workspace --target <solution.slnx|solution.sln|project.csproj> [--include-generated]"],
            [
                OptionSpec.String('t', "target", "target", "solution or project file to load", required: true),
                OptionSpec.Flag(null, "include-generated", "include generated and obj documents"),
            ]),
        new BuiltinCommand(
            "diagnostics",
            "Return source compiler diagnostics for the loaded target.",
            ["roslynkit diagnostics --target <target> [--max-results <n>] [--include-hidden] [--include-generated]"],
            [
                OptionSpec.String('t', "target", "target", "solution or project file to load", required: true),
                OptionSpec.Integer(null, "max-results", "n", "maximum diagnostics to return"),
                OptionSpec.Flag(null, "include-hidden", "include hidden diagnostics"),
                OptionSpec.Flag(null, "include-generated", "include diagnostics from generated and obj documents"),
            ]),
        new BuiltinCommand(
            "symbols",
            "Search source declarations by symbol name.",
            ["roslynkit symbols --target <target> --query <text> [--max-results <n>] [--case-sensitive]"],
            [
                OptionSpec.String('t', "target", "target", "solution or project file to load", required: true),
                OptionSpec.String('q', "query", "text", "symbol name text to search for", required: true),
                OptionSpec.Integer(null, "max-results", "n", "maximum symbols to return"),
                OptionSpec.Flag(null, "case-sensitive", "match query text case-sensitively"),
            ]),
        new BuiltinCommand(
            "document-symbols",
            "List declared symbols in a single source document.",
            ["roslynkit document-symbols --target <target> --file <path>"],
            [
                OptionSpec.String('t', "target", "target", "solution or project file to load", required: true),
                OptionSpec.String('f', "file", "path", "source file in the loaded target", required: true),
            ]),
        new BuiltinCommand(
            "definition",
            "Resolve the symbol at a one-based line and column to source definitions.",
            ["roslynkit definition --target <target> --file <path> --line <n> --column <n>"],
            [
                OptionSpec.String('t', "target", "target", "solution or project file to load", required: true),
                OptionSpec.String('f', "file", "path", "source file in the loaded target", required: true),
                OptionSpec.Integer(null, "line", "n", "one-based source line", required: true),
                OptionSpec.Integer(null, "column", "n", "one-based source column", required: true),
            ]),
        new BuiltinCommand(
            "references",
            "Find source references for the symbol at a one-based line and column.",
            ["roslynkit references --target <target> --file <path> --line <n> --column <n> [--max-results <n>]"],
            [
                OptionSpec.String('t', "target", "target", "solution or project file to load", required: true),
                OptionSpec.String('f', "file", "path", "source file in the loaded target", required: true),
                OptionSpec.Integer(null, "line", "n", "one-based source line", required: true),
                OptionSpec.Integer(null, "column", "n", "one-based source column", required: true),
                OptionSpec.Integer(null, "max-results", "n", "maximum references to return"),
            ]),
    ];

    private static readonly IReadOnlyDictionary<string, BuiltinCommand> Lookup =
        Builtins.ToDictionary(command => command.Name, StringComparer.Ordinal);

    public static IReadOnlyList<BuiltinCommand> Commands => Builtins;

    public static BuiltinCommand? GetBuiltin(string name)
    {
        return Lookup.TryGetValue(name, out var command) ? command : null;
    }
}
