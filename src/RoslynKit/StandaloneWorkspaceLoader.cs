namespace RoslynKit;

/// <summary>
/// Prepares repository dependencies and the selected SDK before a short-lived semantic command loads Roslyn.
/// </summary>
internal static class StandaloneWorkspaceLoader
{
    public static async Task<RoslynWorkspaceLoader> LoadAsync(
        ParsedCommand command,
        CancellationToken cancellationToken)
    {
        var targetPath = command.Optional("target");
        var filePath = command.Optional("file");
        if (targetPath is not null && !File.Exists(targetPath) && !Directory.Exists(targetPath))
        {
            throw new CliUsageException(command.Name,
                $"The '--target' path '{Path.GetFullPath(targetPath)}' does not exist. Pass an existing solution, project, or repository directory.");
        }

        var repository = RepositoryContextResolver.Resolve(targetPath ?? filePath);
        var preparation = await WorkspacePreparation.PrepareAsync(
            repository.RootPath,
            targetPath is null ? null : Path.GetFullPath(targetPath),
            automaticRestore: command.Optional("restore") != "false",
            cancellationToken).ConfigureAwait(false);
        RoslynWorkspaceLoader.RegisterMSBuild(preparation.Sdk.SdkDirectory);
        return await RoslynWorkspaceLoader.LoadAsync(targetPath, filePath, cancellationToken).ConfigureAwait(false);
    }
}
