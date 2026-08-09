using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace RoslynKit;

/// <summary>
/// Starts the current RoslynKit build in hidden daemon mode without waiting for the long-lived child.
/// </summary>
internal static class DaemonProcessStarter
{
    /// <summary>
    /// Starts hidden mode from the exact current apphost or dotnet entry assembly and returns immediately.
    /// </summary>
    public static void Start(string targetPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        var processPath = Environment.ProcessPath
            ?? throw new InvalidOperationException("The current RoslynKit process path is unavailable.");
        var entryAssemblyPath = Assembly.GetEntryAssembly()?.Location;
        if (string.IsNullOrWhiteSpace(entryAssemblyPath))
        {
            throw new InvalidOperationException("The current RoslynKit entry assembly path is unavailable.");
        }

        var startInfo = CreateStartInfo(processPath, entryAssemblyPath, targetPath);
        if (OperatingSystem.IsWindows())
        {
            StartWindows(startInfo);
            return;
        }

        _ = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The RoslynKit daemon process could not be started.");
    }

    internal static ProcessStartInfo CreateStartInfo(
        string processPath,
        string entryAssemblyPath,
        string targetPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(entryAssemblyPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);

        var startInfo = new ProcessStartInfo
        {
            FileName = processPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        if (IsDotNetHost(processPath))
        {
            startInfo.ArgumentList.Add(entryAssemblyPath);
        }

        startInfo.ArgumentList.Add(DaemonServerRunner.InternalModeToken);
        startInfo.ArgumentList.Add("--target");
        startInfo.ArgumentList.Add(targetPath);
        return startInfo;
    }

    internal static string CreateWindowsCommandLine(ProcessStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        var commandLine = new StringBuilder(QuoteWindowsArgument(startInfo.FileName));
        foreach (var argument in startInfo.ArgumentList)
        {
            commandLine.Append(' ').Append(QuoteWindowsArgument(argument));
        }

        return commandLine.ToString();
    }

    private static bool IsDotNetHost(string processPath)
    {
        var separatorIndex = Math.Max(
            processPath.LastIndexOf('/'),
            processPath.LastIndexOf('\\'));
        var fileName = separatorIndex >= 0
            ? processPath[(separatorIndex + 1)..]
            : processPath;
        return string.Equals(
            Path.GetFileNameWithoutExtension(fileName),
            "dotnet",
            StringComparison.OrdinalIgnoreCase);
    }

    [SupportedOSPlatform("windows")]
    private static void StartWindows(ProcessStartInfo startInfo)
    {
        var startupInfo = new StartupInfo
        {
            Size = Marshal.SizeOf<StartupInfo>(),
            Flags = StartfUseStdHandles,
            StandardInput = new IntPtr(-1),
            StandardOutput = new IntPtr(-1),
            StandardError = new IntPtr(-1),
        };
        var commandLine = new StringBuilder(CreateWindowsCommandLine(startInfo));
        if (!CreateProcess(
            applicationName: null,
            commandLine,
            processAttributes: IntPtr.Zero,
            threadAttributes: IntPtr.Zero,
            inheritHandles: false,
            creationFlags: NormalPriorityClass | CreateNoWindow,
            environment: IntPtr.Zero,
            currentDirectory: Directory.GetCurrentDirectory(),
            ref startupInfo,
            out var processInformation))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "The RoslynKit daemon process could not be started.");
        }

        CloseHandle(processInformation.ProcessHandle);
        CloseHandle(processInformation.ThreadHandle);
    }

    private static string QuoteWindowsArgument(string argument)
    {
        if (argument.Length > 0
            && argument.IndexOfAny([' ', '\t', '\n', '\v', '"']) < 0)
        {
            return argument;
        }

        var quoted = new StringBuilder(argument.Length + 2).Append('"');
        var backslashes = 0;
        foreach (var character in argument)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }

            if (character == '"')
            {
                quoted.Append('\\', (backslashes * 2) + 1).Append('"');
                backslashes = 0;
                continue;
            }

            quoted.Append('\\', backslashes).Append(character);
            backslashes = 0;
        }

        return quoted.Append('\\', backslashes * 2).Append('"').ToString();
    }

    private const int StartfUseStdHandles = 0x00000100;
    private const uint NormalPriorityClass = 0x00000020;
    private const uint CreateNoWindow = 0x08000000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public string? Reserved;
        public string? Desktop;
        public string? Title;
        public int X;
        public int Y;
        public int XSize;
        public int YSize;
        public int XCountChars;
        public int YCountChars;
        public int FillAttribute;
        public int Flags;
        public short ShowWindow;
        public short ReservedSize;
        public IntPtr ReservedPointer;
        public IntPtr StandardInput;
        public IntPtr StandardOutput;
        public IntPtr StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr ProcessHandle;
        public IntPtr ThreadHandle;
        public int ProcessId;
        public int ThreadId;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcess(
        string? applicationName,
        [In, Out] StringBuilder commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        string currentDirectory,
        ref StartupInfo startupInfo,
        out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
