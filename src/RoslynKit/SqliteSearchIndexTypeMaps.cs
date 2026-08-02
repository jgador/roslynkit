using System.Linq.Expressions;
using System.Reflection;
using Dapper;

namespace RoslynKit;

/// <summary>
/// Registers explicit Dapper mappings for SQLite search-index query rows.
/// </summary>
internal static class SqliteSearchIndexTypeMaps
{
    static SqliteSearchIndexTypeMaps()
    {
        Register<SqliteSearchIndexMetadataRow>(
            ("schema_version", row => row.SchemaVersion),
            ("target_identity", row => row.TargetIdentity),
            ("fingerprint", row => row.Fingerprint),
            ("indexed_at_utc", row => row.IndexedAtUtc),
            ("symbol_count", row => row.SymbolCount),
            ("language", row => row.Language));

        Register<SqliteSearchIndexMatchRow>(
            ("symbol_key", row => row.SymbolKey),
            ("project_path", row => row.ProjectPath),
            ("project_name", row => row.ProjectName),
            ("language", row => row.Language),
            ("kind", row => row.Kind),
            ("name", row => row.Name),
            ("display_name", row => row.DisplayName),
            ("symbol_id", row => row.SymbolId),
            ("path", row => row.Path),
            ("line", row => row.Line),
            ("column_number", row => row.Column),
            ("end_line", row => row.EndLine),
            ("end_column_number", row => row.EndColumn),
            ("documentation", row => row.Documentation),
            ("signature", row => row.Signature),
            ("comments", row => row.Comments),
            ("body", row => row.Body),
            ("bm25_score", row => row.RawBm25Score),
            ("query_term_coverage", row => row.QueryTermCoverage));
    }

    internal static void EnsureRegistered()
    {
    }

    private static void Register<T>(
        params (string ColumnName, Expression<Func<T, object?>> Selector)[] mappings)
    {
        Dictionary<string, PropertyInfo> properties = mappings.ToDictionary(
            mapping => mapping.ColumnName,
            mapping => Property(mapping.Selector),
            StringComparer.OrdinalIgnoreCase);

        SqlMapper.SetTypeMap(typeof(T), new ExplicitColumnTypeMap<T>(properties));
    }

    private static PropertyInfo Property<T>(Expression<Func<T, object?>> selector)
    {
        Expression body = selector.Body is UnaryExpression conversion
            ? conversion.Operand
            : selector.Body;

        return body is MemberExpression { Member: PropertyInfo property }
            ? property
            : throw new ArgumentException("Selector must reference a property.", nameof(selector));
    }

    /// <summary>
    /// Rejects query columns that do not have an explicit property mapping.
    /// </summary>
    private sealed class ExplicitColumnTypeMap<T> : SqlMapper.ITypeMap
    {
        private readonly DefaultTypeMap _constructorMap = new(typeof(T));
        private readonly CustomPropertyTypeMap _propertyMap;

        internal ExplicitColumnTypeMap(IReadOnlyDictionary<string, PropertyInfo> properties)
        {
            _propertyMap = new CustomPropertyTypeMap(
                typeof(T),
                (_, columnName) => properties.GetValueOrDefault(columnName)!);
        }

        public ConstructorInfo? FindConstructor(string[] names, Type[] types)
        {
            return _constructorMap.FindConstructor(names, types);
        }

        public ConstructorInfo? FindExplicitConstructor()
        {
            return _constructorMap.FindExplicitConstructor();
        }

        public SqlMapper.IMemberMap? GetConstructorParameter(ConstructorInfo constructor, string columnName)
        {
            return _constructorMap.GetConstructorParameter(constructor, columnName);
        }

        public SqlMapper.IMemberMap? GetMember(string columnName)
        {
            return _propertyMap.GetMember(columnName)
                ?? throw new InvalidOperationException(
                    $"Column '{columnName}' has no mapping for {typeof(T).Name}.");
        }
    }
}

/// <summary>
/// Holds primitive persisted metadata values before domain validation.
/// </summary>
internal sealed class SqliteSearchIndexMetadataRow
{
    public int SchemaVersion { get; set; }

    public string TargetIdentity { get; set; } = string.Empty;

    public string? Fingerprint { get; set; }

    public string IndexedAtUtc { get; set; } = string.Empty;

    public int SymbolCount { get; set; }

    public string Language { get; set; } = SourceLanguageNames.CSharp;
}

/// <summary>
/// Holds primitive persisted search values before domain validation and excerpt selection.
/// </summary>
internal sealed class SqliteSearchIndexMatchRow
{
    public string SymbolKey { get; set; } = string.Empty;

    public string ProjectPath { get; set; } = string.Empty;

    public string ProjectName { get; set; } = string.Empty;

    public string Language { get; set; } = SourceLanguageNames.CSharp;

    public string Kind { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string? SymbolId { get; set; }

    public string Path { get; set; } = string.Empty;

    public int Line { get; set; }

    public int Column { get; set; }

    public int EndLine { get; set; }

    public int EndColumn { get; set; }

    public string? Documentation { get; set; }

    public string? Signature { get; set; }

    public string? Comments { get; set; }

    public string? Body { get; set; }

    public double RawBm25Score { get; set; }

    public int QueryTermCoverage { get; set; }
}
