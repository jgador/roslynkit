namespace RoslynKit;

/// <summary>
/// Describes one document resolved from the loaded workspace, including project and path context.
/// </summary>
public sealed class DocumentDescriptor
{
    public DocumentDescriptor(
        string projectName,
        string? projectPath,
        string? targetFramework,
        string documentKind,
        string name,
        string? path,
        string? displayProjectPath = null,
        string? displayPath = null)
        : this(
            documentKey: string.Empty,
            projectName,
            projectPath,
            targetFramework,
            documentKind,
            name,
            path,
            displayProjectPath,
            displayPath)
    {
    }

    internal DocumentDescriptor(
        string documentKey,
        string projectName,
        string? projectPath,
        string? targetFramework,
        string documentKind,
        string name,
        string? path,
        string? displayProjectPath = null,
        string? displayPath = null)
    {
        DocumentKey = documentKey;
        ProjectName = projectName;
        ProjectPath = projectPath;
        TargetFramework = targetFramework;
        DocumentKind = documentKind;
        Name = name;
        Path = path;
        DisplayProjectPath = displayProjectPath ?? projectPath;
        DisplayPath = displayPath ?? path;
    }

    /// <summary>
    /// Private stable key used only for deterministic internal ordering and de-duplication.
    /// </summary>
    internal string DocumentKey { get; }

    /// <summary>
    /// Name of the project that owns the document.
    /// </summary>
    public string ProjectName { get; }

    /// <summary>
    /// Absolute path to the owning project file, when Roslyn exposes one.
    /// </summary>
    public string? ProjectPath { get; }

    /// <summary>
    /// User-facing owning project path, relative to the loaded root when possible.
    /// </summary>
    public string? DisplayProjectPath { get; }

    /// <summary>
    /// Target framework label for the project context, when the load supplied one.
    /// </summary>
    public string? TargetFramework { get; }

    /// <summary>
    /// RoslynKit document-kind name used to route semantic and text commands.
    /// </summary>
    public string DocumentKind { get; }

    /// <summary>
    /// Roslyn document name as reported by the loaded workspace.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Absolute file path for path-backed documents, or <c>null</c> for generated documents without a path.
    /// </summary>
    public string? Path { get; }

    /// <summary>
    /// User-facing document path, relative to the loaded root when possible.
    /// </summary>
    public string? DisplayPath { get; }
}

/// <summary>
/// Represents a one-based span inside a resolved document.
/// </summary>
public sealed class DocumentRange
{
    public DocumentRange(int line, int column, int endLine, int endColumn)
    {
        Line = line;
        Column = column;
        EndLine = endLine;
        EndColumn = endColumn;
    }

    /// <summary>
    /// One-based starting line of the span.
    /// </summary>
    public int Line { get; }

    /// <summary>
    /// One-based starting column of the span.
    /// </summary>
    public int Column { get; }

    /// <summary>
    /// One-based ending line of the span.
    /// </summary>
    public int EndLine { get; }

    /// <summary>
    /// One-based ending column of the span.
    /// </summary>
    public int EndColumn { get; }
}

/// <summary>
/// Represents one loaded project entry in the <c>workspace</c> command payload.
/// </summary>
public sealed class WorkspaceProject
{
    public WorkspaceProject(
        string name,
        string? path,
        string? targetFramework,
        string language,
        int documentCount,
        IReadOnlyList<string> projectReferences)
    {
        Name = name;
        Path = path;
        TargetFramework = targetFramework;
        Language = language;
        DocumentCount = documentCount;
        ProjectReferences = projectReferences;
    }

    /// <summary>
    /// Project display name from the loaded Roslyn workspace.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Absolute project file path, when Roslyn exposes one.
    /// </summary>
    public string? Path { get; }

    /// <summary>
    /// Target framework label associated with this project context, when available.
    /// </summary>
    public string? TargetFramework { get; }

    /// <summary>
    /// Roslyn language name for the project.
    /// </summary>
    public string Language { get; }

    /// <summary>
    /// Count of command-addressable documents owned by this project.
    /// </summary>
    public int DocumentCount { get; }

    /// <summary>
    /// Project references listed by project name in deterministic order.
    /// </summary>
    public IReadOnlyList<string> ProjectReferences { get; }
}

/// <summary>
/// Represents one <c>MSBuildWorkspace</c> diagnostic captured while loading the target.
/// </summary>
public sealed class WorkspaceLoadDiagnostic
{
    public WorkspaceLoadDiagnostic(string kind, string message)
    {
        Kind = kind;
        Message = message;
    }

    /// <summary>
    /// Workspace diagnostic severity or category reported by Roslyn.
    /// </summary>
    public string Kind { get; }

    /// <summary>
    /// Workspace diagnostic message emitted during target load.
    /// </summary>
    public string Message { get; }
}
