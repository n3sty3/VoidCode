using System;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace VoidCode.Services;

internal static class NativeConPty
{
    internal const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    internal const int PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x00020016;
    internal const uint INFINITE = 0xFFFFFFFF;
    internal const uint WAIT_OBJECT_0 = 0x00000000;

    [StructLayout(LayoutKind.Sequential)]
    internal struct COORD
    {
        public short X;
        public short Y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct STARTUPINFOEX
    {
        public STARTUPINFO StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct STARTUPINFO
    {
        public int cb;
        public string lpReserved;
        public string lpDesktop;
        public string lpTitle;
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
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool CreatePipe(out SafeFileHandle hReadPipe, out SafeFileHandle hWritePipe, IntPtr lpPipeAttributes, int nSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern int CreatePseudoConsole(COORD size, SafeFileHandle hInput, SafeFileHandle hOutput, uint dwFlags, out IntPtr phPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern int ResizePseudoConsole(IntPtr hPC, COORD size);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern void ClosePseudoConsole(IntPtr hPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount, int dwFlags, ref IntPtr lpSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool UpdateProcThreadAttribute(
        IntPtr lpAttributeList,
        uint dwFlags,
        IntPtr Attribute,
        IntPtr lpValue,
        IntPtr cbSize,
        IntPtr lpPreviousValue,
        IntPtr lpReturnSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool DeleteProcThreadAttributeList(IntPtr lpAttributeList);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern bool CreateProcess(
        string? lpApplicationName,
        StringBuilder lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        [In] ref STARTUPINFOEX lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);
}

public sealed class PtySession : IDisposable
{
    public string Id { get; }
    public string Title { get; set; }
    public string ShellType { get; set; }
    public bool IsAlive { get; private set; }

    private IntPtr _hPC = IntPtr.Zero;
    private SafeFileHandle? _pipeInWrite;
    private SafeFileHandle? _pipeOutRead;
    private FileStream? _inStream;
    private FileStream? _outStream;
    private readonly Decoder _utf8Decoder = Encoding.UTF8.GetDecoder();
    private NativeConPty.PROCESS_INFORMATION _procInfo;
    private readonly CancellationTokenSource _cts = new();
    private Task? _readLoopTask;
    private Task? _waitExitTask;
    private UnixPtyBackend? _unixBackend;

    public event Action<string, string>? OnOutput;
    public event Action<string, int>? OnExit;

    public PtySession(string id, string title = "PowerShell", string shellType = "powershell")
    {
        Id = id;
        Title = title;
        ShellType = shellType;
    }

    public bool Start(string? workingDir = null, int cols = 80, int rows = 24)
    {
        if (cols < 1) cols = 80;
        if (rows < 1) rows = 24;

        if (!OperatingSystem.IsWindows())
        {
            _unixBackend = new UnixPtyBackend();
            _unixBackend.OnOutput += text => OnOutput?.Invoke(Id, text);
            _unixBackend.OnExit += code =>
            {
                IsAlive = false;
                OnExit?.Invoke(Id, code);
            };
            IsAlive = _unixBackend.Start(ShellType, workingDir, cols, rows);
            return IsAlive;
        }

        // 1. Create Pipes
        if (!NativeConPty.CreatePipe(out var hPipePtyIn, out _pipeInWrite, IntPtr.Zero, 0))
            return false;
        if (!NativeConPty.CreatePipe(out _pipeOutRead, out var hPipePtyOut, IntPtr.Zero, 0))
        {
            hPipePtyIn.Dispose();
            _pipeInWrite?.Dispose();
            return false;
        }

        // 2. Create PseudoConsole
        var size = new NativeConPty.COORD { X = (short)cols, Y = (short)rows };
        int hr = NativeConPty.CreatePseudoConsole(size, hPipePtyIn, hPipePtyOut, 0, out _hPC);

        // Microsoft ConPTY: Parent closes these handles immediately after CreatePseudoConsole
        hPipePtyIn.Dispose();
        hPipePtyOut.Dispose();

        if (hr != 0 || _hPC == IntPtr.Zero)
        {
            _pipeInWrite?.Dispose();
            _pipeOutRead?.Dispose();
            return false;
        }

        // 3. Prepare STARTUPINFOEX with PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE
        var lpSize = IntPtr.Zero;
        NativeConPty.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref lpSize);
        var attrList = Marshal.AllocHGlobal(lpSize);

        if (!NativeConPty.InitializeProcThreadAttributeList(attrList, 1, 0, ref lpSize))
        {
            Marshal.FreeHGlobal(attrList);
            ClosePseudoConsole();
            return false;
        }

        if (!NativeConPty.UpdateProcThreadAttribute(
                attrList,
                0,
                (IntPtr)NativeConPty.PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
                _hPC,
                (IntPtr)IntPtr.Size,
                IntPtr.Zero,
                IntPtr.Zero))
        {
            NativeConPty.DeleteProcThreadAttributeList(attrList);
            Marshal.FreeHGlobal(attrList);
            ClosePseudoConsole();
            return false;
        }

        var siEx = new NativeConPty.STARTUPINFOEX();
        siEx.StartupInfo.cb = Marshal.SizeOf<NativeConPty.STARTUPINFOEX>();
        siEx.lpAttributeList = attrList;

        // 4. Resolve shell command
        string commandLine = ResolveShellCommandLine(ShellType);
        var sbCmd = new StringBuilder(commandLine);

        string effectiveDir = !string.IsNullOrEmpty(workingDir) && Directory.Exists(workingDir)
            ? workingDir
            : Environment.CurrentDirectory;

        bool created = NativeConPty.CreateProcess(
            null,
            sbCmd,
            IntPtr.Zero,
            IntPtr.Zero,
            false,
            NativeConPty.EXTENDED_STARTUPINFO_PRESENT,
            IntPtr.Zero,
            effectiveDir,
            ref siEx,
            out _procInfo);

        NativeConPty.DeleteProcThreadAttributeList(attrList);
        Marshal.FreeHGlobal(attrList);

        if (!created)
        {
            ClosePseudoConsole();
            return false;
        }

        if (_procInfo.hThread != IntPtr.Zero)
        {
            NativeConPty.CloseHandle(_procInfo.hThread);
            _procInfo.hThread = IntPtr.Zero;
        }

        IsAlive = true;
        _inStream = new FileStream(_pipeInWrite, FileAccess.Write, 4096, false);
        _outStream = new FileStream(_pipeOutRead, FileAccess.Read, 4096, false);

        // 5. Start async reading loop
        _readLoopTask = Task.Run(ReadLoopAsync);

        // 6. Start exit monitor
        _waitExitTask = Task.Run(WaitExitAsync);

        return true;
    }

    private static string ResolveShellCommandLine(string shellType)
    {
        var raw = (shellType ?? "").Trim();
        if (raw.StartsWith("wsl:", StringComparison.OrdinalIgnoreCase))
        {
            var distro = raw["wsl:".Length..].Trim();
            if (!string.IsNullOrEmpty(distro) && System.Text.RegularExpressions.Regex.IsMatch(distro, @"^[A-Za-z0-9._-]+$"))
                return $"wsl.exe -d {distro}";
            return "wsl.exe";
        }
        return raw.ToLowerInvariant() switch
        {
            "cmd" => "cmd.exe",
            "wsl" => "wsl.exe",
            "bash" => File.Exists(@"C:\Program Files\Git\bin\bash.exe")
                ? @"C:\Program Files\Git\bin\bash.exe -i -l"
                : BuildPowerShellCommandLine(),
            _ => BuildPowerShellCommandLine()
        };
    }

    /// <summary>
    /// Spawns PowerShell with the console init script handed over at startup.
    /// Typing the prompt setup into a live session raced the shell, echoed the
    /// Command, and was silently dropped if the PTY was not ready yet.
    /// </summary>
    private static string BuildPowerShellCommandLine()
    {
        var initScript = Path.Combine(AppContext.BaseDirectory, "wwwroot", "console", "init.ps1");
        if (!File.Exists(initScript)) return "powershell.exe -NoLogo";

        return $"powershell.exe -NoLogo -NoExit -ExecutionPolicy Bypass -File \"{initScript}\"";
    }

    private async Task ReadLoopAsync()
    {
        var buffer = new byte[8192];
        var chars = new char[Encoding.UTF8.GetMaxCharCount(buffer.Length)];
        try
        {
            while (!_cts.Token.IsCancellationRequested && _outStream != null)
            {
                int bytesRead = await _outStream.ReadAsync(buffer, 0, buffer.Length, _cts.Token);
                if (bytesRead <= 0) break;

                int charCount = _utf8Decoder.GetChars(buffer, 0, bytesRead, chars, 0, flush: false);
                string text = new(chars, 0, charCount);
                OnOutput?.Invoke(Id, text);
            }
        }
        catch (OperationCanceledException) { /* read loop cancelled */ }
        catch (Exception ex)
        {
            OnOutput?.Invoke(Id, $"\r\n[PTY Read Error: {ex.Message}]\r\n");
        }
    }

    private async Task WaitExitAsync()
    {
        await Task.Run(() =>
        {
            if (_procInfo.hProcess != IntPtr.Zero)
            {
                NativeConPty.WaitForSingleObject(_procInfo.hProcess, NativeConPty.INFINITE);
                NativeConPty.GetExitCodeProcess(_procInfo.hProcess, out uint exitCode);
                IsAlive = false;
                OnExit?.Invoke(Id, (int)exitCode);
            }
        });
    }

    public bool Write(string data)
    {
        if (_unixBackend != null) return _unixBackend.Write(data);
        if (!IsAlive || _inStream == null) return false;
        try
        {
            byte[] bytes = Encoding.UTF8.GetBytes(data);
            _inStream.Write(bytes, 0, bytes.Length);
            _inStream.Flush();
            return true;
        }
        catch { return false; }
    }

    public void Resize(int cols, int rows)
    {
        if (_unixBackend != null)
        {
            _unixBackend.Resize(cols, rows);
            return;
        }
        if (_hPC == IntPtr.Zero) return;
        if (cols < 1) cols = 80;
        if (rows < 1) rows = 24;

        try
        {
            var size = new NativeConPty.COORD { X = (short)cols, Y = (short)rows };
            NativeConPty.ResizePseudoConsole(_hPC, size);
        }
        catch (Exception) { /* pty resize is best-effort */ }
    }

    public void Kill()
    {
        if (_unixBackend != null)
        {
            _unixBackend.Kill();
            IsAlive = false;
            return;
        }
        try
        {
            if (_procInfo.hProcess != IntPtr.Zero)
            {
                NativeConPty.TerminateProcess(_procInfo.hProcess, 1);
            }
        }
        catch (Exception) { /* process already exited */ }
        Dispose();
    }

    private void ClosePseudoConsole()
    {
        if (_hPC != IntPtr.Zero)
        {
            try { NativeConPty.ClosePseudoConsole(_hPC); } catch (Exception) { /* pseudo console already closed */ }
            _hPC = IntPtr.Zero;
        }
    }

    public void Dispose()
    {
        IsAlive = false;
        if (_unixBackend != null)
        {
            _unixBackend.Dispose();
            _unixBackend = null;
        }
        try { _cts.Cancel(); } catch (Exception) { /* CTS already cancelled or disposed */ }

        try { _inStream?.Dispose(); } catch (Exception) { /* stdin stream already disposed */ }
        try { _outStream?.Dispose(); } catch (Exception) { /* stdout stream already disposed */ }
        try { _pipeInWrite?.Dispose(); } catch (Exception) { /* pipe already disposed */ }
        try { _pipeOutRead?.Dispose(); } catch (Exception) { /* pipe already disposed */ }

        ClosePseudoConsole();

        if (_procInfo.hProcess != IntPtr.Zero)
        {
            try { NativeConPty.CloseHandle(_procInfo.hProcess); } catch (Exception) { /* handle already closed */ }
            _procInfo.hProcess = IntPtr.Zero;
        }
    }
}

public static class PtyManager
{
    private static readonly ConcurrentDictionary<string, PtySession> _sessions = new();

    public static PtySession GetOrCreate(
        string id,
        string? workingDir,
        string shellType = "powershell",
        int cols = 80,
        int rows = 24,
        Action<string, string>? onOutput = null,
        Action<string, int>? onExit = null)
    {
        return _sessions.GetOrAdd(id, key =>
        {
            var session = new PtySession(key, key.StartsWith("term-") ? $"Terminal {key.Replace("term-", "")}" : key, shellType);
            if (onOutput != null) session.OnOutput += onOutput;
            if (onExit != null) session.OnExit += onExit;
            session.Start(workingDir, cols, rows);
            return session;
        });
    }

    public static PtySession? Get(string id)
    {
        _sessions.TryGetValue(id, out var session);
        return session;
    }

    public static bool Write(string id, string text)
    {
        if (!_sessions.TryGetValue(id, out var session))
        {
            // This used to fail silently, which is why an early write (a styled
            // Prompt sent before the shell existed) vanished with no trace
            LoggingService.LogWarning($"pty_input dropped: no live session '{id}'");
            return false;
        }

        if (!session.Write(text))
        {
            LoggingService.LogWarning($"pty_input dropped: session '{id}' not ready");
            return false;
        }

        return true;
    }

    public static void Resize(string id, int cols, int rows)
    {
        if (_sessions.TryGetValue(id, out var session))
        {
            session.Resize(cols, rows);
        }
    }

    public static void Kill(string id)
    {
        if (_sessions.TryRemove(id, out var session))
        {
            session.Kill();
        }
    }

    public static void KillAll()
    {
        foreach (var kvp in _sessions)
        {
            kvp.Value.Kill();
        }
        _sessions.Clear();
    }
}
