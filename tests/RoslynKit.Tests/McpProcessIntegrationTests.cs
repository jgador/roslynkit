using System.Diagnostics;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace RoslynKit.Tests;

/// <summary>
/// Exercises a real standard-input/output server, retained child workspaces, saved-file refresh, and SDK isolation.
/// </summary>
public sealed class McpProcessIntegrationTests
{
    [Fact]
    public async Task Serve_QueriesTwoRepositoriesWithDifferentSdksAndExitsOnDisconnect()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, TestContext.Current.CancellationToken);
        var token = cancellation.Token;
        var versions = await InstalledSdkVersionsAsync(token);
        if (versions.Length < 2)
        {
            Assert.Skip("Two installed .NET 10 SDK versions are required to verify process-global MSBuild isolation.");
        }

        await using var first = await RepositoryProcessTestArea.CreateAsync(token);
        await using var second = await RepositoryProcessTestArea.CreateAsync(token);
        await PrepareSdkFixtureAsync(first, versions[0], token);
        await PrepareSdkFixtureAsync(second, versions[^1], token);
        var newerSdk = await DotnetSdkResolver.ResolveAsync(second.RootPath, token);
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = Path.GetTempPath(),
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(typeof(CliParser).Assembly.Location);
        startInfo.ArgumentList.Add("serve");
        startInfo.ArgumentList.Add("--max-workspaces");
        startInfo.ArgumentList.Add("2");
        startInfo.Environment["MSBUILD_EXE_PATH"] = Path.Combine(newerSdk.SdkDirectory, "MSBuild.dll");
        startInfo.Environment["MSBuildExtensionsPath"] = newerSdk.SdkDirectory;
        startInfo.Environment["MSBuildSDKsPath"] = Path.Combine(newerSdk.SdkDirectory, "Sdks");
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start the server.");
        var errors = process.StandardError.ReadToEndAsync(token);
        try
        {
            await using (var client = await McpClient.CreateAsync(
                new StreamClientTransport(process.StandardInput.BaseStream, process.StandardOutput.BaseStream),
                new McpClientOptions { ProtocolVersion = "2025-11-25" }, cancellationToken: token))
            {
                var tools = await client.ListToolsAsync(cancellationToken: token);
                Assert.Equal(["help", "query"], tools.Select(tool => tool.Name));
                var queries = await Task.WhenAll(
                    QueryAsync(client, first.RootPath, ["symbols", "--query", "ExpectedSdkMarker", "--exact"], token),
                    QueryAsync(client, second.RootPath, ["symbols", "--query", "ExpectedSdkMarker", "--exact"], token));
                AssertSuccess(queries[0], first.RootPath, "ExpectedSdkMarker");
                AssertSuccess(queries[1], second.RootPath, "ExpectedSdkMarker");
                Assert.DoesNotContain("WrongSdkMarker", McpServerTests.Text(queries[0]), StringComparison.Ordinal);
                Assert.DoesNotContain("WrongSdkMarker", McpServerTests.Text(queries[1]), StringComparison.Ordinal);

                var refresh = await QueryAsync(client, first.RootPath, ["refresh"], token);
                AssertSuccess(refresh, first.RootPath, "status: synchronized");
                var sameRevision = await QueryAsync(client, first.RootPath, ["refresh"], token);
                Assert.Equal(McpServerTests.Text(refresh), McpServerTests.Text(sameRevision));

                await first.WriteSourceAsync("public sealed class SavedReplacementMarker { }\n", token);
                var changed = await QueryAsync(client, first.RootPath, ["refresh"], token);
                AssertSuccess(changed, first.RootPath, "status: synchronized");
                Assert.NotEqual(McpServerTests.Text(refresh), McpServerTests.Text(changed));
                var saved = await QueryAsync(client, first.RootPath,
                    ["symbols", "--query", "SavedReplacementMarker", "--exact"], token);
                AssertSuccess(saved, first.RootPath, "SavedReplacementMarker");
                var relative = await QueryAsync(client, first.RootPath,
                    ["document-lines", "--file", "src/Program.cs", "--start-line", "1", "--end-line", "1"], token);
                AssertSuccess(relative, first.RootPath, "SavedReplacementMarker");

                var invalid = await QueryAsync(client, first.RootPath, ["workspace", "--target", second.TargetPath], token);
                Assert.True(invalid.IsError);
                Assert.Contains("outside repository", McpServerTests.Text(invalid), StringComparison.Ordinal);
            }

            process.StandardInput.Close();
            await process.WaitForExitAsync(token).WaitAsync(TimeSpan.FromSeconds(40), token);
            Assert.Equal(0, process.ExitCode);
            var diagnostics = await errors;
            Assert.DoesNotContain("Unhandled exception", diagnostics, StringComparison.Ordinal);
            Assert.DoesNotContain("terminating its remaining process tree", diagnostics, StringComparison.Ordinal);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }
    }

    private static async Task<CallToolResult> QueryAsync(McpClient client, string root, string[] args, CancellationToken cancellationToken)
    {
        return await client.CallToolAsync("query", new Dictionary<string, object?> { ["repositoryRoot"] = root, ["args"] = args },
            cancellationToken: cancellationToken);
    }

    private static void AssertSuccess(CallToolResult result, string root, string expected)
    {
        var text = McpServerTests.Text(result);
        Assert.False(result.IsError, text);
        Assert.StartsWith($"repository: {root}\n", text, StringComparison.Ordinal);
        Assert.Contains(expected, text, StringComparison.Ordinal);
    }

    private static async Task PrepareSdkFixtureAsync(RepositoryProcessTestArea area, string sdkVersion, CancellationToken cancellationToken)
    {
        await File.WriteAllTextAsync(Path.Combine(area.RootPath, "global.json"),
            JsonSerializer.Serialize(new { sdk = new { version = sdkVersion, rollForward = "disable" } }), cancellationToken);
        await File.WriteAllTextAsync(area.TargetPath, $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <DefineConstants Condition="'$(NETCoreSdkVersion)' == '{{sdkVersion}}'">EXPECTED_SDK</DefineConstants>
              </PropertyGroup>
            </Project>
            """, cancellationToken);
        await area.WriteSourceAsync("""
            #if EXPECTED_SDK
            public sealed class ExpectedSdkMarker { }
            #else
            public sealed class WrongSdkMarker { }
            #endif
            """, cancellationToken);
    }

    private static async Task<string[]> InstalledSdkVersionsAsync(CancellationToken cancellationToken)
    {
        using var process = Process.Start(new ProcessStartInfo("dotnet", "--list-sdks")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        }) ?? throw new InvalidOperationException("Could not inspect installed SDKs.");
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errors = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        Assert.Equal(0, process.ExitCode);
        await errors;
        return (await output).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0])
            .Where(version => version.StartsWith("10.0.", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal).ToArray();
    }
}
