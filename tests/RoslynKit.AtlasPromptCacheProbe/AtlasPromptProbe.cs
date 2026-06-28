using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RoslynKit.AtlasPromptCacheProbe;

/// <summary>
/// Defines the Atlas lane to emulate when building the prompt-cached request.
/// </summary>
public enum AtlasPromptProbeLane
{
    Router,
    CSharpMapper,
    DocMapper,
    TestMapper,
}

/// <summary>
/// Holds the manual probe options for the Atlas prompt-caching utility.
/// </summary>
public sealed class AtlasPromptProbeOptions
{
    private AtlasPromptProbeOptions(
        string task,
        AtlasPromptProbeLane lane,
        string model,
        string promptCacheKey,
        string promptCacheRetention)
    {
        Task = task;
        Lane = lane;
        Model = model;
        PromptCacheKey = promptCacheKey;
        PromptCacheRetention = promptCacheRetention;
    }

    public string Task { get; }

    public AtlasPromptProbeLane Lane { get; }

    public string Model { get; }

    public string PromptCacheKey { get; }

    public string PromptCacheRetention { get; }

    public static AtlasPromptProbeOptions Parse(IReadOnlyList<string> args)
    {
        string? task = null;
        AtlasPromptProbeLane? lane = null;
        string? model = null;
        string? promptCacheKey = null;
        string? promptCacheRetention = null;

        for (var index = 0; index < args.Count; index++)
        {
            switch (args[index])
            {
                case "--task":
                    task = ReadSingleValue(args, ++index, "--task", task);
                    break;
                case "--lane":
                    var laneValue = ReadSingleValue(args, ++index, "--lane", lane is null ? null : AtlasPromptProbeLaneExtensions.ToOptionValue(lane.Value));
                    lane = AtlasPromptProbeLaneExtensions.Parse(laneValue);
                    break;
                case "--model":
                    model = ReadSingleValue(args, ++index, "--model", model);
                    break;
                case "--prompt-cache-key":
                    promptCacheKey = ReadSingleValue(args, ++index, "--prompt-cache-key", promptCacheKey);
                    break;
                case "--prompt-cache-retention":
                    promptCacheRetention = ReadSingleValue(args, ++index, "--prompt-cache-retention", promptCacheRetention);
                    break;
                default:
                    throw new AtlasPromptProbeUsageException($"Unknown option '{args[index]}'.");
            }
        }

        if (string.IsNullOrWhiteSpace(task))
        {
            task = AtlasPromptProbeDefaults.DefaultTask;
        }

        var resolvedLane = lane ?? AtlasPromptProbeLane.Router;
        var resolvedModel = string.IsNullOrWhiteSpace(model) ? AtlasPromptProbeDefaults.DefaultModel : model;
        var resolvedPromptCacheKey = string.IsNullOrWhiteSpace(promptCacheKey)
            ? AtlasPromptProbeDefaults.GetPromptCacheKey(resolvedLane)
            : promptCacheKey;
        var resolvedPromptCacheRetention = string.IsNullOrWhiteSpace(promptCacheRetention)
            ? AtlasPromptProbeDefaults.DefaultPromptCacheRetention
            : promptCacheRetention;

        return new AtlasPromptProbeOptions(
            task.Trim(),
            resolvedLane,
            resolvedModel,
            resolvedPromptCacheKey,
            resolvedPromptCacheRetention);
    }

    private static string ReadSingleValue(IReadOnlyList<string> args, int index, string optionName, string? existingValue)
    {
        if (existingValue is not null)
        {
            throw new AtlasPromptProbeUsageException($"Option '{optionName}' may only be specified once.");
        }

        if (index >= args.Count || string.IsNullOrWhiteSpace(args[index]))
        {
            throw new AtlasPromptProbeUsageException($"Option '{optionName}' requires a value.");
        }

        return args[index];
    }
}

/// <summary>
/// Provides stable defaults for the Atlas prompt-caching probe.
/// </summary>
public static class AtlasPromptProbeDefaults
{
    public const string DefaultModel = "gpt-5.4";
    public const string DefaultPromptCacheRetention = "24h";
    public const string DefaultTask = "trace definition command";

    public static string GetPromptCacheKey(AtlasPromptProbeLane lane)
    {
        return $"roslynkit:atlas:{AtlasPromptProbeLaneExtensions.ToOptionValue(lane)}:v1";
    }
}

/// <summary>
/// Coordinates loading Atlas inputs, calling the Responses API, and emitting a deterministic result.
/// </summary>
public static class AtlasPromptProbeRunner
{
    private const string OpenAiResponsesEndpoint = "https://api.openai.com/v1/responses";

    private static readonly JsonSerializerOptions PromptJsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static async Task<AtlasPromptProbeResult> RunAsync(AtlasPromptProbeOptions options, CancellationToken cancellationToken)
    {
        var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("OPENAI_API_KEY is not set.");
        }

        var repoRoot = ResolveRepositoryRoot();
        var stableFiles = LoadStableFiles(repoRoot, options.Lane);
        var stablePrefix = BuildStablePrefix(options.Lane, stableFiles);
        var routeSummary = await RunRouteAsync(repoRoot, options.Task, cancellationToken).ConfigureAwait(false);
        var indexLoadResult = LoadSelectedIndexes(repoRoot, routeSummary);
        var dynamicSuffix = BuildDynamicSuffix(options.Task, routeSummary, indexLoadResult.SelectedIndexes, indexLoadResult.Warnings);

        using var responseDocument = await CreateResponseAsync(
            options,
            stablePrefix,
            dynamicSuffix,
            apiKey,
            cancellationToken).ConfigureAwait(false);

        var responseText = ExtractResponseText(responseDocument.RootElement);
        var usage = ReadUsage(responseDocument.RootElement);
        var responseId = GetOptionalString(responseDocument.RootElement, "id");
        var responseStatus = GetOptionalString(responseDocument.RootElement, "status");

        return new AtlasPromptProbeResult(
            AtlasPromptProbeLaneExtensions.ToOptionValue(options.Lane),
            options.Model,
            options.PromptCacheKey,
            options.PromptCacheRetention,
            stableFiles.Select(file => file.Path).ToArray(),
            stablePrefix.Length,
            dynamicSuffix.Length,
            routeSummary,
            indexLoadResult.SelectedIndexes,
            new AtlasPromptProbeResponse(responseId, responseStatus, responseText),
            usage,
            indexLoadResult.Warnings);
    }

    /// <summary>
    /// Builds the stable cached prefix from the lane config plus durable Atlas markdown files.
    /// </summary>
    public static string BuildStablePrefix(AtlasPromptProbeLane lane, IReadOnlyList<AtlasPromptProbeTextFile> stableFiles)
    {
        if (stableFiles.Count < 3)
        {
            throw new InvalidOperationException("Stable Atlas inputs must include the lane config, repo map, test index, and feature cards.");
        }

        var orderedFiles = stableFiles
            .OrderBy(file => file.Path.StartsWith(".codex/agents/", StringComparison.OrdinalIgnoreCase) ? 0 : file.Path == ".codex/atlas/repo-map.md" ? 1 : file.Path == ".codex/atlas/test-index.md" ? 2 : 3)
            .ThenBy(file => file.Path, StringComparer.Ordinal)
            .ToArray();

        var builder = new StringBuilder();
        builder.AppendLine($"Atlas lane: {AtlasPromptProbeLaneExtensions.ToOptionValue(lane)}");
        builder.AppendLine("Use the following durable RoslynKit Atlas materials as the reusable prompt prefix for repeated Atlas queries.");
        builder.AppendLine("Keep the cached prefix stable. Raw source remains the source of truth.");
        builder.AppendLine();

        foreach (var file in orderedFiles)
        {
            builder.AppendLine($"<atlas-file path=\"{file.Path}\">");
            builder.AppendLine(file.Content.TrimEnd());
            builder.AppendLine("</atlas-file>");
            builder.AppendLine();
        }

        return builder.ToString().TrimEnd();
    }

    /// <summary>
    /// Builds the dynamic task-specific suffix from the deterministic route and compact index slices.
    /// </summary>
    public static string BuildDynamicSuffix(
        string task,
        AtlasPromptProbeRouteSummary routeSummary,
        AtlasPromptProbeSelectedIndexes selectedIndexes,
        IReadOnlyList<string> warnings)
    {
        var builder = new StringBuilder();
        builder.AppendLine("User task:");
        builder.AppendLine(task);
        builder.AppendLine();
        builder.AppendLine("Deterministic Atlas route summary:");
        AppendList(builder, "Likely feature cards", routeSummary.FeatureCards);
        AppendList(builder, "Matching filenames", routeSummary.MatchingFiles);
        AppendList(builder, "Matching tests", routeSummary.MatchingTests);
        AppendList(builder, "Suggested read order", routeSummary.SuggestedReadOrder);
        builder.AppendLine();
        builder.AppendLine("Raw route.ps1 output:");
        builder.AppendLine(routeSummary.RawOutput.TrimEnd());

        if (selectedIndexes.HasAnyData)
        {
            builder.AppendLine();
            builder.AppendLine("Selected generated Atlas index slices (volatile metadata removed):");
            builder.AppendLine(JsonSerializer.Serialize(selectedIndexes, PromptJsonOptions));
        }

        if (warnings.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Warnings:");
            foreach (var warning in warnings)
            {
                builder.AppendLine($"- {warning}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("Answer using the active lane instructions from the cached Atlas prefix. Treat the route and index slices as routing aids, not as a substitute for source truth.");
        return builder.ToString().TrimEnd();
    }

    public static AtlasPromptProbeIndexLoadResult LoadSelectedIndexes(string repoRoot, AtlasPromptProbeRouteSummary routeSummary)
    {
        var warnings = new List<string>();
        var selectedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in routeSummary.MatchingFiles.Concat(routeSummary.MatchingTests).Concat(routeSummary.SuggestedReadOrder))
        {
            if (!string.IsNullOrWhiteSpace(path))
            {
                selectedPaths.Add(path);
            }
        }

        var indexRoot = Path.Combine(repoRoot, ".codex", "atlas", "indexes");
        if (!Directory.Exists(indexRoot))
        {
            warnings.Add("Atlas indexes are missing. The probe is using markdown-only Atlas context.");
            return new AtlasPromptProbeIndexLoadResult(AtlasPromptProbeSelectedIndexes.Empty, warnings);
        }

        AtlasPromptProbeFileIndexEntry[]? fileEntries = LoadIndex<AtlasPromptProbeFileIndexDocument, AtlasPromptProbeFileIndexEntry[]>(
            Path.Combine(indexRoot, "file-index.json"),
            warnings,
            index => index.Files
                .Where(entry => selectedPaths.Contains(entry.Path))
                .OrderBy(entry => entry.Path, StringComparer.Ordinal)
                .ToArray());

        AtlasPromptProbeProjectIndexEntry[]? projectEntries = LoadIndex<AtlasPromptProbeProjectIndexDocument, AtlasPromptProbeProjectIndexEntry[]>(
            Path.Combine(indexRoot, "project-index.json"),
            warnings,
            index => index.Projects
                .Where(project => selectedPaths.Any(path => IsProjectRelevant(project.Path, path)))
                .OrderBy(project => project.Path, StringComparer.Ordinal)
                .ToArray());

        AtlasPromptProbeTestIndexSelection? testIndexSelection = LoadIndex<AtlasPromptProbeTestIndexDocument, AtlasPromptProbeTestIndexSelection>(
            Path.Combine(indexRoot, "test-index.json"),
            warnings,
            index => new AtlasPromptProbeTestIndexSelection(
                index.TestProjects
                    .Where(project => selectedPaths.Any(path => IsProjectRelevant(project.Path, path)))
                    .OrderBy(project => project.Path, StringComparer.Ordinal)
                    .ToArray(),
                index.TestFiles
                    .Where(entry => selectedPaths.Contains(entry.Path))
                    .OrderBy(entry => entry.Path, StringComparer.Ordinal)
                    .ToArray(),
                index.SupportFiles
                    .Where(entry => selectedPaths.Contains(entry.Path))
                    .OrderBy(entry => entry.Path, StringComparer.Ordinal)
                    .ToArray()));

        AtlasPromptProbeSymbolDocument[]? symbolDocuments = LoadIndex<AtlasPromptProbeSymbolIndexDocument, AtlasPromptProbeSymbolDocument[]>(
            Path.Combine(indexRoot, "symbol-index.json"),
            warnings,
            index => index.Documents
                .Where(document => selectedPaths.Contains(document.Path))
                .OrderBy(document => document.Path, StringComparer.Ordinal)
                .ToArray());

        return new AtlasPromptProbeIndexLoadResult(
            new AtlasPromptProbeSelectedIndexes(
                fileEntries ?? Array.Empty<AtlasPromptProbeFileIndexEntry>(),
                projectEntries ?? Array.Empty<AtlasPromptProbeProjectIndexEntry>(),
                testIndexSelection?.TestProjects ?? Array.Empty<AtlasPromptProbeTestProjectEntry>(),
                testIndexSelection?.TestFiles ?? Array.Empty<AtlasPromptProbeIndexedPathEntry>(),
                testIndexSelection?.SupportFiles ?? Array.Empty<AtlasPromptProbeIndexedPathEntry>(),
                symbolDocuments ?? Array.Empty<AtlasPromptProbeSymbolDocument>()),
            warnings);
    }

    public static AtlasPromptProbeUsage ReadUsage(JsonElement responseRoot)
    {
        if (!responseRoot.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
        {
            return new AtlasPromptProbeUsage(null, null, null, null);
        }

        var cachedTokens =
            GetOptionalInt64(usage, "cached_input_tokens")
            ?? GetNestedOptionalInt64(usage, "prompt_tokens_details", "cached_tokens")
            ?? GetNestedOptionalInt64(usage, "input_tokens_details", "cached_tokens");

        return new AtlasPromptProbeUsage(
            GetOptionalInt64(usage, "input_tokens"),
            GetOptionalInt64(usage, "output_tokens"),
            GetOptionalInt64(usage, "total_tokens"),
            cachedTokens);
    }

    private static async Task<JsonDocument> CreateResponseAsync(
        AtlasPromptProbeOptions options,
        string stablePrefix,
        string dynamicSuffix,
        string apiKey,
        CancellationToken cancellationToken)
    {
        var payload = new
        {
            model = options.Model,
            prompt_cache_key = options.PromptCacheKey,
            prompt_cache_retention = options.PromptCacheRetention,
            instructions = stablePrefix,
            input = dynamicSuffix,
        };

        using var client = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, OpenAiResponsesEndpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var responseContent = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"OpenAI Responses API request failed with HTTP {(int)response.StatusCode} ({response.ReasonPhrase}). {responseContent}");
        }

        return JsonDocument.Parse(responseContent);
    }

    private static IReadOnlyList<AtlasPromptProbeTextFile> LoadStableFiles(string repoRoot, AtlasPromptProbeLane lane)
    {
        var stableFiles = new List<AtlasPromptProbeTextFile>();
        var agentFilePath = $".codex/agents/{AtlasPromptProbeLaneExtensions.ToAgentConfigName(lane)}.toml";
        var repoMapPath = ".codex/atlas/repo-map.md";
        var testIndexPath = ".codex/atlas/test-index.md";

        stableFiles.Add(LoadTextFile(repoRoot, agentFilePath));
        stableFiles.Add(LoadTextFile(repoRoot, repoMapPath));
        stableFiles.Add(LoadTextFile(repoRoot, testIndexPath));

        var featureCardRoot = Path.Combine(repoRoot, ".codex", "atlas", "feature-cards");
        var featureCardFiles = Directory
            .EnumerateFiles(featureCardRoot, "*.md", SearchOption.TopDirectoryOnly)
            .Select(path => NormalizeRelativePath(repoRoot, path))
            .Where(path => !string.Equals(path, ".codex/atlas/feature-cards/README.md", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.Ordinal);

        foreach (var featureCardPath in featureCardFiles)
        {
            stableFiles.Add(LoadTextFile(repoRoot, featureCardPath));
        }

        return stableFiles;
    }

    private static AtlasPromptProbeTextFile LoadTextFile(string repoRoot, string relativePath)
    {
        var fullPath = Path.Combine(repoRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        return new AtlasPromptProbeTextFile(relativePath, File.ReadAllText(fullPath));
    }

    private static async Task<AtlasPromptProbeRouteSummary> RunRouteAsync(string repoRoot, string task, CancellationToken cancellationToken)
    {
        var routeScriptPath = Path.Combine(repoRoot, ".codex", "atlas", "scripts", "route.ps1");
        var startInfo = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "powershell" : "pwsh",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = repoRoot,
        };

        startInfo.ArgumentList.Add("-NoProfile");
        if (OperatingSystem.IsWindows())
        {
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
        }

        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(routeScriptPath);
        startInfo.ArgumentList.Add(task);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start the Atlas route script.");

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Atlas route script failed with exit code {process.ExitCode}.{Environment.NewLine}stdout:{Environment.NewLine}{stdout}{Environment.NewLine}stderr:{Environment.NewLine}{stderr}");
        }

        return ParseRouteOutput(stdout);
    }

    private static AtlasPromptProbeRouteSummary ParseRouteOutput(string rawOutput)
    {
        string? task = null;
        var featureCards = new List<string>();
        var matchingFiles = new List<string>();
        var matchingTests = new List<string>();
        var suggestedReadOrder = new List<string>();
        List<string>? currentSection = null;

        foreach (var rawLine in rawOutput.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = rawLine.TrimEnd();
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (line.StartsWith("Task:", StringComparison.Ordinal))
            {
                task = line.Substring("Task:".Length).Trim();
                currentSection = null;
                continue;
            }

            currentSection = line switch
            {
                "Likely feature cards:" => featureCards,
                "Matching filenames:" => matchingFiles,
                "Matching tests:" => matchingTests,
                "Suggested read order:" => suggestedReadOrder,
                _ => currentSection,
            };

            if (!line.StartsWith("- ", StringComparison.Ordinal) || currentSection is null)
            {
                continue;
            }

            var value = line.Substring(2).Trim();
            if (!string.Equals(value, "(none)", StringComparison.Ordinal))
            {
                currentSection.Add(value);
            }
        }

        return new AtlasPromptProbeRouteSummary(
            task ?? string.Empty,
            rawOutput.TrimEnd(),
            featureCards,
            matchingFiles,
            matchingTests,
            suggestedReadOrder);
    }

    private static TSelection? LoadIndex<TIndex, TSelection>(
        string path,
        ICollection<string> warnings,
        Func<TIndex, TSelection> selector)
        where TIndex : class
    {
        if (!File.Exists(path))
        {
            warnings.Add($"Atlas index '{Path.GetFileName(path)}' is missing.");
            return default;
        }

        try
        {
            var document = JsonSerializer.Deserialize<TIndex>(File.ReadAllText(path), PromptJsonOptions);
            if (document is null)
            {
                warnings.Add($"Atlas index '{Path.GetFileName(path)}' was empty.");
                return default;
            }

            return selector(document);
        }
        catch (JsonException ex)
        {
            warnings.Add($"Atlas index '{Path.GetFileName(path)}' could not be parsed: {ex.Message}");
            return default;
        }
    }

    private static bool IsProjectRelevant(string projectPath, string selectedPath)
    {
        if (string.Equals(projectPath, selectedPath, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var normalizedProjectDirectory = NormalizeDirectory(Path.GetDirectoryName(projectPath) ?? string.Empty);
        var normalizedSelectedPath = NormalizeDirectory(selectedPath);
        return normalizedSelectedPath.StartsWith(normalizedProjectDirectory, StringComparison.OrdinalIgnoreCase);
    }

    private static string ExtractResponseText(JsonElement responseRoot)
    {
        if (responseRoot.TryGetProperty("output_text", out var outputText) && outputText.ValueKind == JsonValueKind.String)
        {
            return outputText.GetString() ?? string.Empty;
        }

        if (!responseRoot.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        foreach (var item in output.EnumerateArray())
        {
            if (!item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var contentItem in content.EnumerateArray())
            {
                if (!contentItem.TryGetProperty("type", out var type)
                    || type.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var contentType = type.GetString();
                if (!string.Equals(contentType, "output_text", StringComparison.Ordinal)
                    && !string.Equals(contentType, "text", StringComparison.Ordinal))
                {
                    continue;
                }

                if (contentItem.TryGetProperty("text", out var text)
                    && text.ValueKind == JsonValueKind.String)
                {
                    if (builder.Length > 0)
                    {
                        builder.AppendLine();
                    }

                    builder.Append(text.GetString());
                }
            }
        }

        return builder.ToString();
    }

    private static long? GetOptionalInt64(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt64()
            : null;
    }

    private static long? GetNestedOptionalInt64(JsonElement element, string propertyName, string nestedPropertyName)
    {
        return element.TryGetProperty(propertyName, out var nestedElement) && nestedElement.ValueKind == JsonValueKind.Object
            ? GetOptionalInt64(nestedElement, nestedPropertyName)
            : null;
    }

    private static string? GetOptionalString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static string ResolveRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidatePath = Path.Combine(directory.FullName, "RoslynKit.slnx");
            if (File.Exists(candidatePath))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Could not locate the RoslynKit repository root from the application base directory.");
    }

    private static string NormalizeRelativePath(string repoRoot, string fullPath)
    {
        var root = Path.GetFullPath(repoRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var path = Path.GetFullPath(fullPath);
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            return NormalizeDirectory(fullPath);
        }

        return NormalizeDirectory(path.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
    }

    private static string NormalizeDirectory(string path)
    {
        return path.Replace('\\', '/');
    }

    private static void AppendList(StringBuilder builder, string heading, IReadOnlyList<string> values)
    {
        builder.AppendLine($"{heading}:");
        if (values.Count == 0)
        {
            builder.AppendLine("- (none)");
            return;
        }

        foreach (var value in values)
        {
            builder.AppendLine($"- {value}");
        }
    }
}

/// <summary>
/// Maps between the lane enum and the checked-in Atlas config names.
/// </summary>
public static class AtlasPromptProbeLaneExtensions
{
    public static AtlasPromptProbeLane Parse(string value)
    {
        return value.ToLowerInvariant() switch
        {
            "router" => AtlasPromptProbeLane.Router,
            "csharp-mapper" => AtlasPromptProbeLane.CSharpMapper,
            "doc-mapper" => AtlasPromptProbeLane.DocMapper,
            "test-mapper" => AtlasPromptProbeLane.TestMapper,
            _ => throw new AtlasPromptProbeUsageException($"Unknown lane '{value}'. Expected router, csharp-mapper, doc-mapper, or test-mapper."),
        };
    }

    public static string ToAgentConfigName(AtlasPromptProbeLane lane)
    {
        return lane switch
        {
            AtlasPromptProbeLane.Router => "atlas-router",
            AtlasPromptProbeLane.CSharpMapper => "atlas-csharp-mapper",
            AtlasPromptProbeLane.DocMapper => "atlas-doc-mapper",
            AtlasPromptProbeLane.TestMapper => "atlas-test-mapper",
            _ => throw new InvalidOperationException($"Unknown lane '{lane}'."),
        };
    }

    public static string ToOptionValue(AtlasPromptProbeLane lane)
    {
        return lane switch
        {
            AtlasPromptProbeLane.Router => "router",
            AtlasPromptProbeLane.CSharpMapper => "csharp-mapper",
            AtlasPromptProbeLane.DocMapper => "doc-mapper",
            AtlasPromptProbeLane.TestMapper => "test-mapper",
            _ => throw new InvalidOperationException($"Unknown lane '{lane}'."),
        };
    }

    public static string ToCliValue(AtlasPromptProbeLane lane)
    {
        return ToOptionValue(lane);
    }
}

/// <summary>
/// Represents one stable text file included in the cached Atlas prefix.
/// </summary>
public sealed class AtlasPromptProbeTextFile
{
    public AtlasPromptProbeTextFile(string path, string content)
    {
        Path = path;
        Content = content;
    }

    public string Path { get; }

    public string Content { get; }
}

/// <summary>
/// Represents the parsed deterministic route output from route.ps1.
/// </summary>
public sealed class AtlasPromptProbeRouteSummary
{
    public AtlasPromptProbeRouteSummary(
        string task,
        string rawOutput,
        IReadOnlyList<string> featureCards,
        IReadOnlyList<string> matchingFiles,
        IReadOnlyList<string> matchingTests,
        IReadOnlyList<string> suggestedReadOrder)
    {
        Task = task;
        RawOutput = rawOutput;
        FeatureCards = featureCards;
        MatchingFiles = matchingFiles;
        MatchingTests = matchingTests;
        SuggestedReadOrder = suggestedReadOrder;
    }

    [JsonPropertyName("task")]
    public string Task { get; }

    [JsonPropertyName("rawOutput")]
    public string RawOutput { get; }

    [JsonPropertyName("featureCards")]
    public IReadOnlyList<string> FeatureCards { get; }

    [JsonPropertyName("matchingFiles")]
    public IReadOnlyList<string> MatchingFiles { get; }

    [JsonPropertyName("matchingTests")]
    public IReadOnlyList<string> MatchingTests { get; }

    [JsonPropertyName("suggestedReadOrder")]
    public IReadOnlyList<string> SuggestedReadOrder { get; }
}

/// <summary>
/// Holds the selected generated index rows that were added to the dynamic Atlas suffix.
/// </summary>
public sealed class AtlasPromptProbeSelectedIndexes
{
    public static readonly AtlasPromptProbeSelectedIndexes Empty = new(
        Array.Empty<AtlasPromptProbeFileIndexEntry>(),
        Array.Empty<AtlasPromptProbeProjectIndexEntry>(),
        Array.Empty<AtlasPromptProbeTestProjectEntry>(),
        Array.Empty<AtlasPromptProbeIndexedPathEntry>(),
        Array.Empty<AtlasPromptProbeIndexedPathEntry>(),
        Array.Empty<AtlasPromptProbeSymbolDocument>());

    public AtlasPromptProbeSelectedIndexes(
        IReadOnlyList<AtlasPromptProbeFileIndexEntry> fileEntries,
        IReadOnlyList<AtlasPromptProbeProjectIndexEntry> projectEntries,
        IReadOnlyList<AtlasPromptProbeTestProjectEntry> testProjects,
        IReadOnlyList<AtlasPromptProbeIndexedPathEntry> testFiles,
        IReadOnlyList<AtlasPromptProbeIndexedPathEntry> supportFiles,
        IReadOnlyList<AtlasPromptProbeSymbolDocument> symbolDocuments)
    {
        FileEntries = fileEntries;
        ProjectEntries = projectEntries;
        TestProjects = testProjects;
        TestFiles = testFiles;
        SupportFiles = supportFiles;
        SymbolDocuments = symbolDocuments;
    }

    [JsonPropertyName("fileEntries")]
    public IReadOnlyList<AtlasPromptProbeFileIndexEntry> FileEntries { get; }

    [JsonPropertyName("projectEntries")]
    public IReadOnlyList<AtlasPromptProbeProjectIndexEntry> ProjectEntries { get; }

    [JsonPropertyName("testProjects")]
    public IReadOnlyList<AtlasPromptProbeTestProjectEntry> TestProjects { get; }

    [JsonPropertyName("testFiles")]
    public IReadOnlyList<AtlasPromptProbeIndexedPathEntry> TestFiles { get; }

    [JsonPropertyName("supportFiles")]
    public IReadOnlyList<AtlasPromptProbeIndexedPathEntry> SupportFiles { get; }

    [JsonPropertyName("symbolDocuments")]
    public IReadOnlyList<AtlasPromptProbeSymbolDocument> SymbolDocuments { get; }

    [JsonIgnore]
    public bool HasAnyData =>
        FileEntries.Count > 0
        || ProjectEntries.Count > 0
        || TestProjects.Count > 0
        || TestFiles.Count > 0
        || SupportFiles.Count > 0
        || SymbolDocuments.Count > 0;
}

/// <summary>
/// Contains the selected index rows plus non-fatal index loading warnings.
/// </summary>
public sealed class AtlasPromptProbeIndexLoadResult
{
    public AtlasPromptProbeIndexLoadResult(AtlasPromptProbeSelectedIndexes selectedIndexes, IReadOnlyList<string> warnings)
    {
        SelectedIndexes = selectedIndexes;
        Warnings = warnings;
    }

    public AtlasPromptProbeSelectedIndexes SelectedIndexes { get; }

    public IReadOnlyList<string> Warnings { get; }
}

/// <summary>
/// Represents the top-level probe result payload written in the JSON envelope.
/// </summary>
public sealed class AtlasPromptProbeResult
{
    public AtlasPromptProbeResult(
        string lane,
        string model,
        string promptCacheKey,
        string promptCacheRetention,
        IReadOnlyList<string> stableFiles,
        int stablePrefixLength,
        int dynamicSuffixLength,
        AtlasPromptProbeRouteSummary dynamicRoute,
        AtlasPromptProbeSelectedIndexes selectedIndexes,
        AtlasPromptProbeResponse response,
        AtlasPromptProbeUsage usage,
        IReadOnlyList<string> warnings)
    {
        Lane = lane;
        Model = model;
        PromptCacheKey = promptCacheKey;
        PromptCacheRetention = promptCacheRetention;
        StableFiles = stableFiles;
        StablePrefixLength = stablePrefixLength;
        DynamicSuffixLength = dynamicSuffixLength;
        DynamicRoute = dynamicRoute;
        SelectedIndexes = selectedIndexes;
        Response = response;
        Usage = usage;
        Warnings = warnings;
    }

    [JsonPropertyName("lane")]
    public string Lane { get; }

    [JsonPropertyName("model")]
    public string Model { get; }

    [JsonPropertyName("promptCacheKey")]
    public string PromptCacheKey { get; }

    [JsonPropertyName("promptCacheRetention")]
    public string PromptCacheRetention { get; }

    [JsonPropertyName("stableFiles")]
    public IReadOnlyList<string> StableFiles { get; }

    [JsonPropertyName("stablePrefixLength")]
    public int StablePrefixLength { get; }

    [JsonPropertyName("dynamicSuffixLength")]
    public int DynamicSuffixLength { get; }

    [JsonPropertyName("dynamicRoute")]
    public AtlasPromptProbeRouteSummary DynamicRoute { get; }

    [JsonPropertyName("selectedIndexes")]
    public AtlasPromptProbeSelectedIndexes SelectedIndexes { get; }

    [JsonPropertyName("response")]
    public AtlasPromptProbeResponse Response { get; }

    [JsonPropertyName("usage")]
    public AtlasPromptProbeUsage Usage { get; }

    [JsonPropertyName("warnings")]
    public IReadOnlyList<string> Warnings { get; }
}

/// <summary>
/// Carries the minimal response payload that the probe reports back to the caller.
/// </summary>
public sealed class AtlasPromptProbeResponse
{
    public AtlasPromptProbeResponse(string? id, string? status, string outputText)
    {
        Id = id;
        Status = status;
        OutputText = outputText;
    }

    [JsonPropertyName("id")]
    public string? Id { get; }

    [JsonPropertyName("status")]
    public string? Status { get; }

    [JsonPropertyName("outputText")]
    public string OutputText { get; }
}

/// <summary>
/// Reports the token usage counters relevant to prompt caching.
/// </summary>
public sealed class AtlasPromptProbeUsage
{
    public AtlasPromptProbeUsage(long? inputTokens, long? outputTokens, long? totalTokens, long? cachedTokens)
    {
        InputTokens = inputTokens;
        OutputTokens = outputTokens;
        TotalTokens = totalTokens;
        CachedTokens = cachedTokens;
    }

    [JsonPropertyName("inputTokens")]
    public long? InputTokens { get; }

    [JsonPropertyName("outputTokens")]
    public long? OutputTokens { get; }

    [JsonPropertyName("totalTokens")]
    public long? TotalTokens { get; }

    [JsonPropertyName("cachedTokens")]
    public long? CachedTokens { get; }
}

/// <summary>
/// Represents one selected file-index row.
/// </summary>
public sealed class AtlasPromptProbeFileIndexEntry
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("extension")]
    public string Extension { get; set; } = string.Empty;

    [JsonPropertyName("area")]
    public string Area { get; set; } = string.Empty;

    [JsonPropertyName("category")]
    public string Category { get; set; } = string.Empty;
}

/// <summary>
/// Represents one selected project-index row.
/// </summary>
public sealed class AtlasPromptProbeProjectIndexEntry
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("category")]
    public string Category { get; set; } = string.Empty;

    [JsonPropertyName("isTestProject")]
    public bool IsTestProject { get; set; }

    [JsonPropertyName("includedInSolution")]
    public bool IncludedInSolution { get; set; }
}

/// <summary>
/// Represents one selected test-project row.
/// </summary>
public sealed class AtlasPromptProbeTestProjectEntry
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("category")]
    public string Category { get; set; } = string.Empty;
}

/// <summary>
/// Represents one selected test or support path row.
/// </summary>
public sealed class AtlasPromptProbeIndexedPathEntry
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("category")]
    public string Category { get; set; } = string.Empty;
}

/// <summary>
/// Represents one selected symbol-index document with its compact symbol list.
/// </summary>
public sealed class AtlasPromptProbeSymbolDocument
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("projectName")]
    public string ProjectName { get; set; } = string.Empty;

    [JsonPropertyName("documentKind")]
    public string DocumentKind { get; set; } = string.Empty;

    [JsonPropertyName("symbolCount")]
    public int SymbolCount { get; set; }

    [JsonPropertyName("symbols")]
    public IReadOnlyList<AtlasPromptProbeSymbol> Symbols { get; set; } = Array.Empty<AtlasPromptProbeSymbol>();
}

/// <summary>
/// Represents one compact symbol row selected from the symbol index.
/// </summary>
public sealed class AtlasPromptProbeSymbol
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = string.Empty;

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = string.Empty;

    [JsonPropertyName("line")]
    public int Line { get; set; }

    [JsonPropertyName("column")]
    public int Column { get; set; }

    [JsonPropertyName("containingType")]
    public string? ContainingType { get; set; }

    [JsonPropertyName("containingNamespace")]
    public string? ContainingNamespace { get; set; }
}

/// <summary>
/// Represents a lightweight usage-only JSON envelope for the probe utility.
/// </summary>
public sealed class ProbeEnvelope
{
    private ProbeEnvelope(int schemaVersion, string tool, string command, bool success, object? data, IReadOnlyList<ProbeErrorInfo> errors)
    {
        SchemaVersion = schemaVersion;
        Tool = tool;
        Command = command;
        Success = success;
        Data = data;
        Errors = errors;
    }

    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; }

    [JsonPropertyName("tool")]
    public string Tool { get; }

    [JsonPropertyName("command")]
    public string Command { get; }

    [JsonPropertyName("success")]
    public bool Success { get; }

    [JsonPropertyName("data")]
    public object? Data { get; }

    [JsonPropertyName("errors")]
    public IReadOnlyList<ProbeErrorInfo> Errors { get; }

    public static ProbeEnvelope ForSuccess(string command, object data)
    {
        return new ProbeEnvelope(1, "atlas-prompt-cache-probe", command, true, data, Array.Empty<ProbeErrorInfo>());
    }

    public static ProbeEnvelope Failure(string command, params ProbeErrorInfo[] errors)
    {
        return new ProbeEnvelope(1, "atlas-prompt-cache-probe", command, false, null, errors);
    }
}

/// <summary>
/// Represents one failed probe error entry.
/// </summary>
public sealed class ProbeErrorInfo
{
    private ProbeErrorInfo(string kind, string message, string? code = null)
    {
        Kind = kind;
        Message = message;
        Code = code;
    }

    [JsonPropertyName("kind")]
    public string Kind { get; }

    [JsonPropertyName("message")]
    public string Message { get; }

    [JsonPropertyName("code")]
    public string? Code { get; }

    public static ProbeErrorInfo Usage(string message)
    {
        return new ProbeErrorInfo("usage", message);
    }

    public static ProbeErrorInfo Canceled(string message)
    {
        return new ProbeErrorInfo("canceled", message);
    }

    public static ProbeErrorInfo Internal(string code, string message)
    {
        return new ProbeErrorInfo("internal", message, code);
    }
}

/// <summary>
/// Thrown when probe option parsing fails before the network request is attempted.
/// </summary>
public sealed class AtlasPromptProbeUsageException : Exception
{
    public AtlasPromptProbeUsageException(string message)
        : base(message)
    {
    }
}

internal sealed class AtlasPromptProbeFileIndexDocument
{
    [JsonPropertyName("files")]
    public IReadOnlyList<AtlasPromptProbeFileIndexEntry> Files { get; set; } = Array.Empty<AtlasPromptProbeFileIndexEntry>();
}

internal sealed class AtlasPromptProbeProjectIndexDocument
{
    [JsonPropertyName("projects")]
    public IReadOnlyList<AtlasPromptProbeProjectIndexEntry> Projects { get; set; } = Array.Empty<AtlasPromptProbeProjectIndexEntry>();
}

internal sealed class AtlasPromptProbeTestIndexDocument
{
    [JsonPropertyName("testProjects")]
    public IReadOnlyList<AtlasPromptProbeTestProjectEntry> TestProjects { get; set; } = Array.Empty<AtlasPromptProbeTestProjectEntry>();

    [JsonPropertyName("testFiles")]
    public IReadOnlyList<AtlasPromptProbeIndexedPathEntry> TestFiles { get; set; } = Array.Empty<AtlasPromptProbeIndexedPathEntry>();

    [JsonPropertyName("supportFiles")]
    public IReadOnlyList<AtlasPromptProbeIndexedPathEntry> SupportFiles { get; set; } = Array.Empty<AtlasPromptProbeIndexedPathEntry>();
}

internal sealed class AtlasPromptProbeTestIndexSelection
{
    public AtlasPromptProbeTestIndexSelection(
        IReadOnlyList<AtlasPromptProbeTestProjectEntry> testProjects,
        IReadOnlyList<AtlasPromptProbeIndexedPathEntry> testFiles,
        IReadOnlyList<AtlasPromptProbeIndexedPathEntry> supportFiles)
    {
        TestProjects = testProjects;
        TestFiles = testFiles;
        SupportFiles = supportFiles;
    }

    public IReadOnlyList<AtlasPromptProbeTestProjectEntry> TestProjects { get; }

    public IReadOnlyList<AtlasPromptProbeIndexedPathEntry> TestFiles { get; }

    public IReadOnlyList<AtlasPromptProbeIndexedPathEntry> SupportFiles { get; }
}

internal sealed class AtlasPromptProbeSymbolIndexDocument
{
    [JsonPropertyName("documents")]
    public IReadOnlyList<AtlasPromptProbeSymbolDocument> Documents { get; set; } = Array.Empty<AtlasPromptProbeSymbolDocument>();
}
