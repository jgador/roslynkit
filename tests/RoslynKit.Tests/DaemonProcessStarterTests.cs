namespace RoslynKit.Tests;

public sealed class DaemonProcessStarterTests
{
    [Fact]
    public void CreateStartInfo_AppHostInvokesSameExecutableDirectly()
    {
        var executable = Path.Combine("tools", OperatingSystem.IsWindows() ? "roslynkit.exe" : "roslynkit");
        var assembly = Path.Combine("tools", "RoslynKit.dll");

        var startInfo = DaemonProcessStarter.CreateStartInfo(executable, assembly, "target.slnx");

        Assert.Equal(executable, startInfo.FileName);
        Assert.Equal(
            [DaemonServerRunner.InternalModeToken, "--target", "target.slnx"],
            startInfo.ArgumentList);
        Assert.False(startInfo.UseShellExecute);
        Assert.True(startInfo.CreateNoWindow);
    }

    [Fact]
    public void CreateStartInfo_DotNetHostPrependsEntryAssembly()
    {
        var executable = Path.Combine("dotnet", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        var assembly = Path.Combine("tools", "RoslynKit.dll");

        var startInfo = DaemonProcessStarter.CreateStartInfo(executable, assembly, "target.slnx");

        Assert.Equal(executable, startInfo.FileName);
        Assert.Equal(
            [assembly, DaemonServerRunner.InternalModeToken, "--target", "target.slnx"],
            startInfo.ArgumentList);
    }

    [Fact]
    public void CreateWindowsCommandLine_RecognizesWindowsDotNetHostPathsAcrossHostPlatforms()
    {
        var startInfo = DaemonProcessStarter.CreateStartInfo(
            @"C:\Program Files\dotnet\dotnet.exe",
            @"C:\repo with spaces\RoslynKit.dll",
            @"C:\repo with spaces\RoslynKit.slnx");

        var commandLine = DaemonProcessStarter.CreateWindowsCommandLine(startInfo);

        Assert.Equal(
            "\"C:\\Program Files\\dotnet\\dotnet.exe\" "
            + "\"C:\\repo with spaces\\RoslynKit.dll\" "
            + "__roslynkit-daemon-v1 --target "
            + "\"C:\\repo with spaces\\RoslynKit.slnx\"",
            commandLine);
    }
}
