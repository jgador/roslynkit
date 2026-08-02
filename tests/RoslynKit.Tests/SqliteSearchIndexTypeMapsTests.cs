using System.Data;
using Dapper;

namespace RoslynKit.Tests;

/// <summary>
/// Verifies explicit Dapper mappings for SQLite search-index query rows.
/// </summary>
public sealed class SqliteSearchIndexTypeMapsTests
{
    [Fact]
    public void MetadataRow_HydratesSqlColumnNamesCaseInsensitively()
    {
        var row = ParseRow<SqliteSearchIndexMetadataRow>(
            ("SCHEMA_VERSION", typeof(int), 7),
            ("target_identity", typeof(string), "RoslynKit.slnx"),
            ("FiNgErPrInT", typeof(string), "fingerprint"),
            ("INDEXED_AT_UTC", typeof(string), "2026-08-03T12:34:56.0000000+00:00"),
            ("symbol_count", typeof(int), 42));

        Assert.Equal(7, row.SchemaVersion);
        Assert.Equal("RoslynKit.slnx", row.TargetIdentity);
        Assert.Equal("fingerprint", row.Fingerprint);
        Assert.Equal("2026-08-03T12:34:56.0000000+00:00", row.IndexedAtUtc);
        Assert.Equal(42, row.SymbolCount);
    }

    [Fact]
    public void MatchRow_HydratesSqlColumnNamesCaseInsensitively()
    {
        var row = ParseRow<SqliteSearchIndexMatchRow>(
            ("NaMe", typeof(string), "SearchAsync"),
            ("DISPLAY_NAME", typeof(string), "RoslynKit.SearchCommandService.SearchAsync"),
            ("project_path", typeof(string), "src/RoslynKit/RoslynKit.csproj"),
            ("BM25_SCORE", typeof(double), -12.5d),
            ("query_term_coverage", typeof(int), 3));

        Assert.Equal("SearchAsync", row.Name);
        Assert.Equal("RoslynKit.SearchCommandService.SearchAsync", row.DisplayName);
        Assert.Equal("src/RoslynKit/RoslynKit.csproj", row.ProjectPath);
        Assert.Equal(-12.5d, row.RawBm25Score);
        Assert.Equal(3, row.QueryTermCoverage);
    }

    private static T ParseRow<T>(params (string ColumnName, Type ColumnType, object Value)[] values)
    {
        SqliteSearchIndexTypeMaps.EnsureRegistered();

        using DataTable table = new();
        foreach (var value in values)
        {
            table.Columns.Add(value.ColumnName, value.ColumnType);
        }

        table.Rows.Add(values.Select(value => value.Value).ToArray());

        using DataTableReader reader = table.CreateDataReader();
        return reader.Parse<T>().Single();
    }
}
