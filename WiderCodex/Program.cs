using System.Collections;
using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Windows.ApplicationModel;
using Windows.Management.Deployment;

return await Launcher.RunAsync(args);

internal static class Launcher
{
    private const string PackageName = "OpenAI.Codex";
    private const string BuildFlavor = "env";
    private const uint DetachedProcess = 0x00000008;
    private const uint CreateNewProcessGroup = 0x00000200;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint CreateNoWindow = 0x08000000;
    private const uint CreateBreakawayFromJob = 0x01000000;
    private static readonly TimeSpan InjectionTimeout = TimeSpan.FromSeconds(30);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private const string InjectionScript = """
        (() => {
          const apply = () => {
            const id = "wider-codex";
            let style = document.getElementById(id);

            if (!style) {
              style = document.createElement("style");
              style.id = id;
              document.head.appendChild(style);
            }

            style.textContent = `
              [data-codex-window-type="electron"] body {
                --thread-content-max-width: 96rem !important;
                --thread-composer-max-width: calc(var(--thread-content-max-width) + 1rem) !important;
              }
            `;
          };

          if (document.head) {
            apply();
            return;
          }

          window.addEventListener("DOMContentLoaded", apply, { once: true });
        })();
        """;

    public static async Task<int> RunAsync(string[] args)
    {
        var options = LauncherOptions.Parse(args);
        if (options.ShowHelp)
        {
            PrintUsage();
            return 0;
        }

        var installation = ResolveInstallation();
        if (installation is null)
        {
            Console.Error.WriteLine("Unable to locate the Codex installation.");
            Console.Error.WriteLine("Set CODEX_GUI_PATH or install the OpenAI.Codex AppX package.");
            return 1;
        }

        if (options.ForceRestart)
        {
            await KillMatchingProcessesAsync();
        }

        var debugPort = FindFreeTcpPort();
        using var launchCts = new CancellationTokenSource(InjectionTimeout);

        Process? codexProcess;
        try
        {
            codexProcess = StartCodex(installation, debugPort, options.ForwardedArgs);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to launch Codex: {ex.Message}");
            return 1;
        }

        if (codexProcess is null)
        {
            Console.Error.WriteLine("Process.Start returned null.");
            return 1;
        }

        using (codexProcess)
        {
            Console.WriteLine($"Started Codex with BUILD_FLAVOR={BuildFlavor}.");

            try
            {
                var target = await WaitForPageTargetAsync(debugPort, codexProcess, launchCts.Token);
                if (target is null)
                {
                    Console.Error.WriteLine("Codex started, but the DevTools page target was not reachable.");
                    Console.Error.WriteLine("If Codex was already running, close it and relaunch through this launcher.");
                    return 2;
                }

                await using var devTools = new DevToolsClient(new Uri(target.WebSocketDebuggerUrl!));
                await devTools.ConnectAsync(launchCts.Token);
                await devTools.SendCommandAsync("Page.addScriptToEvaluateOnNewDocument", new { source = InjectionScript }, launchCts.Token);
                await devTools.SendCommandAsync("Runtime.evaluate", new
                {
                    expression = InjectionScript,
                    awaitPromise = false,
                    returnByValue = false
                }, launchCts.Token);

                Console.WriteLine("Injected the wide-chat CSS override.");
                return 0;
            }
            catch (OperationCanceledException)
            {
                Console.Error.WriteLine("Timed out while waiting for Codex to expose its renderer for injection.");
                Console.Error.WriteLine("Close any existing Codex window and try again.");
                return 2;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Codex launched, but JS injection failed: {ex.Message}");
                return 2;
            }
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Wider Codex");
        Console.WriteLine("  Launches Codex with BUILD_FLAVOR=env and injects a wider chat layout.");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  WiderCodex.exe [--force-restart] [-- <additional Codex args>]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --force-restart   Kill running Codex processes before launching.");
        Console.WriteLine("  --help            Show this help.");
        Console.WriteLine();
        Console.WriteLine("Environment overrides:");
        Console.WriteLine("  CODEX_GUI_PATH    Override the detected path to Codex.exe.");
        Console.WriteLine("  CODEX_HELPER_PATH Override the detected path to resources\\codex.exe.");
    }

    private static Process StartCodex(CodexInstallation installation, int debugPort, IReadOnlyList<string> forwardedArgs)
    {
        var codexArgs = new List<string>(forwardedArgs.Count + 1)
        {
            $"--remote-debugging-port={debugPort}"
        };
        codexArgs.AddRange(forwardedArgs);

        return StartDetachedProcess(installation.GuiPath, Path.GetDirectoryName(installation.GuiPath)!, codexArgs);
    }

    private static Process StartDetachedProcess(string executablePath, string workingDirectory, IReadOnlyList<string> arguments)
    {
        var startupInfo = new STARTUPINFO();
        startupInfo.cb = Marshal.SizeOf<STARTUPINFO>();

        var processFlags = DetachedProcess | CreateNewProcessGroup | CreateUnicodeEnvironment | CreateNoWindow;
        var commandLine = new StringBuilder(BuildCommandLine(executablePath, arguments));
        var environmentBlock = BuildEnvironmentBlock();
        var environmentPtr = Marshal.StringToHGlobalUni(environmentBlock);

        try
        {
            if (!TryCreateProcess(processFlags | CreateBreakawayFromJob, executablePath, commandLine, workingDirectory, environmentPtr, startupInfo, out var processInfo))
            {
                commandLine = new StringBuilder(BuildCommandLine(executablePath, arguments));
                if (!TryCreateProcess(processFlags, executablePath, commandLine, workingDirectory, environmentPtr, startupInfo, out processInfo))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateProcessW failed to start Codex.");
                }

                Console.WriteLine("Started Codex without CREATE_BREAKAWAY_FROM_JOB.");
            }

            try
            {
                return Process.GetProcessById((int)processInfo.dwProcessId);
            }
            finally
            {
                NativeMethods.CloseHandle(processInfo.hThread);
                NativeMethods.CloseHandle(processInfo.hProcess);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(environmentPtr);
        }
    }

    private static bool TryCreateProcess(
        uint creationFlags,
        string executablePath,
        StringBuilder commandLine,
        string workingDirectory,
        IntPtr environmentPtr,
        STARTUPINFO startupInfo,
        out PROCESS_INFORMATION processInformation)
    {
        return NativeMethods.CreateProcessW(
            executablePath,
            commandLine,
            IntPtr.Zero,
            IntPtr.Zero,
            false,
            creationFlags,
            environmentPtr,
            workingDirectory,
            ref startupInfo,
            out processInformation);
    }

    private static string BuildEnvironmentBlock()
    {
        var variables = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            var key = entry.Key?.ToString();
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            variables[key] = entry.Value?.ToString() ?? string.Empty;
        }

        variables["BUILD_FLAVOR"] = BuildFlavor;

        var builder = new StringBuilder();
        foreach (var (key, value) in variables)
        {
            builder.Append(key).Append('=').Append(value).Append('\0');
        }

        builder.Append('\0');
        return builder.ToString();
    }

    private static string BuildCommandLine(string executablePath, IReadOnlyList<string> arguments)
    {
        var builder = new StringBuilder();
        builder.Append(QuoteWindowsArgument(executablePath));

        foreach (var argument in arguments)
        {
            builder.Append(' ').Append(QuoteWindowsArgument(argument));
        }

        return builder.ToString();
    }

    private static string QuoteWindowsArgument(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "\"\"";
        }

        if (!value.Any(character => char.IsWhiteSpace(character) || character is '"' or '\\'))
        {
            return value;
        }

        var builder = new StringBuilder();
        builder.Append('"');
        var backslashCount = 0;

        foreach (var character in value)
        {
            if (character == '\\')
            {
                backslashCount++;
                continue;
            }

            if (character == '"')
            {
                builder.Append('\\', backslashCount * 2 + 1);
                builder.Append('"');
                backslashCount = 0;
                continue;
            }

            if (backslashCount > 0)
            {
                builder.Append('\\', backslashCount);
                backslashCount = 0;
            }

            builder.Append(character);
        }

        if (backslashCount > 0)
        {
            builder.Append('\\', backslashCount * 2);
        }

        builder.Append('"');
        return builder.ToString();
    }

    private static async Task<CdpTarget?> WaitForPageTargetAsync(int debugPort, Process process, CancellationToken cancellationToken)
    {
        using var httpClient = new HttpClient
        {
            BaseAddress = new Uri($"http://127.0.0.1:{debugPort}/"),
            Timeout = TimeSpan.FromSeconds(2)
        };

        while (!cancellationToken.IsCancellationRequested)
        {
            if (process.HasExited)
            {
                return null;
            }

            try
            {
                var targets = await httpClient.GetFromJsonAsync<List<CdpTarget>>("json/list", JsonOptions, cancellationToken);
                var target = targets?
                    .FirstOrDefault(item => string.Equals(item.Type, "page", StringComparison.OrdinalIgnoreCase) &&
                                            !string.IsNullOrWhiteSpace(item.WebSocketDebuggerUrl));

                if (target is not null)
                {
                    return target;
                }
            }
            catch (HttpRequestException)
            {
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
            }
            catch (JsonException)
            {
            }

            await Task.Delay(500, cancellationToken);
        }

        return null;
    }

    private static async Task KillMatchingProcessesAsync()
    {
        var launcherProcessId = Environment.ProcessId;

        foreach (var process in Process.GetProcesses())
        {
            if (process.Id == launcherProcessId)
            {
                process.Dispose();
                continue;
            }

            try
            {
                var path = process.MainModule?.FileName;
                if (TryResolveFromInstalledLayout(path) is null)
                {
                    process.Dispose();
                    continue;
                }

                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
            catch
            {
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    private static int FindFreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static CodexInstallation? ResolveInstallation()
    {
        var overriddenGuiPath = Environment.GetEnvironmentVariable("CODEX_GUI_PATH");
        var overriddenHelperPath = Environment.GetEnvironmentVariable("CODEX_HELPER_PATH");
        if (!string.IsNullOrWhiteSpace(overriddenGuiPath))
        {
            var guiPath = Path.GetFullPath(overriddenGuiPath);
            var helperPath = !string.IsNullOrWhiteSpace(overriddenHelperPath)
                ? Path.GetFullPath(overriddenHelperPath)
                : TryResolveCompanionHelperPath(guiPath);

            if (IsValidInstallation(guiPath, helperPath))
            {
                return new CodexInstallation(guiPath, helperPath);
            }
        }

        var packageRoot = TryResolveInstallRootFromPackageManager();
        if (!string.IsNullOrWhiteSpace(packageRoot))
        {
            var installation = TryResolveFromInstalledLayout(Path.Combine(packageRoot, "app", "Codex.exe"));
            if (installation is not null)
            {
                return installation;
            }
        }

        foreach (var process in Process.GetProcesses())
        {
            try
            {
                var processPath = process.MainModule?.FileName;
                var installation = TryResolveFromInstalledLayout(processPath);
                if (installation is not null)
                {
                    return installation;
                }
            }
            catch
            {
            }
            finally
            {
                process.Dispose();
            }
        }

        return null;
    }

    private static CodexInstallation? TryResolveFromInstalledLayout(string? candidatePath)
    {
        if (string.IsNullOrWhiteSpace(candidatePath))
        {
            return null;
        }

        var fileName = Path.GetFileName(candidatePath);
        if (!fileName.Equals("Codex.exe", StringComparison.OrdinalIgnoreCase) &&
            !fileName.Equals("codex.exe", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string? appDirectory;
        if (fileName.Equals("Codex.exe", StringComparison.OrdinalIgnoreCase))
        {
            appDirectory = Path.GetDirectoryName(candidatePath);
        }
        else
        {
            var resourcesDirectory = Path.GetDirectoryName(candidatePath);
            appDirectory = resourcesDirectory is null ? null : Path.GetDirectoryName(resourcesDirectory);
        }

        if (string.IsNullOrWhiteSpace(appDirectory))
        {
            return null;
        }

        var guiPath = Path.Combine(appDirectory, "Codex.exe");
        var helperPath = TryResolveCompanionHelperPath(guiPath);
        return IsValidInstallation(guiPath, helperPath)
            ? new CodexInstallation(guiPath, helperPath)
            : null;
    }

    private static string? TryResolveCompanionHelperPath(string guiPath)
    {
        var appDirectory = Path.GetDirectoryName(guiPath);
        if (string.IsNullOrWhiteSpace(appDirectory))
        {
            return null;
        }

        var candidatePath = Path.Combine(appDirectory, "resources", "codex.exe");
        return File.Exists(candidatePath) ? candidatePath : null;
    }

    private static bool IsValidInstallation(string guiPath, string? helperPath)
    {
        return File.Exists(guiPath) && (helperPath is null || File.Exists(helperPath));
    }

    private static string? TryResolveInstallRootFromPackageManager()
    {
        try
        {
            var packageManager = new PackageManager();
            var package = packageManager
                .FindPackagesForUser(string.Empty)
                .Where(static package => string.Equals(package.Id.Name, PackageName, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(static package => package.Id.Version, PackageVersionComparer.Instance)
                .FirstOrDefault();

            return package?.InstalledLocation?.Path;
        }
        catch
        {
            return null;
        }
    }
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct STARTUPINFO
{
    public int cb;
    public string? lpReserved;
    public string? lpDesktop;
    public string? lpTitle;
    public int dwX;
    public int dwY;
    public int dwXSize;
    public int dwYSize;
    public int dwXCountChars;
    public int dwYCountChars;
    public int dwFillAttribute;
    public int dwFlags;
    public short wShowWindow;
    public short cbReserved2;
    public IntPtr lpReserved2;
    public IntPtr hStdInput;
    public IntPtr hStdOutput;
    public IntPtr hStdError;
}

[StructLayout(LayoutKind.Sequential)]
internal struct PROCESS_INFORMATION
{
    public IntPtr hProcess;
    public IntPtr hThread;
    public uint dwProcessId;
    public uint dwThreadId;
}

internal sealed class DevToolsClient : IAsyncDisposable
{
    private readonly Uri _endpoint;
    private readonly ClientWebSocket _socket = new();
    private int _nextId = 1;

    public DevToolsClient(Uri endpoint)
    {
        _endpoint = endpoint;
    }

    public Task ConnectAsync(CancellationToken cancellationToken)
    {
        return _socket.ConnectAsync(_endpoint, cancellationToken);
    }

    public async Task SendCommandAsync(string method, object? parameters, CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref _nextId);
        var payload = JsonSerializer.Serialize(new
        {
            id,
            method,
            @params = parameters
        });

        var bytes = Encoding.UTF8.GetBytes(payload);
        await _socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);

        while (true)
        {
            using var message = await ReceiveMessageAsync(cancellationToken);
            using var document = JsonDocument.Parse(message);
            var root = document.RootElement;

            if (!root.TryGetProperty("id", out var responseId) || responseId.GetInt32() != id)
            {
                continue;
            }

            if (root.TryGetProperty("error", out var error))
            {
                throw new InvalidOperationException(error.ToString());
            }

            return;
        }
    }

    private async Task<MemoryStream> ReceiveMessageAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        var message = new MemoryStream();

        while (true)
        {
            var result = await _socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new WebSocketException("DevTools socket closed before responding.");
            }

            message.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                message.Position = 0;
                return message;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_socket.State == WebSocketState.Open)
        {
            await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
        }

        _socket.Dispose();
    }
}

internal sealed record LauncherOptions(bool ForceRestart, bool ShowHelp, IReadOnlyList<string> ForwardedArgs)
{
    public static LauncherOptions Parse(string[] args)
    {
        var forceRestart = false;
        var showHelp = false;
        var forwardedArgs = new List<string>();
        var forwardEverything = false;

        foreach (var arg in args)
        {
            if (forwardEverything)
            {
                forwardedArgs.Add(arg);
                continue;
            }

            switch (arg)
            {
                case "--force-restart":
                    forceRestart = true;
                    break;
                case "--":
                    forwardEverything = true;
                    break;
                case "--help":
                case "-h":
                case "/?":
                    showHelp = true;
                    break;
                default:
                    forwardedArgs.Add(arg);
                    break;
            }
        }

        return new LauncherOptions(forceRestart, showHelp, forwardedArgs);
    }
}

internal sealed record CdpTarget(string? Id, string? Type, string? Url, string? WebSocketDebuggerUrl);

internal sealed record CodexInstallation(string GuiPath, string? HelperPath);

internal sealed class PackageVersionComparer : IComparer<PackageVersion>
{
    public static PackageVersionComparer Instance { get; } = new();

    public int Compare(PackageVersion x, PackageVersion y)
    {
        var result = x.Major.CompareTo(y.Major);
        if (result != 0)
        {
            return result;
        }

        result = x.Minor.CompareTo(y.Minor);
        if (result != 0)
        {
            return result;
        }

        result = x.Build.CompareTo(y.Build);
        if (result != 0)
        {
            return result;
        }

        return x.Revision.CompareTo(y.Revision);
    }
}

internal static class NativeMethods
{
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern bool CreateProcessW(
        string? lpApplicationName,
        StringBuilder lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref STARTUPINFO lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(IntPtr hObject);
}
