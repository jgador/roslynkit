namespace RoslynKit;

/// <summary>
/// Describes the files scaffolded by the init command across one or more agent targets.
/// </summary>
public sealed record InitResult(
    string AgentSelection,
    string RepositoryRoot,
    IReadOnlyList<InitFileResult> Files);

/// <summary>
/// Describes one scaffolded skill-bundle file and whether the command created, preserved, or replaced it.
/// </summary>
public sealed record InitFileResult(
    string Agent,
    string Path,
    InitFileStatus Status);

public enum InitFileStatus
{
    Created,
    Unchanged,
    Overwritten,
}
