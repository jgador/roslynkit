namespace RoslynKit;

/// <summary>
/// Resolves a tool invocation against an explicit repository without changing the server's current directory.
/// </summary>
internal sealed record McpQuery(string RepositoryRoot, string? TargetPath, bool AutomaticRestore, string[] Args, string CommandName)
{
    private static readonly string[] PathOptions = ["target", "file", "project", "index-path"];

    public static McpQuery Create(string repositoryRoot, IReadOnlyList<string> args, bool automaticRestore = true)
    {
        var root = RepositoryContextResolver.ResolveExplicitRoot(repositoryRoot).RootPath;
        if (args.Count == 0)
        {
            throw new CliUsageException("query", "Supply an operation in 'args'; call the help tool to discover operations.");
        }

        if (args[0] is "init" or "serve" or "--repository-worker")
        {
            throw new CliUsageException("query", $"Operation '{args[0]}' is not available through the query tool.");
        }

        var command = ParseCommand(args);
        var options = new Dictionary<string, string>(command.Options, StringComparer.Ordinal);
        foreach (var name in PathOptions)
        {
            if (command.Optional(name) is { } path)
            {
                options[name] = ResolvePath(root, path);
            }
        }

        var target = options.GetValueOrDefault("target");
        if (string.Equals(root, target, PathComparison))
        {
            target = null;
            options.Remove("target");
        }

        var restore = automaticRestore && command.Optional("restore") != "false";
        var normalized = command.IsHelp
            ? new List<string>(["help", .. command.HelpSubject?.Path ?? []])
            : new List<string>(command.Name.Split(' '));
        foreach (var (name, value) in options)
        {
            var option = command.Builtin?.Options.FirstOrDefault(candidate => candidate.LongName == name);
            if (option?.Kind == OptionKind.Flag || name == "restore")
            {
                normalized.Add(value == "false" ? $"--no-{name}" : $"--{name}");
            }
            else
            {
                normalized.Add($"--{name}");
                normalized.Add(value);
            }
        }

        return new McpQuery(root, target, restore, normalized.ToArray(), command.Name);
    }

    public static ParsedCommand ParseCommand(IReadOnlyList<string> args)
    {
        if (args.Count == 0 || args[0] != "refresh")
        {
            return CliParser.Parse(args);
        }

        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 1; index < args.Count; index++)
        {
            var token = args[index];
            if (token == "--target" && index + 1 < args.Count && !options.ContainsKey("target"))
            {
                options.Add("target", args[++index]);
            }
            else if (token == "--no-restore" && !options.ContainsKey("restore"))
            {
                options.Add("restore", "false");
            }
            else
            {
                throw new CliUsageException("refresh", "Usage: query args [\"refresh\", optional \"--target\", \"path\", optional \"--no-restore\"].");
            }
        }

        return new ParsedCommand("refresh", null, options, null);
    }

    private static string ResolvePath(string root, string path)
    {
        var resolved = Path.GetFullPath(path, root);
        if (!IsWithin(root, resolved))
        {
            throw new CliUsageException("query", $"Path '{path}' is outside repository '{root}'.");
        }

        // Resolve existing ancestors as well as the leaf so a not-yet-created catalog cannot escape through a link.
        var relative = Path.GetRelativePath(root, resolved);
        var current = root;
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (segment == ".")
            {
                continue;
            }

            current = Path.Combine(current, segment);
            FileSystemInfo item = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (item.LinkTarget is not null)
            {
                current = item.ResolveLinkTarget(returnFinalTarget: true)?.FullName
                    ?? throw new CliUsageException("query", $"Cannot resolve linked path '{path}'.");
            }

            if (!IsWithin(root, current))
            {
                throw new CliUsageException("query", $"Path '{path}' resolves outside repository '{root}'.");
            }
        }

        return current;
    }

    private static bool IsWithin(string root, string path)
    {
        return string.Equals(root, path, PathComparison)
            || path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, PathComparison);
    }

    internal static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
