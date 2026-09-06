using System.Text;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.Text;

namespace RoslynKit.Tests;

/// <summary>
/// Verifies retained workspace revisions, file reconciliation, and reader ownership without MSBuild dependencies.
/// </summary>
public sealed class RepositoryWorkspaceSessionTests
{
    [Fact]
    public async Task Acquire_ReusesLazyWorkspaceAndFreezesEveryLinkedDocumentUntilRefresh()
    {
        using var files = new WorkspaceFiles(linkedProjects: true);
        var loads = 0;
        var preparations = 0;
        await using var session = files.CreateSession(
            _ => Task.FromResult(files.Load(++loads)),
            _ =>
            {
                preparations++;
                return Task.FromResult<IReadOnlyList<string>>([]);
            });
        Assert.Equal(0, loads);
        using var original = await session.AcquireAsync(Token);
        using var repeated = await session.AcquireAsync(Token);
        Assert.Same(original.Snapshot, repeated.Snapshot);

        await File.WriteAllTextAsync(files.SourcePath, "class After_ { }", Token);
        using var undetected = await session.AcquireAsync(Token);
        Assert.Equal(original.Revision, undetected.Revision);
        Assert.All(await TextsAsync(original), text => Assert.Contains("Before", text));
        Assert.All(await TextsAsync(undetected), text => Assert.Contains("Before", text));

        var revision = await session.SynchronizeAsync(Token);
        using var updated = await session.AcquireAsync(Token);
        Assert.True(revision > original.Revision);
        Assert.Equal(2, (await TextsAsync(updated)).Count);
        Assert.All(await TextsAsync(updated), text => Assert.Contains("After_", text));
        Assert.All(await TextsAsync(original), text => Assert.Contains("Before", text));
        Assert.Equal(1, loads);
        Assert.Equal(1, preparations);
        Assert.NotEqual(original.Fingerprint, updated.Fingerprint);
        Assert.Contains("Before", (await files.Workspaces[0].CurrentSolution.Projects.First().Documents.First().GetTextAsync(Token)).ToString());
    }

    [Fact]
    public async Task Reconciliation_ReusesUnchangedTextAndSyntaxTreesWhileApplyingOnlyTheDetectedTextDelta()
    {
        using var files = new WorkspaceFiles();
        var unchangedPath = files.PathFor("Unchanged.cs");
        await File.WriteAllTextAsync(unchangedPath, "class Unchanged { }", Token);
        await using var session = files.CreateSession(_ => Task.FromResult(files.Load(1)));
        using var original = await session.AcquireAsync(Token);
        var oldProject = original.Snapshot.Solution.Projects.Single();
        var oldChanged = oldProject.Documents.Single(document => document.FilePath == files.SourcePath);
        var oldUnchanged = oldProject.Documents.Single(document => document.FilePath == unchangedPath);
        var oldChangedText = await oldChanged.GetTextAsync(Token);
        var unchangedText = await oldUnchanged.GetTextAsync(Token);
        var unchangedTree = await oldUnchanged.GetSyntaxTreeAsync(Token);
        var unchangedVersion = await oldUnchanged.GetTextVersionAsync(Token);
        var oldCompilation = await oldProject.GetCompilationAsync(Token);
        await File.WriteAllTextAsync(files.SourcePath, "class After_ { }", Token);

        await session.SynchronizeAsync(Token);

        using var updated = await session.AcquireAsync(Token);
        var newProject = updated.Snapshot.Solution.Projects.Single();
        var newChanged = newProject.GetDocument(oldChanged.Id)!;
        var newUnchanged = newProject.GetDocument(oldUnchanged.Id)!;
        var changedText = await newChanged.GetTextAsync(Token);
        var delta = Assert.Single(changedText.GetTextChanges(oldChangedText));
        Assert.Equal(new TextSpan(6, 6), delta.Span);
        Assert.Equal("After_", delta.NewText);
        Assert.Same(unchangedText, await newUnchanged.GetTextAsync(Token));
        Assert.Same(unchangedTree, await newUnchanged.GetSyntaxTreeAsync(Token));
        Assert.Equal(unchangedVersion, await newUnchanged.GetTextVersionAsync(Token));
        var newCompilation = await newProject.GetCompilationAsync(Token);
        Assert.NotSame(oldCompilation, newCompilation);
        Assert.Contains(unchangedTree, newCompilation!.SyntaxTrees);
    }

    [Theory]
    [InlineData("addition")]
    [InlineData("deletion")]
    [InlineData("rename")]
    [InlineData("project")]
    [InlineData("generated")]
    [InlineData("config")]
    public async Task Synchronize_ReplacesWorkspaceForStructuralInputChanges(string change)
    {
        using var files = new WorkspaceFiles();
        await File.WriteAllTextAsync(files.PathFor("obj", "Generated.g.cs"), "class Generated { }", Token);
        var loads = 0;
        await using var session = files.CreateSession(_ => Task.FromResult(files.Load(++loads)));
        using var original = await session.AcquireAsync(Token);

        switch (change)
        {
            case "addition":
                await File.WriteAllTextAsync(files.PathFor("Added.cs"), "class Added { }", Token);
                break;
            case "deletion":
                File.Delete(files.SourcePath);
                break;
            case "rename":
                File.Move(files.SourcePath, files.PathFor("Renamed.cs"));
                break;
            case "project":
                await File.WriteAllTextAsync(files.ProjectPath, "<Project><PropertyGroup /></Project>", Token);
                break;
            case "generated":
                await File.WriteAllTextAsync(files.PathFor("obj", "Generated.g.cs"), "class Regenerated { }", Token);
                break;
            case "config":
                await File.WriteAllTextAsync(files.PathFor(".editorconfig"), "root = true\n", Token);
                break;
        }

        await session.SynchronizeAsync(Token);
        using var current = await session.AcquireAsync(Token);
        Assert.Equal(2, loads);
        Assert.NotSame(original.Snapshot.Workspace, current.Snapshot.Workspace);
        Assert.False(files.Workspaces[0].WasDisposed);
        original.Dispose();
        Assert.True(files.Workspaces[0].WasDisposed);
    }

    [Fact]
    public async Task Acquire_WaitsForKnownReplacementWhileOldLeaseRemainsUsable()
    {
        using var files = new WorkspaceFiles();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var loads = 0;
        await using var session = files.CreateSession(async token =>
        {
            if (++loads == 2)
            {
                entered.SetResult();
                await resume.Task.WaitAsync(token);
            }

            return files.Load(loads);
        });
        using var original = await session.AcquireAsync(Token);
        await File.WriteAllTextAsync(files.PathFor("Directory.Build.props"), "<Project />", Token);
        session.NotifyFileChanged(files.PathFor("Directory.Build.props"));
        await entered.Task.WaitAsync(Token);
        var pending = session.AcquireAsync(Token);
        Assert.False(pending.IsCompleted);
        Assert.False(session.IsCurrent(original.Revision));
        Assert.False(session.TryBeginRetirement());
        Assert.Contains("Before", Assert.Single(await TextsAsync(original)));
        resume.SetResult();

        using var current = await pending;
        Assert.True(current.Revision > original.Revision);
        Assert.False(files.Workspaces[0].WasDisposed);
        original.Dispose();
        Assert.True(files.Workspaces[0].WasDisposed);
    }

    [Fact]
    public async Task FailedReplacement_RejectsNewQueriesAndDoesNotRetryOnEveryAcquire()
    {
        using var files = new WorkspaceFiles();
        var loads = 0;
        var fail = false;
        await using var session = files.CreateSession(_ =>
        {
            loads++;
            return fail
                ? Task.FromException<RoslynWorkspaceLoader>(new InvalidOperationException("replacement failed"))
                : Task.FromResult(files.Load(loads));
        });
        using var original = await session.AcquireAsync(Token);
        fail = true;
        await File.WriteAllTextAsync(files.PathFor("Added.cs"), "class Added { }", Token);
        session.NotifyFileChanged(files.PathFor("Added.cs"), WatcherChangeTypes.Created);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => session.AcquireAsync(Token));
        Assert.Equal("replacement failed", error.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.AcquireAsync(Token));
        Assert.Equal(2, loads);
        Assert.Contains("Before", Assert.Single(await TextsAsync(original)));

        fail = false;
        await session.SynchronizeAsync(Token);
        using var recovered = await session.AcquireAsync(Token);
        Assert.Equal(3, loads);
        Assert.True(recovered.Revision > original.Revision);
    }

    [Fact]
    public async Task FailedReplacement_ReplaysCorrectionsAlreadyQueuedDuringTheFailedLoad()
    {
        using var files = new WorkspaceFiles();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fail = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var loads = 0;
        await using var session = files.CreateSession(async token =>
        {
            if (++loads == 2)
            {
                entered.SetResult();
                await fail.Task.WaitAsync(token);
                throw new InvalidOperationException("temporary project failure");
            }

            return files.Load(loads);
        });
        using var original = await session.AcquireAsync(Token);
        await File.WriteAllTextAsync(files.PathFor("Added.cs"), "class Added { }", Token);
        session.NotifyFileChanged(files.PathFor("Added.cs"), WatcherChangeTypes.Created);
        await entered.Task.WaitAsync(Token);
        await File.WriteAllTextAsync(files.SourcePath, "class Corrected { }", Token);
        session.NotifyFileChanged(files.SourcePath);
        fail.SetResult();

        using var recovered = await session.AcquireAsync(Token);
        Assert.Equal(3, loads);
        Assert.Contains(await TextsAsync(recovered), text => text.Contains("Corrected", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CancelledWaiter_DoesNotCancelTheSharedRefresh()
    {
        using var files = new WorkspaceFiles();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var session = files.CreateSession(async token =>
        {
            entered.SetResult();
            await resume.Task.WaitAsync(token);
            return files.Load(1);
        });
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var first = session.AcquireAsync(cancelled.Token);
        await entered.Task.WaitAsync(Token);
        var second = session.AcquireAsync(Token);
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.False(second.IsCompleted);
        resume.SetResult();

        using var current = await second;
        Assert.Equal(1, current.Revision);
    }

    [Fact]
    public async Task Dispose_RetainsWorkspaceUntilTheLastReaderReleasesIt()
    {
        using var files = new WorkspaceFiles();
        var session = files.CreateSession(_ => Task.FromResult(files.Load(1)));
        var lease = await session.AcquireAsync(Token);
        lease.Snapshot.Dispose();
        Assert.False(files.Workspaces[0].WasDisposed);
        await session.DisposeAsync();
        Assert.False(files.Workspaces[0].WasDisposed);
        Assert.Contains("Before", Assert.Single(await TextsAsync(lease)));
        lease.Dispose();
        lease.Dispose();
        Assert.True(files.Workspaces[0].WasDisposed);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.AcquireAsync(Token));
    }

    [Fact]
    public async Task Retirement_ReservesOnlyAnIdleSessionAndPreventsNewBackgroundWork()
    {
        using var files = new WorkspaceFiles();
        await using var session = files.CreateSession(_ => Task.FromResult(files.Load(1)));
        using var lease = await session.AcquireAsync(Token);
        Assert.False(session.TryBeginRetirement());
        lease.Dispose();
        Assert.True(session.TryBeginRetirement());
        await File.WriteAllTextAsync(files.SourcePath, "class After_ { }", Token);
        session.NotifyFileChanged(files.SourcePath);
        session.NotifyWatcherError();

        Assert.False(session.IsBusy);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.AcquireAsync(Token));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.SynchronizeAsync(Token));
    }

    [Fact]
    public async Task Fingerprint_DistinguishesEffectiveCompilationSettingsWithIdenticalFiles()
    {
        using var files = new WorkspaceFiles();
        await using var first = files.CreateSession(_ => Task.FromResult(files.Load(1)));
        using var before = await first.AcquireAsync(Token);
        files.ParseOptions = CSharpParseOptions.Default.WithPreprocessorSymbols("FEATURE_ENABLED");
        await using var second = files.CreateSession(_ => Task.FromResult(files.Load(1)));
        using var after = await second.AcquireAsync(Token);

        Assert.NotEqual(before.Fingerprint, after.Fingerprint);
        Assert.True(await first.ValidateSnapshotAsync(before, Token));
        Assert.True(await second.ValidateSnapshotAsync(after, Token));
    }

    [Fact]
    public async Task PeriodicReconciliation_DetectsMissedEditsWithUnchangedSizeAndTimestamp()
    {
        using var files = new WorkspaceFiles();
        await using var session = files.CreateSession(
            _ => Task.FromResult(files.Load(1)),
            reconciliationInterval: TimeSpan.FromMilliseconds(25));
        using var original = await session.AcquireAsync(Token);
        var timestamp = File.GetLastWriteTimeUtc(files.SourcePath);
        var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Changed += () => published.TrySetResult();
        await File.WriteAllTextAsync(files.SourcePath, "class After_ { }", Token);
        File.SetLastWriteTimeUtc(files.SourcePath, timestamp);
        await published.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);

        using var updated = await session.AcquireAsync(Token);
        Assert.Contains("After_", Assert.Single(await TextsAsync(updated)));
        Assert.NotEqual(original.Fingerprint, updated.Fingerprint);
    }

    [Fact]
    public async Task Watcher_ObservesExplicitSourceInsideAnOtherwiseExcludedOutputDirectory()
    {
        using var files = new WorkspaceFiles(sourceInBin: true);
        await using var session = files.CreateSession(_ => Task.FromResult(files.Load(1)), watchFiles: true);
        using var original = await session.AcquireAsync(Token);
        var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Changed += () => published.TrySetResult();
        await File.WriteAllTextAsync(files.SourcePath, "class After_ { }", Token);
        await published.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);

        using var updated = await session.AcquireAsync(Token);
        Assert.Contains("After_", Assert.Single(await TextsAsync(updated)));
        Assert.True(updated.Revision > original.Revision);
    }

    [Fact]
    public async Task Synchronize_DiscoversNewSourceAlongsideEvaluatedSourceInExcludedDirectories()
    {
        using var files = new WorkspaceFiles(sourceInBin: true);
        var loads = 0;
        await using var session = files.CreateSession(_ => Task.FromResult(files.Load(++loads)));
        using var original = await session.AcquireAsync(Token);
        await File.WriteAllTextAsync(files.PathFor("bin", "NewlyIncluded.cs"), "class NewlyIncluded { }", Token);
        await session.SynchronizeAsync(Token);

        using var updated = await session.AcquireAsync(Token);
        Assert.Equal(2, loads);
        Assert.Contains(await TextsAsync(updated), text => text.Contains("NewlyIncluded", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ValidateSnapshot_DetectsMissedChangesAndSchedulesRefresh()
    {
        using var files = new WorkspaceFiles();
        await using var session = files.CreateSession(_ => Task.FromResult(files.Load(1)));
        using var original = await session.AcquireAsync(Token);
        Assert.True(await session.ValidateSnapshotAsync(original, Token));
        await File.WriteAllTextAsync(files.SourcePath, "class After_ { }", Token);

        Assert.False(await session.ValidateSnapshotAsync(original, Token));
        using var updated = await session.AcquireAsync(Token);
        Assert.True(await session.ValidateSnapshotAsync(updated, Token));
        Assert.Contains("After_", Assert.Single(await TextsAsync(updated)));
    }

    [Fact]
    public async Task IndexOutputs_DoNotInvalidateSnapshotsAndCannotOverlapEvaluatedInputs()
    {
        using var files = new WorkspaceFiles();
        var output = files.PathFor("cache", "index.db");
        await using var session = files.CreateSession(_ => Task.FromResult(files.Load(1)));
        session.ExcludeOutputPath(output);
        using var original = await session.AcquireAsync(Token);

        foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
        {
            await File.WriteAllTextAsync(output + suffix, "index output", Token);
            session.NotifyFileChanged(output + suffix, WatcherChangeTypes.Created);
        }

        Assert.True(session.IsCurrent(original.Revision));
        Assert.True(await session.ValidateSnapshotAsync(original, Token));
        Assert.Throws<InvalidOperationException>(() => session.ExcludeOutputPath(files.SourcePath));
    }

    [Fact]
    public async Task OverflowRecovery_ReplacesWorkspaceForExternalAdditionalFileChanges()
    {
        using var files = new WorkspaceFiles();
        var externalPath = Path.Combine(files.TestDirectory, "external", "notes.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(externalPath)!);
        await File.WriteAllTextAsync(externalPath, "original generator input", Token);
        files.AdditionalPath = externalPath;
        var loads = 0;
        await using var session = files.CreateSession(_ => Task.FromResult(files.Load(++loads)));
        using var original = await session.AcquireAsync(Token);
        await File.WriteAllTextAsync(externalPath, "changed generator input", Token);
        session.NotifyWatcherError();

        using var updated = await session.AcquireAsync(Token);
        Assert.Equal(2, loads);
        Assert.Contains("original", (await original.Snapshot.Solution.Projects.Single().AdditionalDocuments.Single().GetTextAsync(Token)).ToString());
        Assert.Contains("changed", (await updated.Snapshot.Solution.Projects.Single().AdditionalDocuments.Single().GetTextAsync(Token)).ToString());
    }

    [Fact]
    public async Task MetadataReplacement_PreservesOldReferenceImagesAndDocumentationForExistingReaders()
    {
        using var files = new WorkspaceFiles();
        files.MetadataPath = files.PathFor("lib", "Dependency.dll");
        WriteReference("ReferenceBefore", "original documentation");
        var timestamp = File.GetLastWriteTimeUtc(files.MetadataPath);
        var loads = 0;
        await using var session = files.CreateSession(_ => Task.FromResult(files.Load(++loads)));
        using var original = await session.AcquireAsync(Token);
        WriteReference("ReferenceAfter", "updated documentation");
        File.SetLastWriteTimeUtc(files.MetadataPath, timestamp);
        await session.SynchronizeAsync(Token);
        using var updated = await session.AcquireAsync(Token);

        var oldCompilation = await original.Snapshot.Solution.Projects.Single().GetCompilationAsync(Token);
        var newCompilation = await updated.Snapshot.Solution.Projects.Single().GetCompilationAsync(Token);
        var oldType = Assert.IsAssignableFrom<INamedTypeSymbol>(oldCompilation!.GetTypeByMetadataName("ReferenceBefore"));
        var newType = Assert.IsAssignableFrom<INamedTypeSymbol>(newCompilation!.GetTypeByMetadataName("ReferenceAfter"));
        Assert.Null(oldCompilation.GetTypeByMetadataName("ReferenceAfter"));
        Assert.Null(newCompilation.GetTypeByMetadataName("ReferenceBefore"));
        Assert.Contains("original documentation", oldType.GetDocumentationCommentXml(cancellationToken: Token));
        Assert.Contains("updated documentation", newType.GetDocumentationCommentXml(cancellationToken: Token));
        Assert.Equal(2, loads);

        void WriteReference(string typeName, string documentation)
        {
            var compilation = CSharpCompilation.Create("Dependency",
                [CSharpSyntaxTree.ParseText($"public class {typeName} {{ }}", cancellationToken: Token)],
                [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            using var output = File.Create(files.MetadataPath);
            var result = compilation.Emit(output, cancellationToken: Token);
            Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
            File.WriteAllText(Path.ChangeExtension(files.MetadataPath, ".xml"),
                $"<doc><members><member name=\"T:{typeName}\"><summary>{documentation}</summary></member></members></doc>");
        }
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static async Task<IReadOnlyList<string>> TextsAsync(WorkspaceSnapshotLease lease)
    {
        var texts = new List<string>();
        foreach (var document in lease.Snapshot.Solution.Projects.SelectMany(project => project.Documents))
        {
            texts.Add((await document.GetTextAsync(Token)).ToString());
        }

        return texts;
    }

    /// <summary>
    /// Creates isolated source files and instrumented Roslyn workspaces for session lifecycle tests.
    /// </summary>
    private sealed class WorkspaceFiles : IDisposable
    {
        private readonly bool _linkedProjects;

        internal WorkspaceFiles(bool linkedProjects = false, bool sourceInBin = false)
        {
            _linkedProjects = linkedProjects;
            TestDirectory = TestPaths.RepoFile("artifacts", "workspace-runtime-tests", Guid.NewGuid().ToString("N"));
            Root = Path.Combine(TestDirectory, "repo");
            Directory.CreateDirectory(Root);
            ProjectPath = PathFor("App.csproj");
            File.WriteAllText(ProjectPath, "<Project />");
            if (linkedProjects)
            {
                File.WriteAllText(PathFor("Linked.csproj"), "<Project />");
            }

            SourcePath = sourceInBin ? PathFor("bin", "Consumed.cs") : PathFor("Source.cs");
            File.WriteAllText(SourcePath, "class Before { }");
        }

        internal string TestDirectory { get; }
        internal string Root { get; }
        internal string ProjectPath { get; }
        internal string SourcePath { get; }
        internal string? AdditionalPath { get; set; }
        internal string? MetadataPath { get; set; }
        internal CSharpParseOptions ParseOptions { get; set; } = CSharpParseOptions.Default;
        internal List<TrackingWorkspace> Workspaces { get; } = [];

        internal string PathFor(params string[] parts)
        {
            var path = Path.Combine([Root, .. parts]);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            return path;
        }

        internal RepositoryWorkspaceSession CreateSession(
            Func<CancellationToken, Task<RoslynWorkspaceLoader>> load,
            Func<CancellationToken, Task<IReadOnlyList<string>>>? prepare = null,
            TimeSpan? reconciliationInterval = null,
            bool watchFiles = false)
        {
            return new RepositoryWorkspaceSession(Root, null, load, prepare,
                reconciliationInterval ?? Timeout.InfiniteTimeSpan, TimeSpan.Zero, watchFiles);
        }

        internal RoslynWorkspaceLoader Load(int generation)
        {
            var workspace = new TrackingWorkspace();
            Workspaces.Add(workspace);
            var solution = workspace.CurrentSolution;
            for (var index = 0; index < (_linkedProjects ? 2 : 1); index++)
            {
                var id = ProjectId.CreateNewId();
                solution = solution.AddProject(ProjectInfo.Create(
                    id, VersionStamp.Create(), $"App{index}-{generation}", $"App{index}", LanguageNames.CSharp,
                    filePath: index == 0 ? ProjectPath : PathFor("Linked.csproj"),
                    compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
                    parseOptions: ParseOptions));
                if (MetadataPath is not null)
                {
                    solution = solution.WithProjectMetadataReferences(id,
                        [MetadataReference.CreateFromFile(typeof(object).Assembly.Location), MetadataReference.CreateFromFile(MetadataPath)]);
                }
                foreach (var path in Directory.EnumerateFiles(Root, "*.cs", SearchOption.AllDirectories))
                {
                    solution = solution.AddDocument(DocumentId.CreateNewId(id), Path.GetFileName(path),
                        SourceText.From(File.ReadAllText(path), Encoding.UTF8), filePath: path);
                }

                if (AdditionalPath is not null)
                {
                    solution = solution.AddAdditionalDocument(DocumentId.CreateNewId(id), Path.GetFileName(AdditionalPath),
                        SourceText.From(File.ReadAllText(AdditionalPath), Encoding.UTF8), filePath: AdditionalPath);
                }
            }

            workspace.SetSolution(solution);
            return new RoslynWorkspaceLoader(workspace, solution, Root, "repository", Root,
                new Dictionary<ProjectId, string?>(), []);
        }

        public void Dispose()
        {
            Directory.Delete(TestDirectory, recursive: true);
        }
    }

    /// <summary>
    /// Records owner disposal while keeping immutable solution reads available to the test fixture.
    /// </summary>
    private sealed class TrackingWorkspace() : Workspace(MefHostServices.DefaultHost, "session-test")
    {
        internal bool WasDisposed { get; private set; }

        internal void SetSolution(Solution solution) => SetCurrentSolution(solution);

        protected override void Dispose(bool finalize)
        {
            WasDisposed = true;
            base.Dispose(finalize);
        }
    }
}
