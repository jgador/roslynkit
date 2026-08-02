namespace RoslynKit;

/// <summary>
/// Identifies the semantic backend selected from an explicit workspace target.
/// </summary>
internal enum WorkspaceTargetKind
{
    CSharp,
    TypeScript,
}

/// <summary>
/// Selects the language backend solely from the canonical target file name.
/// </summary>
internal static class WorkspaceTarget
{
    public static WorkspaceTargetKind Resolve(string targetPath, string commandName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        var fileName = Path.GetFileName(targetPath);
        if (fileName.Equals("tsconfig.json", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("jsconfig.json", StringComparison.OrdinalIgnoreCase))
        {
            return WorkspaceTargetKind.TypeScript;
        }

        var extension = Path.GetExtension(targetPath);
        if (extension.Equals(".slnx", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".sln", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            return WorkspaceTargetKind.CSharp;
        }

        throw new CliUsageException(
            commandName,
            "The '--target' value must name an existing .slnx, .sln, .csproj, tsconfig.json, or jsconfig.json file.");
    }
}
