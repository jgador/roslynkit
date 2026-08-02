using System.Security.Cryptography;
using System.Text.Json;

namespace RoslynKit;

/// <summary>
/// Resolves Node.js, the packaged bridge script, and a native-preview installation without relying on the source checkout.
/// </summary>
internal static class TypeScriptRuntimeResolver
{
    private const string PackageName = "@typescript/native-preview";

    public static async Task<TypeScriptRuntime> ResolveAsync(
        string configPath,
        CancellationToken cancellationToken)
    {
        var canonicalConfig = PathCanonicalizer.ResolveExistingPath(configPath);
        if (!File.Exists(canonicalConfig))
        {
            throw new CliUsageException("workspace", $"The TypeScript target '{configPath}' does not exist.");
        }

        var nodePath = ResolveExecutable("ROSLYNKIT_NODE_PATH", OperatingSystem.IsWindows() ? "node.exe" : "node")
            ?? throw new InvalidOperationException(
                "Node.js 16.20 or later is required for TypeScript targets. Install Node.js and ensure 'node' is on PATH, or set ROSLYNKIT_NODE_PATH to the Node executable.");
        var nodeVersion = await ValidateNodeVersionAsync(nodePath, canonicalConfig, cancellationToken).ConfigureAwait(false);
        var bridgePath = ResolveBridgePath();
        var package = await ResolvePackageAsync(
            canonicalConfig,
            bridgePath,
            cancellationToken).ConfigureAwait(false);
        await using var bridgeStream = File.OpenRead(bridgePath);
        var bridgeDigest = await SHA256.HashDataAsync(bridgeStream, cancellationToken).ConfigureAwait(false);
        return new TypeScriptRuntime(
            nodePath,
            nodeVersion,
            bridgePath,
            Convert.ToHexStringLower(bridgeDigest),
            package.RootPath,
            package.Version);
    }

    private static string ResolveBridgePath()
    {
        var overridePath = Environment.GetEnvironmentVariable("ROSLYNKIT_TYPESCRIPT_BRIDGE_PATH");
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            if (!File.Exists(overridePath))
            {
                throw new InvalidOperationException(
                    $"ROSLYNKIT_TYPESCRIPT_BRIDGE_PATH points to missing bridge script '{Path.GetFullPath(overridePath)}'. Remove the override or point it at bridge.mjs.");
            }

            return Path.GetFullPath(overridePath);
        }

        var packagedPath = Path.Combine(AppContext.BaseDirectory, "TypeScriptBridge", "bridge.mjs");
        if (File.Exists(packagedPath))
        {
            return packagedPath;
        }

        throw new InvalidOperationException(
            $"The packaged TypeScript bridge was not found at '{packagedPath}'. Reinstall RoslynKit, or set ROSLYNKIT_TYPESCRIPT_BRIDGE_PATH to bridge.mjs.");
    }

    private static async Task<NativePreviewPackage> ResolvePackageAsync(
        string configPath,
        string bridgePath,
        CancellationToken cancellationToken)
    {
        var overrideRoot = Environment.GetEnvironmentVariable("ROSLYNKIT_TYPESCRIPT_NATIVE_PREVIEW_ROOT");
        if (!string.IsNullOrWhiteSpace(overrideRoot))
        {
            return ValidatePackageRoot(overrideRoot);
        }

        var candidates = new List<string>();
        AddAncestorPackageCandidates(candidates, Path.GetDirectoryName(configPath)!);
        AddAncestorPackageCandidates(candidates, Path.GetDirectoryName(bridgePath)!);
        AddAncestorPackageCandidates(candidates, Environment.CurrentDirectory);
        foreach (var candidate in candidates.Distinct(PathComparer))
        {
            if (IsPackageRoot(candidate))
            {
                return ValidatePackageRoot(candidate);
            }
        }

        var npmPath = ResolveExecutable("ROSLYNKIT_NPM_PATH", OperatingSystem.IsWindows() ? "npm.cmd" : "npm");
        if (npmPath is not null)
        {
            var result = await ProcessCommandRunner.RunAsync(
                npmPath,
                Path.GetDirectoryName(configPath)!,
                ["root", "--global"],
                cancellationToken).ConfigureAwait(false);
            if (result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.StandardOutput))
            {
                var globalCandidate = Path.Combine(
                    result.StandardOutput.Trim(),
                    "@typescript",
                    "native-preview");
                if (IsPackageRoot(globalCandidate))
                {
                    return ValidatePackageRoot(globalCandidate);
                }
            }
        }

        throw new InvalidOperationException(
            "@typescript/native-preview is required for TypeScript targets. Run 'npm install --global @typescript/native-preview@latest', install it in the target repository, or set ROSLYNKIT_TYPESCRIPT_NATIVE_PREVIEW_ROOT to the package directory.");
    }

    private static void AddAncestorPackageCandidates(ICollection<string> candidates, string startDirectory)
    {
        var directory = new DirectoryInfo(Path.GetFullPath(startDirectory));
        while (directory is not null)
        {
            candidates.Add(Path.Combine(directory.FullName, "node_modules", "@typescript", "native-preview"));
            candidates.Add(Path.Combine(
                directory.FullName,
                "src",
                "RoslynKit",
                "TypeScriptBridge",
                "node_modules",
                "@typescript",
                "native-preview"));
            directory = directory.Parent;
        }
    }

    private static NativePreviewPackage ValidatePackageRoot(string packageRoot)
    {
        var fullRoot = Path.GetFullPath(packageRoot);
        var packageJsonPath = Path.Combine(fullRoot, "package.json");
        if (!File.Exists(packageJsonPath))
        {
            throw new InvalidOperationException(
                $"The native-preview package root '{fullRoot}' does not contain package.json. Point ROSLYNKIT_TYPESCRIPT_NATIVE_PREVIEW_ROOT at node_modules/@typescript/native-preview.");
        }

        using var packageJson = JsonDocument.Parse(File.ReadAllText(packageJsonPath));
        if (!packageJson.RootElement.TryGetProperty("name", out var name)
            || !string.Equals(name.GetString(), PackageName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The package at '{fullRoot}' is not {PackageName}.");
        }

        if (!packageJson.RootElement.TryGetProperty("version", out var version)
            || string.IsNullOrWhiteSpace(version.GetString()))
        {
            throw new InvalidOperationException(
                $"The {PackageName} package at '{fullRoot}' does not declare a version in package.json. Reinstall it with npm.");
        }

        return new NativePreviewPackage(fullRoot, version.GetString()!);
    }

    private static async Task<string> ValidateNodeVersionAsync(
        string nodePath,
        string configPath,
        CancellationToken cancellationToken)
    {
        ProcessCommandResult result;
        try
        {
            result = await ProcessCommandRunner.RunAsync(
                nodePath,
                Path.GetDirectoryName(configPath)!,
                ["--version"],
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                $"Node.js could not be started from '{nodePath}'. Install Node.js 16.20 or later, ensure 'node' is on PATH, or set ROSLYNKIT_NODE_PATH.",
                exception);
        }

        var versionText = result.StandardOutput.Trim().TrimStart('v');
        if (result.ExitCode != 0
            || !Version.TryParse(versionText, out var version)
            || version < new Version(16, 20))
        {
            throw new InvalidOperationException(
                $"Node.js 16.20 or later is required for TypeScript targets, but '{nodePath} --version' returned '{result.StandardOutput.Trim()}'. Install a compatible Node.js runtime or set ROSLYNKIT_NODE_PATH.");
        }

        return versionText;
    }

    private static bool IsPackageRoot(string path)
    {
        return File.Exists(Path.Combine(path, "package.json"));
    }

    private static string? ResolveExecutable(string overrideVariable, string executableName)
    {
        var overridePath = Environment.GetEnvironmentVariable(overrideVariable);
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            return File.Exists(overridePath) ? Path.GetFullPath(overridePath) : null;
        }

        if (Path.IsPathRooted(executableName) && File.Exists(executableName))
        {
            return Path.GetFullPath(executableName);
        }

        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory, executableName);
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        return null;
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private sealed record NativePreviewPackage(string RootPath, string Version);
}

/// <summary>
/// Captures the resolved runtime inputs used to launch one maintained TypeScript bridge.
/// </summary>
internal sealed record TypeScriptRuntime(
    string NodePath,
    string NodeVersion,
    string BridgePath,
    string BridgeSha256,
    string NativePreviewRoot,
    string NativePreviewVersion);
