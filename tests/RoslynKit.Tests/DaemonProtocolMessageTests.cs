namespace RoslynKit.Tests;

public sealed class DaemonProtocolMessageTests
{
    [Fact]
    public async Task Messages_RoundTripEveryRequestAndResponseShape()
    {
        var requestId = Guid.Parse("10cb0757-c567-4276-a1a8-ec566d54cdcd");
        var version = RoslynKitBuildInfo.DaemonProtocolVersion;
        DaemonRequest[] requests =
        [
            new DaemonHandshakeRequest(version, requestId),
            new DaemonCommandRequest(
                version,
                requestId,
                "symbols",
                new Dictionary<string, string>(StringComparer.Ordinal) { ["query"] = "Protocol" },
                DateTimeOffset.Parse("2026-07-30T12:00:00+00:00")),
            new DaemonStatusRequest(version, requestId),
            new DaemonStopRequest(version, requestId),
        ];
        DaemonResponse[] responses =
        [
            new DaemonHandshakeResponse(version, requestId, Accepted: true, Diagnostic: null),
            new DaemonCommandResponse(version, requestId, 0, "output\n", string.Empty),
            new DaemonStatusResponse(version, requestId, Running: true, "C:\\repo\\RoslynKit.slnx", 123, "ready", 4, 2, 1, null),
            new DaemonStopResponse(version, requestId, Stopping: true),
        ];

        foreach (var request in requests)
        {
            await using var stream = new MemoryStream();
            await DaemonProtocol.WriteRequestAsync(stream, request, TestContext.Current.CancellationToken);
            stream.Position = 0;

            var decoded = await DaemonProtocol.ReadRequestAsync(stream, TestContext.Current.CancellationToken);
            Assert.Equal(request.ProtocolVersion, decoded.ProtocolVersion);
            Assert.Equal(request.RequestId, decoded.RequestId);
            Assert.Equal(request.GetType(), decoded.GetType());
            if (request is DaemonCommandRequest expectedCommand)
            {
                var actualCommand = Assert.IsType<DaemonCommandRequest>(decoded);
                Assert.Equal(expectedCommand.CommandName, actualCommand.CommandName);
                Assert.Equal(expectedCommand.Options, actualCommand.Options);
                Assert.Equal(expectedCommand.DeadlineUtc, actualCommand.DeadlineUtc);
            }
            else
            {
                Assert.Equal(request, decoded);
            }
        }

        foreach (var response in responses)
        {
            await using var stream = new MemoryStream();
            await DaemonProtocol.WriteResponseAsync(stream, response, TestContext.Current.CancellationToken);
            stream.Position = 0;

            Assert.Equal(response, await DaemonProtocol.ReadResponseAsync(stream, TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public void EnsureCompatible_RejectsDifferentProtocolVersion()
    {
        var request = new DaemonHandshakeRequest(
            RoslynKitBuildInfo.DaemonProtocolVersion + 1,
            Guid.NewGuid());

        var exception = Assert.Throws<DaemonProtocolException>(() => DaemonProtocol.EnsureCompatible(request));

        Assert.Equal(DaemonProtocolError.UnsupportedVersion, exception.Error);
    }

    [Fact]
    public void CommandResponse_PreservesExactBufferedProcessResult()
    {
        var requestId = Guid.NewGuid();
        var expected = new CliProcessResult(7, "partial-looking stdout", "warning text");

        var response = DaemonCommandResponse.Create(requestId, expected);

        Assert.Equal(requestId, response.RequestId);
        Assert.Equal(expected, response.ToProcessResult());
    }

    [Fact]
    public void CommandRequest_CreateCanonicalizesPathOptionsAndRevalidatesOnServer()
    {
        var repositoryRoot = TestPaths.RepositoryRoot();
        var solutionPath = Path.Combine(".", "RoslynKit.slnx");
        var projectPath = Path.Combine(".", "src", "RoslynKit", "RoslynKit.csproj");
        var filePath = Path.Combine(".", "src", "RoslynKit", "CliApplication.cs");
        var parsed = CliParser.Parse(
        [
            "document-lines",
            "--target", solutionPath,
            "--project", projectPath,
            "--file", filePath,
            "--start-line", "1",
            "--end-line", "10",
        ]);
        var request = DaemonCommandRequest.Create(
            parsed,
            Guid.Parse("10cb0757-c567-4276-a1a8-ec566d54cdcd"),
            DateTimeOffset.Parse("2026-07-30T20:00:00+08:00"),
            repositoryRoot);

        Assert.Equal(TestPaths.SolutionPath(), request.Options["target"]);
        Assert.Equal(TestPaths.RepoFile("src", "RoslynKit", "RoslynKit.csproj"), request.Options["project"]);
        Assert.Equal(TestPaths.RepoFile("src", "RoslynKit", "CliApplication.cs"), request.Options["file"]);
        Assert.Equal(TimeSpan.Zero, request.DeadlineUtc.Offset);
        Assert.Equal(DateTimeOffset.Parse("2026-07-30T12:00:00+00:00"), request.DeadlineUtc);

        var reconstructed = request.ToParsedCommand();
        Assert.Equal(parsed.Name, reconstructed.Name);
        Assert.Equal("1", reconstructed.Options["start-line"]);
        Assert.Equal(request.Options["target"], reconstructed.Options["target"]);
    }

    [Fact]
    public void CommandRequest_ToParsedCommandRejectsUnknownOptions()
    {
        var request = new DaemonCommandRequest(
            RoslynKitBuildInfo.DaemonProtocolVersion,
            Guid.NewGuid(),
            "symbols",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["not-an-option"] = "value" },
            DateTimeOffset.UtcNow);

        Assert.Throws<CliUsageException>(() => request.ToParsedCommand());
    }

    [Theory]
    [InlineData("help")]
    [InlineData("version")]
    [InlineData("init")]
    [InlineData("daemon status")]
    [InlineData("daemon stop")]
    public void CommandRequest_CreateRejectsLocalCommands(string commandName)
    {
        var parsed = new ParsedCommand(
            commandName,
            BuiltinCommandRegistry.GetBuiltin(commandName),
            new Dictionary<string, string>(StringComparer.Ordinal),
            HelpSubject: null);

        var exception = Assert.Throws<DaemonProtocolException>(
            () => DaemonCommandRequest.Create(parsed, Guid.NewGuid(), DateTimeOffset.UtcNow));

        Assert.Equal(DaemonProtocolError.InvalidMessage, exception.Error);
    }

    [Theory]
    [InlineData("help")]
    [InlineData("version")]
    [InlineData("init")]
    [InlineData("daemon status")]
    [InlineData("daemon stop")]
    public void CommandRequest_ToParsedCommandRejectsLocalCommands(string commandName)
    {
        var request = new DaemonCommandRequest(
            RoslynKitBuildInfo.DaemonProtocolVersion,
            Guid.NewGuid(),
            commandName,
            new Dictionary<string, string>(StringComparer.Ordinal),
            DateTimeOffset.UtcNow);

        var exception = Assert.Throws<DaemonProtocolException>(() => request.ToParsedCommand());

        Assert.Equal(DaemonProtocolError.InvalidMessage, exception.Error);
    }
}
