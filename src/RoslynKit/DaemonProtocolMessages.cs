using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

namespace RoslynKit;

/// <summary>
/// Carries the compatibility version and correlation identifier shared by every daemon wire message.
/// </summary>
internal abstract record DaemonMessage(int ProtocolVersion, Guid RequestId);

/// <summary>
/// Identifies the closed set of messages accepted by a daemon connection.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "messageType")]
[JsonDerivedType(typeof(DaemonHandshakeRequest), "handshake")]
[JsonDerivedType(typeof(DaemonCommandRequest), "command")]
[JsonDerivedType(typeof(DaemonStatusRequest), "status")]
[JsonDerivedType(typeof(DaemonStopRequest), "stop")]
internal abstract record DaemonRequest(int ProtocolVersion, Guid RequestId)
    : DaemonMessage(ProtocolVersion, RequestId);

/// <summary>
/// Identifies the closed set of messages returned by a daemon connection.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "messageType")]
[JsonDerivedType(typeof(DaemonHandshakeResponse), "handshake")]
[JsonDerivedType(typeof(DaemonCommandResponse), "command")]
[JsonDerivedType(typeof(DaemonStatusResponse), "status")]
[JsonDerivedType(typeof(DaemonStopResponse), "stop")]
internal abstract record DaemonResponse(int ProtocolVersion, Guid RequestId)
    : DaemonMessage(ProtocolVersion, RequestId);

/// <summary>
/// Probes a connected endpoint before it is considered ready for requests.
/// </summary>
internal sealed record DaemonHandshakeRequest(int ProtocolVersion, Guid RequestId)
    : DaemonRequest(ProtocolVersion, RequestId);

/// <summary>
/// Confirms whether a connected endpoint accepts the client's protocol handshake.
/// </summary>
internal sealed record DaemonHandshakeResponse(
    int ProtocolVersion,
    Guid RequestId,
    bool Accepted,
    string? Diagnostic)
    : DaemonResponse(ProtocolVersion, RequestId);

/// <summary>
/// Carries one locally parsed workspace command with canonical path options and an absolute deadline.
/// </summary>
internal sealed record DaemonCommandRequest(
    int ProtocolVersion,
    Guid RequestId,
    string CommandName,
    IReadOnlyDictionary<string, string> Options,
    DateTimeOffset DeadlineUtc)
    : DaemonRequest(ProtocolVersion, RequestId)
{
    private static readonly HashSet<string> EligibleCommandNames = new(StringComparer.Ordinal)
    {
        "definition",
        "diagnostics",
        "document-lines",
        "document-symbols",
        "document-text",
        "implementations",
        "index",
        "quick-info",
        "references",
        "search",
        "signature-help",
        "symbol-context",
        "symbol-source",
        "symbols",
        "type-definition",
        "workspace",
    };

    private static readonly HashSet<string> PathOptionNames = new(StringComparer.Ordinal)
    {
        "file",
        "index-path",
        "project",
        "target",
    };

    /// <summary>
    /// Creates the wire representation of a parsed command and canonicalizes path-valued options at the client boundary.
    /// </summary>
    public static DaemonCommandRequest Create(
        ParsedCommand command,
        Guid requestId,
        DateTimeOffset deadlineUtc,
        string? baseDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(command);
        EnsureEligibleCommand(command.Name);
        baseDirectory = Path.GetFullPath(baseDirectory ?? Directory.GetCurrentDirectory());

        var options = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in command.Options)
        {
            options.Add(
                name,
                CanonicalizeOptionPath(name, value, baseDirectory));
        }

        return new DaemonCommandRequest(
            RoslynKitBuildInfo.DaemonProtocolVersion,
            requestId,
            command.Name,
            new ReadOnlyDictionary<string, string>(options),
            deadlineUtc.ToUniversalTime());
    }

    /// <summary>
    /// Reconstructs and revalidates the parsed command at the server boundary before execution.
    /// </summary>
    public ParsedCommand ToParsedCommand()
    {
        DaemonProtocol.EnsureCompatible(this);
        EnsureEligibleCommand(CommandName);
        var builtin = BuiltinCommandRegistry.GetBuiltin(CommandName)
            ?? throw new DaemonProtocolException(
                DaemonProtocolError.InvalidMessage,
                $"Unknown daemon command '{CommandName}'.");

        var arguments = new List<string>(builtin.Path);
        foreach (var (name, value) in Options.OrderBy(option => option.Key, StringComparer.Ordinal))
        {
            arguments.Add($"--{name}");
            var option = builtin.Options.FirstOrDefault(candidate => candidate.LongName == name);
            if (option?.Kind == OptionKind.Flag)
            {
                if (!bool.TryParse(value, out var enabled) || !enabled)
                {
                    throw new DaemonProtocolException(
                        DaemonProtocolError.InvalidMessage,
                        $"Daemon flag option '--{name}' must have the normalized value 'True'.");
                }

                continue;
            }

            arguments.Add(value);
        }

        return CliParser.Parse(arguments);
    }

    private static void EnsureEligibleCommand(string commandName)
    {
        if (!EligibleCommandNames.Contains(commandName))
        {
            throw new DaemonProtocolException(
                DaemonProtocolError.InvalidMessage,
                $"Command '{commandName}' is not eligible for daemon execution.");
        }
    }

    private static string CanonicalizeOptionPath(string name, string value, string baseDirectory)
    {
        if (!PathOptionNames.Contains(name))
        {
            return value;
        }

        return name == "index-path"
            ? ResolveIndexPath(value, baseDirectory)
            : PathCanonicalizer.ResolveExistingPath(value, baseDirectory);
    }

    private static string ResolveIndexPath(string indexPath, string baseDirectory)
    {
        var fullPath = Path.GetFullPath(indexPath, baseDirectory);
        if (File.Exists(fullPath) || Directory.Exists(fullPath))
        {
            return PathCanonicalizer.ResolveExistingPath(fullPath);
        }

        var unresolvedSegments = new Stack<string>();
        var existingAncestor = fullPath;
        while (!File.Exists(existingAncestor) && !Directory.Exists(existingAncestor))
        {
            var parent = Path.GetDirectoryName(existingAncestor);
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, existingAncestor, StringComparison.Ordinal))
            {
                return fullPath;
            }

            unresolvedSegments.Push(Path.GetFileName(existingAncestor));
            existingAncestor = parent;
        }

        var canonicalPath = PathCanonicalizer.ResolveExistingPath(existingAncestor);
        while (unresolvedSegments.TryPop(out var segment))
        {
            canonicalPath = Path.Combine(canonicalPath, segment);
        }

        return canonicalPath;
    }
}

/// <summary>
/// Returns one complete buffered command result without publishing partial process output.
/// </summary>
internal sealed record DaemonCommandResponse(
    int ProtocolVersion,
    Guid RequestId,
    int ExitCode,
    string Stdout,
    string Stderr)
    : DaemonResponse(ProtocolVersion, RequestId)
{
    public CliProcessResult ToProcessResult()
    {
        return new CliProcessResult(ExitCode, Stdout, Stderr);
    }

    public static DaemonCommandResponse Create(Guid requestId, CliProcessResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return new DaemonCommandResponse(
            RoslynKitBuildInfo.DaemonProtocolVersion,
            requestId,
            result.ExitCode,
            result.Stdout,
            result.Stderr);
    }
}

/// <summary>
/// Requests a non-starting snapshot of daemon lifecycle and workspace state.
/// </summary>
internal sealed record DaemonStatusRequest(int ProtocolVersion, Guid RequestId)
    : DaemonRequest(ProtocolVersion, RequestId);

/// <summary>
/// Reports daemon lifecycle, workspace generation, and bounded diagnostic state.
/// </summary>
internal sealed record DaemonStatusResponse(
    int ProtocolVersion,
    Guid RequestId,
    bool Running,
    string? TargetPath,
    int? ProcessId,
    string? WorkspaceState,
    long? Generation,
    int ActiveRequests,
    int QueuedRequests,
    string? Diagnostic)
    : DaemonResponse(ProtocolVersion, RequestId);

/// <summary>
/// Requests graceful daemon shutdown without starting an absent daemon.
/// </summary>
internal sealed record DaemonStopRequest(int ProtocolVersion, Guid RequestId)
    : DaemonRequest(ProtocolVersion, RequestId);

/// <summary>
/// Confirms whether graceful daemon shutdown has begun.
/// </summary>
internal sealed record DaemonStopResponse(int ProtocolVersion, Guid RequestId, bool Stopping)
    : DaemonResponse(ProtocolVersion, RequestId);
