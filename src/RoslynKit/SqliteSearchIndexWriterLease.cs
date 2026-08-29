using Microsoft.Data.Sqlite;

namespace RoslynKit;

/// <summary>
/// Holds the database-wide immediate SQLite write transaction for one coherent search-index refresh.
/// </summary>
internal sealed class SqliteSearchIndexWriterLease : IAsyncDisposable
{
    private readonly SqliteConnection _connection;
    private readonly SqliteTransaction _transaction;
    private bool _committed;
    private bool _disposed;

    internal SqliteSearchIndexWriterLease(SqliteConnection connection, SqliteTransaction transaction)
    {
        _connection = connection;
        _transaction = transaction;
    }

    /// <summary>
    /// Reads target metadata from the same transaction that holds the writer lease.
    /// </summary>
    public Task<SqliteSearchIndexMetadata?> ReadMetadataAsync(
        RepositoryRelativePath targetIdentity,
        CancellationToken cancellationToken)
    {
        ThrowIfUnavailable();
        return SqliteSearchIndex.ReadMetadataAsync(_connection, _transaction, targetIdentity, cancellationToken);
    }

    /// <summary>
    /// Replaces one target partition without publishing it until the lease commits.
    /// </summary>
    public Task ReplaceTargetAsync(
        SqliteSearchIndexTarget target,
        IReadOnlyCollection<SqliteSearchIndexSymbol> symbols,
        CancellationToken cancellationToken)
    {
        return ReplaceTargetAsync(
            target,
            symbols,
            symbols
                .Select(symbol => new SqliteSearchIndexProject(symbol.ProjectPath, symbol.ProjectName, []))
                .DistinctBy(project => project.Path)
                .ToArray(),
            cancellationToken);
    }

    /// <summary>
    /// Replaces one target partition and its project metadata without publishing it until commit.
    /// </summary>
    public Task ReplaceTargetAsync(
        SqliteSearchIndexTarget target,
        IReadOnlyCollection<SqliteSearchIndexSymbol> symbols,
        IReadOnlyCollection<SqliteSearchIndexProject> projects,
        CancellationToken cancellationToken)
    {
        ThrowIfUnavailable();
        return SqliteSearchIndex.ReplaceTargetWithinLeaseAsync(
            _connection,
            _transaction,
            target,
            symbols,
            projects,
            cancellationToken);
    }

    /// <summary>
    /// Replaces selected project partitions without publishing them until the lease commits.
    /// </summary>
    public Task ReplaceProjectsAsync(
        SqliteSearchIndexTarget target,
        IReadOnlyCollection<RepositoryRelativePath> projectPaths,
        IReadOnlyCollection<SqliteSearchIndexSymbol> symbols,
        CancellationToken cancellationToken)
    {
        return ReplaceProjectsAsync(
            target,
            projectPaths,
            symbols,
            symbols
                .Select(symbol => new SqliteSearchIndexProject(symbol.ProjectPath, symbol.ProjectName, []))
                .DistinctBy(project => project.Path)
                .ToArray(),
            cancellationToken);
    }

    /// <summary>
    /// Replaces selected project partitions and their project metadata without publishing until commit.
    /// </summary>
    public Task ReplaceProjectsAsync(
        SqliteSearchIndexTarget target,
        IReadOnlyCollection<RepositoryRelativePath> projectPaths,
        IReadOnlyCollection<SqliteSearchIndexSymbol> symbols,
        IReadOnlyCollection<SqliteSearchIndexProject> projects,
        CancellationToken cancellationToken)
    {
        ThrowIfUnavailable();
        return SqliteSearchIndex.ReplaceProjectsWithinLeaseAsync(
            _connection,
            _transaction,
            target,
            projectPaths,
            symbols,
            projects,
            cancellationToken);
    }

    /// <summary>
    /// Updates a target fingerprint and timestamp while preserving the currently indexed symbol count.
    /// </summary>
    public Task UpdateTargetMetadataAsync(SqliteSearchIndexTarget target, CancellationToken cancellationToken)
    {
        ThrowIfUnavailable();
        return SqliteSearchIndex.UpdateTargetMetadataWithinLeaseAsync(
            _connection,
            _transaction,
            target,
            cancellationToken);
    }

    /// <summary>
    /// Writes one semantic operation result without publishing it until the lease commits.
    /// </summary>
    public Task WriteCatalogOperationAsync(
        RepositoryRelativePath targetIdentity,
        string operationKey,
        string resultType,
        int formatVersion,
        string payloadJson,
        CancellationToken cancellationToken)
    {
        ThrowIfUnavailable();
        return SqliteSearchIndex.WriteCatalogOperationWithinLeaseAsync(
            _connection,
            _transaction,
            targetIdentity,
            operationKey,
            resultType,
            formatVersion,
            payloadJson,
            cancellationToken);
    }

    /// <summary>
    /// Makes all lease changes visible to concurrent SQLite readers.
    /// </summary>
    public async Task CommitAsync(CancellationToken cancellationToken)
    {
        ThrowIfUnavailable();
        await _transaction.CommitAsync(cancellationToken);
        _committed = true;
    }

    /// <summary>
    /// Rolls back any uncommitted changes before releasing the SQLite writer lease.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            if (!_committed)
            {
                await _transaction.RollbackAsync(CancellationToken.None);
            }
        }
        finally
        {
            await _transaction.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private void ThrowIfUnavailable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_committed)
        {
            throw new InvalidOperationException("The SQLite search-index writer lease has already committed.");
        }
    }
}

/// <summary>
/// Indicates that another process owns the SQLite search-index writer lease.
/// </summary>
internal sealed class SqliteSearchIndexWriterLeaseUnavailableException : InvalidOperationException
{
    public SqliteSearchIndexWriterLeaseUnavailableException(TimeSpan timeout, SqliteException innerException)
        : base($"The SQLite search-index writer lease could not be acquired within {timeout.TotalMilliseconds:0} ms.", innerException)
    {
        Timeout = timeout;
    }

    public TimeSpan Timeout { get; }
}
