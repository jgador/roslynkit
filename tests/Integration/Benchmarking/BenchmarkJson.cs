using System.Text.Json;
using System.Text.Json.Serialization;

namespace RoslynKit.Benchmarking;

/// <summary>
/// Provides the strict JSON contract shared by catalogs and run documents.
/// </summary>
internal static class BenchmarkJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        AllowTrailingCommas = false,
        MaxDepth = 32,
        NumberHandling = JsonNumberHandling.Strict,
        PropertyNameCaseInsensitive = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
    };
}
