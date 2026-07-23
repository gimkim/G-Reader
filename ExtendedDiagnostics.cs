using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Channels;

namespace CDisplayEx.CSharp;

internal static class ExtendedDiagnostics
{
    [Flags]
    private enum MiniDumpType : uint
    {
        Normal = 0,
        WithHandleData = 0x00000004,
        WithUnloadedModules = 0x00000020,
        WithProcessThreadData = 0x00000100,
        WithFullMemoryInfo = 0x00000800,
        WithThreadInfo = 0x00001000
    }

    [DllImport("Dbghelp.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MiniDumpWriteDump(IntPtr process, uint processId,
        Microsoft.Win32.SafeHandles.SafeFileHandle file, MiniDumpType dumpType,
        IntPtr exceptionParam, IntPtr userStreamParam, IntPtr callbackParam);

    private static readonly object FileGate = new();
    private static readonly ConcurrentDictionary<string, long> RecentErrors = new();
    private static readonly Channel<string> LogLines = Channel.CreateBounded<string>(
        new BoundedChannelOptions(4096)
        {
            // Fatal/hang paths drain pending lines synchronously before writing
            // their terminal record, so the channel can temporarily have a
            // second reader besides the normal background writer.
            SingleReader = false,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest
        });
    private static readonly string DiagnosticsFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Fast Reader Viewer", "Diagnostics");
    private static volatile bool _enabled;
    private static int _initialized;
    private static int _dumpInProgress;
    private static long _lastUiHeartbeat = Environment.TickCount64;
    private static long _lastHealthLog;
    private static long _hangStarted;
    private static string _uiContext = "UI not attached";
    private static string? _sessionLogPath;
    private static System.Windows.Forms.Timer? _heartbeatTimer;
    private static int _logWriterStarted;

    public static string FolderPath => DiagnosticsFolder;

    public static void Initialize(bool enabled, string[] args)
    {
        if (Interlocked.Exchange(ref _initialized, 1) == 0)
        {
            Application.ThreadException += (_, eventArgs) =>
                RecordFatal("WinForms UI exception", eventArgs.Exception);
            AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
                RecordFatal("Unhandled AppDomain exception",
                    eventArgs.ExceptionObject as Exception,
                    $"terminating={eventArgs.IsTerminating}; object={eventArgs.ExceptionObject}");
            TaskScheduler.UnobservedTaskException += (_, eventArgs) =>
                LogException("Unobserved task exception", eventArgs.Exception);
            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            {
                Write("SESSION", "Process exit");
                FlushPendingLogLines();
            };
            var watchdog = new Thread(WatchdogLoop)
            {
                IsBackground = true,
                Name = "Fast Reader/Viewer diagnostic watchdog",
                Priority = ThreadPriority.BelowNormal
            };
            watchdog.Start();
        }
        Configure(enabled);
        if (enabled)
        {
            Write("SESSION", $"Started; version={Assembly.GetExecutingAssembly().GetName().Version}; " +
                $"os={Environment.OSVersion}; framework={RuntimeInformation.FrameworkDescription}; " +
                $"cpu={Environment.ProcessorCount}; pid={Environment.ProcessId}; " +
                $"args={string.Join(" | ", args.Select(SanitizeForLog))}");
            _ = Task.Run(CleanupOldFiles);
        }
    }

    public static void Configure(bool enabled)
    {
        var changed = _enabled != enabled;
        _enabled = enabled;
        Volatile.Write(ref _lastUiHeartbeat, Environment.TickCount64);
        Volatile.Write(ref _hangStarted, 0);
        ConfigureWindowsErrorReporting(enabled);
        if (!enabled) return;
        EnsureSessionLog();
        if (changed) Write("CONFIG", "Extended logging enabled");
    }

    private static void ConfigureWindowsErrorReporting(bool enabled)
    {
        var executableName = Path.GetFileName(Application.ExecutablePath);
        var relativePath =
            $@"Software\Microsoft\Windows\Windows Error Reporting\LocalDumps\{executableName}";
        try
        {
            if (enabled)
            {
                Directory.CreateDirectory(DiagnosticsFolder);
                using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(relativePath);
                key?.SetValue("DumpFolder", DiagnosticsFolder,
                    Microsoft.Win32.RegistryValueKind.ExpandString);
                key?.SetValue("DumpType", 2, Microsoft.Win32.RegistryValueKind.DWord);
                key?.SetValue("DumpCount", 10, Microsoft.Win32.RegistryValueKind.DWord);
                key?.SetValue("GReaderManaged", 1, Microsoft.Win32.RegistryValueKind.DWord);
            }
            else
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    relativePath, writable: true);
                if (Convert.ToInt32(key?.GetValue("GReaderManaged", 0)) != 1) return;
                foreach (var name in new[] { "DumpFolder", "DumpType", "DumpCount", "GReaderManaged" })
                    key?.DeleteValue(name, throwOnMissingValue: false);
            }
        }
        catch { }
    }

    public static void AttachUi(Control owner, Func<string> contextProvider)
    {
        _heartbeatTimer?.Dispose();
        var timer = new System.Windows.Forms.Timer { Interval = 500 };
        timer.Tick += (_, _) =>
        {
            Volatile.Write(ref _lastUiHeartbeat, Environment.TickCount64);
            try { Volatile.Write(ref _uiContext, contextProvider()); }
            catch { }
        };
        owner.HandleCreated += (_, _) => timer.Start();
        owner.HandleDestroyed += (_, _) => timer.Stop();
        owner.Disposed += (_, _) => timer.Dispose();
        _heartbeatTimer = timer;
        if (owner.IsHandleCreated) timer.Start();
    }

    public static void Breadcrumb(string message)
    {
        if (_enabled) Write("TRACE", message);
    }

    public static void LogException(string category, Exception exception,
        string? detail = null)
    {
        if (!_enabled) return;
        var key = $"{category}|{exception.GetType().FullName}|{exception.Message}";
        var now = Environment.TickCount64;
        if (RecentErrors.TryGetValue(key, out var previous) && now - previous < 5000) return;
        RecentErrors[key] = now;
        if (RecentErrors.Count > 2048)
            foreach (var stale in RecentErrors.Where(pair => now - pair.Value > 60_000).Take(512))
                RecentErrors.TryRemove(stale.Key, out _);
        Write("ERROR", $"{category}\n{detail}\n{exception}");
    }

    public static void RecordFatal(string category, Exception? exception,
        string? detail = null)
    {
        if (!_enabled) return;
        WriteCritical("CRASH", $"{category}\n{detail}\n{exception}\n{CreateProcessSnapshot()}");
        WriteDump("crash");
    }

    private static void WatchdogLoop()
    {
        while (true)
        {
            Thread.Sleep(1000);
            if (!_enabled) continue;
            var now = Environment.TickCount64;
            var heartbeatAge = Math.Max(0, now - Volatile.Read(ref _lastUiHeartbeat));
            if (now - Volatile.Read(ref _lastHealthLog) >= 30000)
            {
                Volatile.Write(ref _lastHealthLog, now);
                Write("HEALTH", $"uiHeartbeatAgeMs={heartbeatAge}; context={Volatile.Read(ref _uiContext)}; " +
                    CreateMemorySnapshot());
            }
            if (heartbeatAge >= 8000)
            {
                if (Interlocked.CompareExchange(ref _hangStarted, now, 0) == 0)
                {
                    WriteCritical("HANG", $"UI heartbeat stalled for {heartbeatAge} ms\n" +
                        CreateProcessSnapshot());
                    WriteDump("hang");
                }
                else if (now - Volatile.Read(ref _hangStarted) >= 15000)
                {
                    Volatile.Write(ref _hangStarted, now);
                    Write("HANG", $"UI remains stalled; heartbeatAgeMs={heartbeatAge}; " +
                        CreateMemorySnapshot());
                }
            }
            else if (Interlocked.Exchange(ref _hangStarted, 0) != 0)
            {
                Write("RECOVERY", $"UI heartbeat resumed; context={Volatile.Read(ref _uiContext)}");
            }
        }
    }

    private static string CreateMemorySnapshot()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            var gc = GC.GetGCMemoryInfo();
            return $"workingSet={process.WorkingSet64}; privateBytes={process.PrivateMemorySize64}; " +
                $"managed={GC.GetTotalMemory(false)}; gcHeap={gc.HeapSizeBytes}; " +
                $"handles={process.HandleCount}; threads={process.Threads.Count}";
        }
        catch (Exception exception) { return $"snapshot unavailable: {exception.Message}"; }
    }

    private static string CreateProcessSnapshot()
    {
        var lines = new List<string>
        {
            $"context={Volatile.Read(ref _uiContext)}",
            CreateMemorySnapshot()
        };
        try
        {
            using var process = Process.GetCurrentProcess();
            lines.Add($"totalCpu={process.TotalProcessorTime}; started={process.StartTime:O}");
            foreach (ProcessThread thread in process.Threads)
            {
                try
                {
                    lines.Add($"thread id={thread.Id}; state={thread.ThreadState}; " +
                        $"wait={(thread.ThreadState == System.Diagnostics.ThreadState.Wait ? thread.WaitReason : "-")}; " +
                        $"cpu={thread.TotalProcessorTime}; priority={thread.CurrentPriority}");
                }
                catch { lines.Add($"thread id={thread.Id}; details unavailable"); }
            }
        }
        catch (Exception exception) { lines.Add($"thread snapshot failed: {exception}"); }
        return string.Join(Environment.NewLine, lines);
    }

    private static void WriteDump(string kind)
    {
        if (!_enabled || Interlocked.Exchange(ref _dumpInProgress, 1) != 0) return;
        try
        {
            Directory.CreateDirectory(DiagnosticsFolder);
            var path = Path.Combine(DiagnosticsFolder,
                $"{kind}-{DateTime.Now:yyyyMMdd-HHmmss}-pid{Environment.ProcessId}.dmp");
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write,
                FileShare.Read, 64 * 1024, FileOptions.WriteThrough);
            using var process = Process.GetCurrentProcess();
            var flags = MiniDumpType.WithHandleData | MiniDumpType.WithUnloadedModules |
                MiniDumpType.WithProcessThreadData | MiniDumpType.WithFullMemoryInfo |
                MiniDumpType.WithThreadInfo;
            var success = MiniDumpWriteDump(process.Handle, (uint)process.Id,
                stream.SafeFileHandle, flags, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            Write(success ? "DUMP" : "ERROR",
                success ? $"Created {path}" : $"MiniDumpWriteDump failed: {Marshal.GetLastWin32Error()}");
        }
        catch (Exception exception) { Write("ERROR", $"Dump creation failed: {exception}"); }
        finally { Volatile.Write(ref _dumpInProgress, 0); }
    }

    private static void EnsureSessionLog()
    {
        if (_sessionLogPath is not null) return;
        lock (FileGate)
        {
            if (_sessionLogPath is not null) return;
            Directory.CreateDirectory(DiagnosticsFolder);
            _sessionLogPath = Path.Combine(DiagnosticsFolder,
                $"session-{DateTime.Now:yyyyMMdd-HHmmss}-pid{Environment.ProcessId}.log");
        }
    }

    private static void Write(string category, string message)
    {
        if (!_enabled) return;
        try
        {
            EnsureLogWriter();
            LogLines.Writer.TryWrite(FormatLine(category, message));
        }
        catch { }
    }

    private static string FormatLine(string category, string message) =>
        $"[{DateTime.Now:O}] [{category}] [T{Environment.CurrentManagedThreadId}] " +
        message.Replace("\0", "") + Environment.NewLine;

    private static void EnsureLogWriter()
    {
        if (Interlocked.Exchange(ref _logWriterStarted, 1) != 0) return;
        _ = Task.Run(ProcessLogLinesAsync);
    }

    private static async Task ProcessLogLinesAsync()
    {
        await foreach (var line in LogLines.Reader.ReadAllAsync())
            AppendLine(line);
    }

    private static void AppendLine(string line)
    {
        try
        {
            EnsureSessionLog();
            lock (FileGate) File.AppendAllText(_sessionLogPath!, line);
        }
        catch { }
    }

    private static void WriteCritical(string category, string message)
    {
        if (!_enabled) return;
        FlushPendingLogLines();
        AppendLine(FormatLine(category, message));
    }

    private static void FlushPendingLogLines()
    {
        while (LogLines.Reader.TryRead(out var line)) AppendLine(line);
    }

    private static void CleanupOldFiles()
    {
        try
        {
            if (!Directory.Exists(DiagnosticsFolder)) return;
            var cutoff = DateTime.Now.AddDays(-30);
            foreach (var file in Directory.EnumerateFiles(DiagnosticsFolder))
                try { if (File.GetLastWriteTime(file) < cutoff) File.Delete(file); }
                catch { }
            foreach (var pattern in new[] { "*.dmp", "*.log" })
            {
                var keep = pattern == "*.dmp" ? 20 : 50;
                foreach (var file in Directory.EnumerateFiles(DiagnosticsFolder, pattern)
                             .Select(path => new FileInfo(path))
                             .OrderByDescending(file => file.LastWriteTimeUtc).Skip(keep))
                    try { file.Delete(); }
                    catch { }
            }
        }
        catch { }
    }

    private static string SanitizeForLog(string value) =>
        value.Replace("\r", " ").Replace("\n", " ");
}
