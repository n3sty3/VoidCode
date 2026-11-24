using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace VoidCode.Services;

/// <summary>
/// POSIX pseudo-terminal backend for macOS (and Linux) using <c>openpty</c>,
/// <c>ioctl(TIOCSWINSZ)</c>, and <c>posix_spawn</c> (async-signal-safe in multi-threaded .NET).
/// </summary>
internal sealed class UnixPtyBackend : IDisposable
{
    private const int F_SETFD = 2;
    private const int FD_CLOEXEC = 1;
    private const int SIGKILL = 9;
    private const int SIGHUP = 1;

    // macOS/BSD TIOCSWINSZ = 0x80087467; Linux TIOCSWINSZ = 0x5414
    private static ulong Tiocswinsz => OperatingSystem.IsMacOS() ? 0x80087467UL : 0x5414UL;

    // macOS POSIX_SPAWN_SETSID = 0x0400 (available since macOS 10.14)
    private const short POSIX_SPAWN_SETSID_MAC = 0x0400;

    [StructLayout(LayoutKind.Sequential)]
    private struct Winsize
    {
        public ushort ws_row;
        public ushort ws_col;
        public ushort ws_xpixel;
        public ushort ws_ypixel;
    }

    // macOS ships openpty, ioctl, fcntl, posix_spawn, waitpid, kill in libSystem.B.dylib.
    [DllImport("libSystem.B.dylib", EntryPoint = "openpty", SetLastError = true)]
    private static extern int MacOpenPty(out int amaster, out int aslave, IntPtr name, IntPtr termp, ref Winsize winp);

    [DllImport("libutil.so.1", EntryPoint = "openpty", SetLastError = true)]
    private static extern int LinuxOpenPty(out int amaster, out int aslave, IntPtr name, IntPtr termp, ref Winsize winp);

    [DllImport("libc", SetLastError = true)]
    private static extern int ioctl(int fd, ulong request, ref Winsize argp);

    [DllImport("libc", SetLastError = true)]
    private static extern int fcntl(int fd, int cmd, int arg);

    [DllImport("libc", SetLastError = true)]
    private static extern int close(int fd);

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int sig);

    [DllImport("libc", SetLastError = true)]
    private static extern int waitpid(int pid, out int status, int options);

    [DllImport("libc", SetLastError = true)]
    private static extern int posix_spawn_file_actions_init(out IntPtr fileActions);

    [DllImport("libc", SetLastError = true)]
    private static extern int posix_spawn_file_actions_destroy(ref IntPtr fileActions);

    [DllImport("libc", SetLastError = true)]
    private static extern int posix_spawn_file_actions_adddup2(ref IntPtr fileActions, int fildes, int newfildes);

    [DllImport("libc", SetLastError = true)]
    private static extern int posix_spawn_file_actions_addclose(ref IntPtr fileActions, int fildes);

    [DllImport("libc", SetLastError = true)]
    private static extern int posix_spawn_file_actions_addchdir_np(ref IntPtr fileActions, string path);

    [DllImport("libc", SetLastError = true)]
    private static extern int posix_spawnattr_init(out IntPtr attr);

    [DllImport("libc", SetLastError = true)]
    private static extern int posix_spawnattr_destroy(ref IntPtr attr);

    [DllImport("libc", SetLastError = true)]
    private static extern int posix_spawnattr_setflags(ref IntPtr attr, short flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int posix_spawn(
        out int pid,
        string path,
        ref IntPtr fileActions,
        ref IntPtr attr,
        IntPtr[] argv,
        IntPtr[] envp);

    private int _masterFd = -1;
    private int _childPid = -1;
    private FileStream? _masterStream;
    private SafeFileHandle? _masterHandle;
    private readonly CancellationTokenSource _cts = new();

    public bool IsAlive { get; private set; }
    public event Action<string>? OnOutput;
    public event Action<int>? OnExit;

    public bool Start(string shellType, string? workingDir, int cols, int rows)
    {
        if (cols < 1) cols = 80;
        if (rows < 1) rows = 24;

        var ws = new Winsize { ws_col = (ushort)cols, ws_row = (ushort)rows };
        int rc = OperatingSystem.IsMacOS()
            ? MacOpenPty(out _masterFd, out int slaveFd, IntPtr.Zero, IntPtr.Zero, ref ws)
            : LinuxOpenPty(out _masterFd, out slaveFd, IntPtr.Zero, IntPtr.Zero, ref ws);

        if (rc != 0 || _masterFd < 0 || slaveFd < 0)
        {
            LoggingService.LogError($"UnixPty openpty failed (errno={Marshal.GetLastPInvokeError()}).", source: nameof(UnixPtyBackend));
            return false;
        }

        // Prevent child from inheriting the master PTY descriptor.
        fcntl(_masterFd, F_SETFD, FD_CLOEXEC);

        var (exePath, args, extraEnv) = ResolveUnixShell(shellType);
        var effectiveDir = !string.IsNullOrEmpty(workingDir) && Directory.Exists(workingDir)
            ? workingDir
            : Environment.CurrentDirectory;

        var argvPtrs = AllocStringArray(args);
        var envPtrs = AllocStringArray(BuildEnvironment(extraEnv));
        IntPtr fileActions = IntPtr.Zero;
        IntPtr attr = IntPtr.Zero;

        try
        {
            posix_spawn_file_actions_init(out fileActions);
            posix_spawn_file_actions_adddup2(ref fileActions, slaveFd, 0);
            posix_spawn_file_actions_adddup2(ref fileActions, slaveFd, 1);
            posix_spawn_file_actions_adddup2(ref fileActions, slaveFd, 2);
            posix_spawn_file_actions_addclose(ref fileActions, slaveFd);
            posix_spawn_file_actions_addclose(ref fileActions, _masterFd);
            try { posix_spawn_file_actions_addchdir_np(ref fileActions, effectiveDir); }
            catch (EntryPointNotFoundException) { /* older libc without addchdir_np */ }

            posix_spawnattr_init(out attr);
            if (OperatingSystem.IsMacOS())
            {
                posix_spawnattr_setflags(ref attr, POSIX_SPAWN_SETSID_MAC);
            }

            int spawnRc = posix_spawn(out _childPid, exePath, ref fileActions, ref attr, argvPtrs, envPtrs);
            close(slaveFd);

            if (spawnRc != 0 || _childPid <= 0)
            {
                LoggingService.LogError($"UnixPty posix_spawn('{exePath}') failed (rc={spawnRc}).", source: nameof(UnixPtyBackend));
                close(_masterFd);
                _masterFd = -1;
                return false;
            }
        }
        finally
        {
            if (fileActions != IntPtr.Zero) posix_spawn_file_actions_destroy(ref fileActions);
            if (attr != IntPtr.Zero) posix_spawnattr_destroy(ref attr);
            FreeStringArray(argvPtrs);
            FreeStringArray(envPtrs);
        }

        IsAlive = true;
        _masterHandle = new SafeFileHandle((IntPtr)_masterFd, ownsHandle: true);
        _masterStream = new FileStream(_masterHandle, FileAccess.ReadWrite, 4096, false);

        _ = Task.Run(ReadLoopAsync);
        _ = Task.Run(WaitExitAsync);
        return true;
    }

    public bool Write(string data)
    {
        if (!IsAlive || _masterStream == null) return false;
        try
        {
            var bytes = Encoding.UTF8.GetBytes(data);
            _masterStream.Write(bytes, 0, bytes.Length);
            _masterStream.Flush();
            return true;
        }
        catch (Exception) { return false; }
    }

    public void Resize(int cols, int rows)
    {
        if (_masterFd < 0 || !IsAlive) return;
        if (cols < 1) cols = 80;
        if (rows < 1) rows = 24;
        try
        {
            var ws = new Winsize { ws_col = (ushort)cols, ws_row = (ushort)rows };
            ioctl(_masterFd, Tiocswinsz, ref ws);
        }
        catch (Exception) { /* best-effort resize */ }
    }

    public void Kill()
    {
        if (_childPid > 0)
        {
            try
            {
                kill(-_childPid, SIGHUP);
                kill(_childPid, SIGKILL);
            }
            catch (Exception) { /* child already exited */ }
        }
        Dispose();
    }

    public void Dispose()
    {
        IsAlive = false;
        try { _cts.Cancel(); } catch (Exception) { /* already cancelled */ }
        try { _masterStream?.Dispose(); } catch (Exception) { /* stream closed */ }
        try { _masterHandle?.Dispose(); } catch (Exception) { /* handle closed */ }
        _masterFd = -1;
    }

    private async Task ReadLoopAsync()
    {
        var buffer = new byte[4096];
        var decoder = Encoding.UTF8.GetDecoder();
        var charBuf = new char[4096];

        try
        {
            while (!_cts.Token.IsCancellationRequested && _masterStream != null)
            {
                int bytesRead = await _masterStream.ReadAsync(buffer, 0, buffer.Length, _cts.Token);
                if (bytesRead <= 0) break;
                int chars = decoder.GetChars(buffer, 0, bytesRead, charBuf, 0);
                if (chars > 0)
                {
                    OnOutput?.Invoke(new string(charBuf, 0, chars));
                }
            }
        }
        catch (OperationCanceledException) { /* normal shutdown */ }
        catch (IOException) { /* PTY master closed when child exits */ }
        catch (ObjectDisposedException) { /* disposed during read */ }
        catch (Exception ex) { LoggingService.Swallowed(ex, "UnixPtyBackend.ReadLoop"); }
        finally
        {
            IsAlive = false;
        }
    }

    private Task WaitExitAsync()
    {
        int exitCode = 0;
        try
        {
            if (_childPid > 0)
            {
                waitpid(_childPid, out int status, 0);
                exitCode = (status >> 8) & 0xFF;
            }
        }
        catch (Exception) { /* process wait interrupted */ }
        finally
        {
            IsAlive = false;
            OnExit?.Invoke(exitCode);
        }
        return Task.CompletedTask;
    }

    private static (string exe, string[] args, Dictionary<string, string> env) ResolveUnixShell(string shellType)
    {
        var extraEnv = new Dictionary<string, string>(StringComparer.Ordinal);
        var normalized = (shellType ?? "").Trim().ToLowerInvariant();
        if (normalized == "powershell" || normalized == "cmd" || string.IsNullOrEmpty(normalized))
        {
            var envShell = Environment.GetEnvironmentVariable("SHELL");
            if (!string.IsNullOrWhiteSpace(envShell) && File.Exists(envShell))
            {
                normalized = Path.GetFileName(envShell).ToLowerInvariant();
            }
            else
            {
                normalized = OperatingSystem.IsMacOS() ? "zsh" : "bash";
            }
        }

        if (normalized == "pwsh")
        {
            var pwsh = LspService.FindInPath("pwsh");
            if (!string.IsNullOrEmpty(pwsh))
            {
                return (pwsh, new[] { pwsh, "-NoLogo" }, extraEnv);
            }
            normalized = OperatingSystem.IsMacOS() ? "zsh" : "bash";
        }

        if (normalized == "zsh" && File.Exists("/bin/zsh"))
        {
            var zdotdir = EnsureZshOsc133DotDir();
            if (!string.IsNullOrEmpty(zdotdir))
            {
                extraEnv["VOIDCODE_ORIG_ZDOTDIR"] = Environment.GetEnvironmentVariable("ZDOTDIR") ?? "";
                extraEnv["ZDOTDIR"] = zdotdir;
            }
            return ("/bin/zsh", new[] { "/bin/zsh", "-i" }, extraEnv);
        }

        if (normalized == "bash" && File.Exists("/bin/bash"))
        {
            return ("/bin/bash", new[] { "/bin/bash", "-i" }, extraEnv);
        }

        var fallback = File.Exists("/bin/zsh") ? "/bin/zsh" : (File.Exists("/bin/bash") ? "/bin/bash" : "/bin/sh");
        return (fallback, new[] { fallback, "-i" }, extraEnv);
    }

    /// <summary>
    /// Creates a tiny ZDOTDIR wrapper that sources the user's real ~/.zshrc and attaches
    /// lightweight OSC 133 precmd/preexec hooks so macOS zsh gets VoidCode Command Blocks.
    /// </summary>
    private static string? EnsureZshOsc133DotDir()
    {
        try
        {
            var dir = Path.Combine(StorageService.Root, "shell", "zsh");
            Directory.CreateDirectory(dir);
            var zshrc = Path.Combine(dir, ".zshrc");
            const string script =
                "if [[ -n \"$VOIDCODE_ORIG_ZDOTDIR\" && -f \"$VOIDCODE_ORIG_ZDOTDIR/.zshrc\" ]]; then\n" +
                "  source \"$VOIDCODE_ORIG_ZDOTDIR/.zshrc\"\n" +
                "elif [[ -f \"$HOME/.zshrc\" ]]; then\n" +
                "  source \"$HOME/.zshrc\"\n" +
                "fi\n" +
                "__voidcode_precmd() {\n" +
                "  local ec=$?\n" +
                "  printf '\\e]133;D;%d\\a\\e]133;A\\a' \"$ec\"\n" +
                "}\n" +
                "__voidcode_preexec() {\n" +
                "  printf '\\e]133;C\\a'\n" +
                "}\n" +
                "autoload -Uz add-zsh-hook\n" +
                "add-zsh-hook precmd __voidcode_precmd\n" +
                "add-zsh-hook preexec __voidcode_preexec\n";
            File.WriteAllText(zshrc, script, new UTF8Encoding(false));
            return dir;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string[] BuildEnvironment(Dictionary<string, string> overrides)
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string k && entry.Value is string v)
            {
                dict[k] = v;
            }
        }
        dict["TERM"] = "xterm-256color";
        dict["COLORTERM"] = "truecolor";
        dict["TERM_PROGRAM"] = "VoidCode";
        if (!dict.ContainsKey("LANG")) dict["LANG"] = "en_US.UTF-8";
        foreach (var kv in overrides) dict[kv.Key] = kv.Value;

        var result = new string[dict.Count];
        int i = 0;
        foreach (var kv in dict) result[i++] = $"{kv.Key}={kv.Value}";
        return result;
    }

    private static IntPtr[] AllocStringArray(string[] items)
    {
        var ptrs = new IntPtr[items.Length + 1];
        for (int i = 0; i < items.Length; i++)
        {
            ptrs[i] = Marshal.StringToCoTaskMemUTF8(items[i]);
        }
        ptrs[items.Length] = IntPtr.Zero;
        return ptrs;
    }

    private static void FreeStringArray(IntPtr[] ptrs)
    {
        foreach (var p in ptrs)
        {
            if (p != IntPtr.Zero) Marshal.FreeCoTaskMem(p);
        }
    }
}
