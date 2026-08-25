using System.Text.RegularExpressions;

namespace RoslynKit.Tests;

public sealed class DaemonEndpointNameTests
{
    [Fact]
    public void Create_ReturnsStableFixedLengthOpaqueName()
    {
        var identity = CreateIdentity();

        var endpointName = DaemonEndpointName.Create(identity);

        Assert.Equal(DaemonEndpointName.Length, endpointName.Length);
        Assert.Matches(new Regex("^roslynkit-[0-9a-f]{64}$", RegexOptions.CultureInvariant), endpointName);
        Assert.DoesNotContain(identity.Workspace.WorktreeRoot, endpointName, StringComparison.Ordinal);
        Assert.DoesNotContain(identity.Workspace.TargetPath, endpointName, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_ReturnsSameName_WhenEnvironmentInsertionOrderDiffers()
    {
        var identity = CreateIdentity();
        var reorderedEnvironment = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["ZETA"] = null,
            ["ALPHA"] = "one",
        };
        var reordered = identity with
        {
            Workspace = identity.Workspace with { BuildEnvironment = reorderedEnvironment },
        };

        Assert.Equal(DaemonEndpointName.Create(identity), DaemonEndpointName.Create(reordered));
    }

    [Fact]
    public void Create_DistinguishesEveryCompatibilityInput()
    {
        var identity = CreateIdentity();
        var variants = new DaemonIdentity[]
        {
            identity with { User = identity.User with { Kind = "other-user-kind" } },
            identity with { User = identity.User with { Value = "other-user" } },
            identity with { IpcRuntimeDirectory = "/other-runtime" },
            identity with { Workspace = identity.Workspace with { WorktreeRoot = "/other-repository" } },
            identity with { Workspace = identity.Workspace with { TargetPath = "/private/repository/Other.slnx" } },
            identity with { Workspace = identity.Workspace with { GlobalJson = null } },
            identity with { Workspace = identity.Workspace with { GlobalJson = identity.Workspace.GlobalJson! with { Path = "/other/global.json" } } },
            identity with { Workspace = identity.Workspace with { GlobalJson = identity.Workspace.GlobalJson! with { Sha256 = "other-global-json" } } },
            identity with { Workspace = identity.Workspace with { MSBuild = identity.Workspace.MSBuild with { Name = "Other MSBuild" } } },
            identity with { Workspace = identity.Workspace with { MSBuild = identity.Workspace.MSBuild with { DiscoveryType = "Other" } } },
            identity with { Workspace = identity.Workspace with { MSBuild = identity.Workspace.MSBuild with { MSBuildPath = "/other/msbuild" } } },
            identity with { Workspace = identity.Workspace with { BuildEnvironment = new Dictionary<string, string?> { ["ALPHA"] = "two", ["ZETA"] = null } } },
            identity with { Workspace = identity.Workspace with { BuildEnvironment = new Dictionary<string, string?> { ["ALPHA"] = "one", ["ZETA"] = string.Empty } } },
            identity with { Workspace = identity.Workspace with { ProcessArchitecture = "other-architecture" } },
        };
        var endpointName = DaemonEndpointName.Create(identity);

        Assert.All(variants, variant => Assert.NotEqual(endpointName, DaemonEndpointName.Create(variant)));
        Assert.Equal(variants.Length, variants.Select(DaemonEndpointName.Create).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Resolve_NormalizesRuntimeDirectory_AndUsesProvidedUser()
    {
        var runtimeDirectory = Path.Combine(Path.GetTempPath(), "roslynkit-runtime", ".");
        var resolver = new DaemonIdentityResolver(
            () => new DaemonUserIdentity(DaemonIdentityResolver.UnixEffectiveUserIdKind, "42"),
            () => runtimeDirectory);

        var identity = resolver.Resolve(CreateIdentity().Workspace);

        Assert.Equal(new DaemonUserIdentity(DaemonIdentityResolver.UnixEffectiveUserIdKind, "42"), identity.User);
        Assert.Equal(Path.TrimEndingDirectorySeparator(Path.GetFullPath(runtimeDirectory)), identity.IpcRuntimeDirectory);
    }

    [Fact]
    public void Resolve_SnapshotsBuildEnvironment()
    {
        var sourceIdentity = CreateIdentity();
        var sourceEnvironment = Assert.IsType<Dictionary<string, string?>>(sourceIdentity.Workspace.BuildEnvironment);
        var resolver = new DaemonIdentityResolver(
            () => sourceIdentity.User,
            () => sourceIdentity.IpcRuntimeDirectory);

        var identity = resolver.Resolve(sourceIdentity.Workspace);
        var endpointName = DaemonEndpointName.Create(identity);
        sourceEnvironment["ALPHA"] = "changed";

        Assert.Equal("one", identity.Workspace.BuildEnvironment["ALPHA"]);
        Assert.Equal(endpointName, DaemonEndpointName.Create(identity));
    }

    [Fact]
    public void Resolve_UsesOperatingSystemStableUserIdentifier()
    {
        var identity = new DaemonIdentityResolver().Resolve(CreateIdentity().Workspace);

        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(DaemonIdentityResolver.WindowsSidKind, identity.User.Kind);
            Assert.StartsWith("S-1-", identity.User.Value, StringComparison.Ordinal);
        }
        else
        {
            Assert.Equal(DaemonIdentityResolver.UnixEffectiveUserIdKind, identity.User.Kind);
            Assert.True(uint.TryParse(identity.User.Value, out _));
        }
    }

    private static DaemonIdentity CreateIdentity()
    {
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["ALPHA"] = "one",
            ["ZETA"] = null,
        };
        var workspace = new GitWorkspaceIdentity(
            "/private/repository",
            "/private/repository/RoslynKit.slnx",
            new GlobalJsonIdentity("/private/repository/global.json", "global-json-digest"),
            new MSBuildInstanceIdentity(
                ".NET SDK",
                "DotNetSdk",
                "/dotnet/sdk/10.0.100"),
            environment,
            "X64");
        return new DaemonIdentity(
            new DaemonUserIdentity(DaemonIdentityResolver.UnixEffectiveUserIdKind, "1000"),
            "/runtime/user/1000",
            workspace);
    }
}
