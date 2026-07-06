#:project ../src/RoslynKit/RoslynKit.csproj

using System.Text;
using RoslynKit;

return CommandDocsProgram.Run(args);

internal static class CommandDocsProgram
{
    public static int Run(string[] args)
    {
        if (args.Length != 1 || args[0] is not "--write" and not "--check")
        {
            Console.Error.WriteLine("Usage: dotnet run --file .\\tools\\RoslynKit.CommandDocs.cs -- [--write|--check]");
            return 2;
        }

        var root = FindRepositoryRoot(Directory.GetCurrentDirectory());
        var outputPath = Path.Combine(root, CommandReferenceMarkdown.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        var generated = CommandReferenceMarkdown.Render();

        if (args[0] == "--write")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            File.WriteAllText(outputPath, generated, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            Console.WriteLine($"Wrote {CommandReferenceMarkdown.RelativePath}");
            return 0;
        }

        if (!File.Exists(outputPath))
        {
            Console.Error.WriteLine($"{CommandReferenceMarkdown.RelativePath} is missing. Run `dotnet run --file .\\tools\\RoslynKit.CommandDocs.cs -- --write`.");
            return 1;
        }

        var actual = File.ReadAllText(outputPath);
        if (!string.Equals(NormalizeNewlines(actual), NormalizeNewlines(generated), StringComparison.Ordinal))
        {
            Console.Error.WriteLine($"{CommandReferenceMarkdown.RelativePath} is stale. Run `dotnet run --file .\\tools\\RoslynKit.CommandDocs.cs -- --write`.");
            return 1;
        }

        Console.WriteLine($"{CommandReferenceMarkdown.RelativePath} is up to date.");
        return 0;
    }

    private static string FindRepositoryRoot(string startDirectory)
    {
        for (var directory = new DirectoryInfo(startDirectory); directory is not null; directory = directory.Parent)
        {
            var solutionPath = Path.Combine(directory.FullName, "RoslynKit.slnx");
            var registryPath = Path.Combine(directory.FullName, "src", "RoslynKit", "BuiltinCommandRegistry.cs");
            if (File.Exists(solutionPath) && File.Exists(registryPath))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Could not locate the RoslynKit repository root.");
    }

    private static string NormalizeNewlines(string value)
    {
        return value.Replace("\r\n", "\n", StringComparison.Ordinal);
    }
}
