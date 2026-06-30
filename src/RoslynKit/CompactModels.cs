using System.Text.Json.Serialization;

namespace RoslynKit;

/// <summary>
/// Typed payloads for the <c>--format compact</c> output mode. Every property carries an explicit
/// <see cref="JsonPropertyName"/> so the compact contract is stable and round-trip (de)serializable,
/// with no anonymous/dynamic objects. Compact intentionally trims verbose fields from the default
/// json models and collapses source locations into <c>path:line:column</c> strings. These payloads are
/// wrapped in the shared <see cref="JsonEnvelope"/>, identical to the default json frame.
/// </summary>

/// <summary>One source-declared symbol in compact form.</summary>
public sealed record CompactSymbol(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("container")] string? Container,
    [property: JsonPropertyName("loc")] string? Loc,
    [property: JsonPropertyName("decls")] IReadOnlyList<string>? Decls);

/// <summary>One compiler diagnostic in compact form.</summary>
public sealed record CompactDiagnostic(
    [property: JsonPropertyName("severity")] string Severity,
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("loc")] string? Loc,
    [property: JsonPropertyName("message")] string Message);

/// <summary>One loaded project in compact form.</summary>
public sealed record CompactProject(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("tfm")] string? Tfm,
    [property: JsonPropertyName("docs")] int Docs);

/// <summary>One workspace document in compact form.</summary>
public sealed record CompactDocument(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("path")] string? Path);

/// <summary>Compact payload for the <c>workspace</c> command.</summary>
public sealed record CompactWorkspaceData(
    [property: JsonPropertyName("target")] string Target,
    [property: JsonPropertyName("targetKind")] string TargetKind,
    [property: JsonPropertyName("projects")] IReadOnlyList<CompactProject> Projects,
    [property: JsonPropertyName("documents")] IReadOnlyList<CompactDocument> Documents,
    [property: JsonPropertyName("workspaceDiagnosticCount")] int? WorkspaceDiagnosticCount);

/// <summary>Compact payload for the <c>diagnostics</c> command.</summary>
public sealed record CompactDiagnosticsData(
    [property: JsonPropertyName("total")] int Total,
    [property: JsonPropertyName("returned")] int Returned,
    [property: JsonPropertyName("truncated")] bool? Truncated,
    [property: JsonPropertyName("diagnostics")] IReadOnlyList<CompactDiagnostic> Diagnostics,
    [property: JsonPropertyName("workspaceDiagnosticCount")] int? WorkspaceDiagnosticCount);

/// <summary>Compact payload for the <c>symbols</c> command.</summary>
public sealed record CompactSymbolsData(
    [property: JsonPropertyName("target")] string Target,
    [property: JsonPropertyName("query")] string Query,
    [property: JsonPropertyName("total")] int Total,
    [property: JsonPropertyName("returned")] int Returned,
    [property: JsonPropertyName("truncated")] bool? Truncated,
    [property: JsonPropertyName("symbols")] IReadOnlyList<CompactSymbol> Symbols,
    [property: JsonPropertyName("workspaceDiagnosticCount")] int? WorkspaceDiagnosticCount);

/// <summary>Compact payload for <c>definition</c> and <c>type-definition</c>.</summary>
public sealed record CompactDefinitionData(
    [property: JsonPropertyName("at")] string At,
    [property: JsonPropertyName("symbol")] CompactSymbol Symbol,
    [property: JsonPropertyName("workspaceDiagnosticCount")] int? WorkspaceDiagnosticCount);

/// <summary>Compact payload for the <c>references</c> command.</summary>
public sealed record CompactReferencesData(
    [property: JsonPropertyName("symbol")] CompactSymbol Symbol,
    [property: JsonPropertyName("total")] int Total,
    [property: JsonPropertyName("returned")] int Returned,
    [property: JsonPropertyName("truncated")] bool? Truncated,
    [property: JsonPropertyName("locations")] IReadOnlyList<string> Locations,
    [property: JsonPropertyName("workspaceDiagnosticCount")] int? WorkspaceDiagnosticCount);

/// <summary>Compact payload for the <c>implementations</c> command.</summary>
public sealed record CompactImplementationsData(
    [property: JsonPropertyName("symbol")] CompactSymbol Symbol,
    [property: JsonPropertyName("total")] int Total,
    [property: JsonPropertyName("returned")] int Returned,
    [property: JsonPropertyName("truncated")] bool? Truncated,
    [property: JsonPropertyName("symbols")] IReadOnlyList<CompactSymbol> Symbols,
    [property: JsonPropertyName("workspaceDiagnosticCount")] int? WorkspaceDiagnosticCount);

/// <summary>Compact payload for the <c>quick-info</c> command.</summary>
public sealed record CompactQuickInfoData(
    [property: JsonPropertyName("at")] string At,
    [property: JsonPropertyName("tags")] IReadOnlyList<string>? Tags,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("workspaceDiagnosticCount")] int? WorkspaceDiagnosticCount);

/// <summary>Compact payload for the <c>signature-help</c> command.</summary>
public sealed record CompactSignatureHelpData(
    [property: JsonPropertyName("at")] string At,
    [property: JsonPropertyName("active")] int Active,
    [property: JsonPropertyName("signatures")] IReadOnlyList<string> Signatures,
    [property: JsonPropertyName("workspaceDiagnosticCount")] int? WorkspaceDiagnosticCount);

/// <summary>Compact payload for the <c>document-symbols</c> command.</summary>
public sealed record CompactDocumentSymbolsData(
    [property: JsonPropertyName("document")] string Document,
    [property: JsonPropertyName("symbols")] IReadOnlyList<CompactSymbol> Symbols,
    [property: JsonPropertyName("workspaceDiagnosticCount")] int? WorkspaceDiagnosticCount);

/// <summary>Compact payload for the <c>document-text</c> command.</summary>
public sealed record CompactDocumentTextData(
    [property: JsonPropertyName("document")] string Document,
    [property: JsonPropertyName("truncated")] bool? Truncated,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("workspaceDiagnosticCount")] int? WorkspaceDiagnosticCount);
