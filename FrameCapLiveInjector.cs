using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace DeskrawlHubClient;

internal static class FrameCapLiveInjector
{
    private static readonly ConcurrentDictionary<int, string> StatusPathByProcess = new();
    private static readonly JsonSerializerOptions SettingsJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private const uint ProcessCreateThread = 0x0002;
    private const uint ProcessVmOperation = 0x0008;
    private const uint ProcessVmRead = 0x0010;
    private const uint ProcessVmWrite = 0x0020;
    private const uint ProcessQueryInformation = 0x0400;
    private const uint MemCommitReserve = 0x3000;
    private const uint MemRelease = 0x8000;
    private const uint PageReadWrite = 0x04;
    private const uint WaitObject0 = 0;
    private const uint WaitTimeout = 0x102;
    private const string LivePluginPrefix = "DeskrawlConsole.FrameCap.Live.";
    private const string StartExport = "DeskrawlFrameCapStart";

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern IntPtr OpenProcess(uint access, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualAllocEx(IntPtr process, IntPtr address, UIntPtr size, uint allocationType, uint protect);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualFreeEx(IntPtr process, IntPtr address, UIntPtr size, uint freeType);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern bool WriteProcessMemory(IntPtr process, IntPtr address, byte[] buffer, UIntPtr size, out UIntPtr written);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateRemoteThread(
        IntPtr process,
        IntPtr threadAttributes,
        UIntPtr stackSize,
        IntPtr startAddress,
        IntPtr parameter,
        uint creationFlags,
        out uint threadId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeThread(IntPtr thread, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern IntPtr GetModuleHandleW(string moduleName);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)]
    private static extern IntPtr GetProcAddress(IntPtr module, string procedureName);

    internal static FrameCapProcessResult Apply(
        int processId,
        FrameCapClientSettings settings,
        string settingsPath,
        string? pluginPath,
        string dataRoot)
    {
        try
        {
            using Process target = Process.GetProcessById(processId);
            target.Refresh();
            string? executablePath = target.MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(executablePath)
                || !Path.GetFileName(executablePath).Equals("Deskrawl.exe", StringComparison.OrdinalIgnoreCase))
                return new FrameCapProcessResult(false, "error", "账号对应的游戏进程已经变化。");
            ProcessModule[] modules = GetModules(target);
            ProcessModule[] loadedPlugins = modules
                .Where(module => Path.GetFileName(module.ModuleName).StartsWith(LivePluginPrefix, StringComparison.OrdinalIgnoreCase))
                .ToArray();

            if (loadedPlugins.Length > 0
                && (loadedPlugins.Length != 1
                    || string.IsNullOrWhiteSpace(pluginPath)
                    || !Path.GetFullPath(loadedPlugins[0].FileName).Equals(
                        Path.GetFullPath(pluginPath), StringComparison.OrdinalIgnoreCase)))
            {
                return new FrameCapProcessResult(false, "restart-required",
                    "此游戏进程仍载入旧限帧组件，请先关闭并重新启动游戏。重启后即可使用锁帧。");
            }

            if (!settings.Enabled && loadedPlugins.Length == 0)
                return new FrameCapProcessResult(true, "disabled", "限帧已关闭。");

            if (!modules.Any(module => module.ModuleName.Equals("GameAssembly.dll", StringComparison.OrdinalIgnoreCase)))
                return new FrameCapProcessResult(false, "waiting", "游戏正在启动。");

            if (settings.Enabled && (string.IsNullOrWhiteSpace(pluginPath) || !File.Exists(pluginPath)))
                return new FrameCapProcessResult(false, "error", "限帧组件未就绪。");

            WriteSettingsFile(settingsPath, settings);

            if (!settings.Enabled)
            {
                string firstError = "";
                foreach (ProcessModule loadedPlugin in loadedPlugins)
                {
                    long invokedAtUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    uint startCode = InvokePluginStart(target, loadedPlugin.FileName, settingsPath);
                    if (!TryReadStatus(processId, settings, settingsPath, dataRoot, invokedAtUtc,
                        out FrameCapProcessResult? pluginStatus)
                        || pluginStatus is null || !pluginStatus.Success || pluginStatus.State != "disabled")
                    {
                        firstError = pluginStatus?.Message ?? (startCode == 0
                            ? "游戏暂未确认帧率已恢复。"
                            : DescribeStartError(startCode));
                    }
                }
                if (!string.IsNullOrWhiteSpace(firstError))
                    return new FrameCapProcessResult(false, "waiting", firstError);
                return new FrameCapProcessResult(true, "disabled", "限帧已关闭。");
            }
            else
            {
                string activePluginPath = pluginPath!;
                ProcessModule? currentPlugin = modules.FirstOrDefault(module =>
                    Path.GetFileName(module.FileName).Equals(Path.GetFileName(activePluginPath), StringComparison.OrdinalIgnoreCase));
                if (currentPlugin is null)
                {
                    LoadPluginIntoProcess(target, activePluginPath);
                    modules = GetModules(target);
                    currentPlugin = modules.FirstOrDefault(module =>
                        Path.GetFileName(module.FileName).Equals(Path.GetFileName(activePluginPath), StringComparison.OrdinalIgnoreCase));
                }
                if (currentPlugin is null)
                    return new FrameCapProcessResult(false, "error", "游戏没有载入限帧组件。");
                long invokedAtUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                uint startCode = InvokePluginStart(target, currentPlugin.FileName, settingsPath);
                if (TryReadStatus(processId, settings, settingsPath, dataRoot, invokedAtUtc, out FrameCapProcessResult? status))
                    return status!;
                if (startCode != 0)
                    return new FrameCapProcessResult(false, "waiting", DescribeStartError(startCode));
                return new FrameCapProcessResult(false, "waiting", "游戏暂未确认帧率设置。");
            }
        }
        catch (ArgumentException)
        {
            return new FrameCapProcessResult(false, "waiting", "游戏进程已关闭。");
        }
        catch (Exception ex)
        {
            return new FrameCapProcessResult(false, "error", DescribeException(ex));
        }
    }

    private static void WriteSettingsFile(string path, FrameCapClientSettings settings)
    {
        string directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("限帧设置目录无效。");
        Directory.CreateDirectory(directory);
        string temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath,
            JsonSerializer.Serialize(settings, SettingsJsonOptions), new UTF8Encoding(false));
        File.Move(temporaryPath, path, overwrite: true);
    }

    private static void LoadPluginIntoProcess(Process target, string pluginPath)
    {
        IntPtr processHandle = OpenProcess(
            ProcessCreateThread | ProcessQueryInformation | ProcessVmOperation | ProcessVmRead | ProcessVmWrite,
            inheritHandle: false,
            target.Id);
        if (processHandle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());

        IntPtr remotePath = IntPtr.Zero;
        bool keepRemotePath = false;
        try
        {
            remotePath = AllocateRemoteString(processHandle, pluginPath);
            IntPtr remoteLoadLibrary = ResolveRemoteLoadLibrary(target);
            _ = RunRemoteThread(processHandle, remoteLoadLibrary, remotePath, 10000, out bool completed);
            keepRemotePath = !completed;
            if (!completed) throw new TimeoutException("游戏正在载入限帧组件。");
        }
        finally
        {
            if (remotePath != IntPtr.Zero && !keepRemotePath)
                _ = VirtualFreeEx(processHandle, remotePath, UIntPtr.Zero, MemRelease);
            _ = CloseHandle(processHandle);
        }
    }

    private static uint InvokePluginStart(Process target, string modulePath, string settingsPath)
    {
        if (!File.Exists(modulePath)) throw new FileNotFoundException("游戏中已载入的限帧组件文件无法读取。", modulePath);

        IntPtr processHandle = OpenProcess(
            ProcessCreateThread | ProcessQueryInformation | ProcessVmOperation | ProcessVmRead | ProcessVmWrite,
            inheritHandle: false,
            target.Id);
        if (processHandle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());

        IntPtr remoteSettings = IntPtr.Zero;
        bool keepRemoteSettings = false;
        IntPtr localModule = IntPtr.Zero;
        try
        {
            remoteSettings = AllocateRemoteString(processHandle, settingsPath);
            ProcessModule remoteModule = GetModules(target).FirstOrDefault(module =>
                Path.GetFullPath(module.FileName).Equals(Path.GetFullPath(modulePath), StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("游戏中的限帧组件尚未载入。");

            localModule = NativeLibrary.Load(modulePath);
            IntPtr localExport = NativeLibrary.GetExport(localModule, StartExport);
            long exportOffset = localExport.ToInt64() - localModule.ToInt64();
            if (exportOffset < 0 || exportOffset >= remoteModule.ModuleMemorySize)
                throw new InvalidOperationException("限帧组件入口无效。");
            IntPtr remoteExport = IntPtr.Add(remoteModule.BaseAddress, checked((int)exportOffset));

            uint result = RunRemoteThread(processHandle, remoteExport, remoteSettings, 8000, out bool completed);
            keepRemoteSettings = !completed;
            if (!completed) throw new TimeoutException("游戏窗口暂时没有响应。");
            return result;
        }
        finally
        {
            if (localModule != IntPtr.Zero) NativeLibrary.Free(localModule);
            if (remoteSettings != IntPtr.Zero && !keepRemoteSettings)
                _ = VirtualFreeEx(processHandle, remoteSettings, UIntPtr.Zero, MemRelease);
            _ = CloseHandle(processHandle);
        }
    }

    private static IntPtr ResolveRemoteLoadLibrary(Process target)
    {
        IntPtr localKernel32 = GetModuleHandleW("kernel32.dll");
        IntPtr localLoadLibrary = GetProcAddress(localKernel32, "LoadLibraryW");
        if (localKernel32 == IntPtr.Zero || localLoadLibrary == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法定位 Windows 程序加载函数。");

        using Process current = Process.GetCurrentProcess();
        ProcessModule localOwner = GetModules(current).FirstOrDefault(module => ContainsAddress(module, localLoadLibrary))
            ?? throw new InvalidOperationException("无法定位 Windows 程序加载函数所在模块。");
        ProcessModule remoteOwner = GetModules(target).FirstOrDefault(module =>
            module.ModuleName.Equals(localOwner.ModuleName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("游戏系统组件尚未就绪。");

        long offset = localLoadLibrary.ToInt64() - localOwner.BaseAddress.ToInt64();
        if (offset < 0 || offset >= localOwner.ModuleMemorySize)
            throw new InvalidOperationException("Windows 程序加载函数地址无效。");
        return IntPtr.Add(remoteOwner.BaseAddress, checked((int)offset));
    }

    private static bool ContainsAddress(ProcessModule module, IntPtr address)
    {
        long value = address.ToInt64();
        long start = module.BaseAddress.ToInt64();
        return value >= start && value < start + module.ModuleMemorySize;
    }

    private static ProcessModule[] GetModules(Process process)
    {
        process.Refresh();
        return process.Modules.Cast<ProcessModule>().ToArray();
    }

    private static IntPtr AllocateRemoteString(IntPtr processHandle, string value)
    {
        byte[] bytes = Encoding.Unicode.GetBytes(value + '\0');
        IntPtr address = VirtualAllocEx(processHandle, IntPtr.Zero, new UIntPtr((uint)bytes.Length), MemCommitReserve, PageReadWrite);
        if (address == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (!WriteProcessMemory(processHandle, address, bytes, new UIntPtr((uint)bytes.Length), out UIntPtr written)
            || written.ToUInt64() != (ulong)bytes.Length)
        {
            int error = Marshal.GetLastWin32Error();
            _ = VirtualFreeEx(processHandle, address, UIntPtr.Zero, MemRelease);
            throw new Win32Exception(error);
        }
        return address;
    }

    private static uint RunRemoteThread(
        IntPtr processHandle,
        IntPtr startAddress,
        IntPtr parameter,
        uint timeoutMilliseconds,
        out bool completed)
    {
        IntPtr thread = CreateRemoteThread(processHandle, IntPtr.Zero, UIntPtr.Zero, startAddress,
            parameter, 0, out _);
        if (thread == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());

        try
        {
            uint wait = WaitForSingleObject(thread, timeoutMilliseconds);
            completed = wait == WaitObject0;
            if (wait == WaitTimeout) return 0;
            if (!completed) throw new Win32Exception(Marshal.GetLastWin32Error(), "游戏进程暂时无法处理限帧设置。");
            if (!GetExitCodeThread(thread, out uint exitCode)) throw new Win32Exception(Marshal.GetLastWin32Error());
            return exitCode;
        }
        finally
        {
            _ = CloseHandle(thread);
        }
    }

    private static bool TryReadStatus(
        int processId,
        FrameCapClientSettings settings,
        string settingsPath,
        string dataRoot,
        long invokedAtUtc,
        out FrameCapProcessResult? result)
    {
        result = null;
        string statusDirectory = Path.GetDirectoryName(settingsPath) ?? dataRoot;
        string currentName = $"frame-cap-live-status-{processId}.json";
        string legacyName = $"DeskrawlConsole.FrameCap.live-status.{processId}.json";
        var candidates = new List<string>
        {
            Path.Combine(statusDirectory, currentName),
            Path.Combine(statusDirectory, legacyName),
            Path.Combine(dataRoot, currentName),
            Path.Combine(dataRoot, legacyName),
        };
        if (StatusPathByProcess.TryGetValue(processId, out string? cachedPath))
            candidates.Insert(0, cachedPath);
        candidates.AddRange(FindSandboxStatusPaths(currentName, legacyName));

        foreach (string path in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(path)) continue;
            try
            {
                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
                JsonElement root = document.RootElement;
                if (root.TryGetProperty("pid", out JsonElement pidElement)
                    && pidElement.TryGetInt32(out int statusPid) && statusPid != processId)
                    continue;
                string state = root.TryGetProperty("state", out JsonElement stateElement) ? stateElement.GetString() ?? "" : "";
                string requestId = root.TryGetProperty("requestId", out JsonElement requestElement) ? requestElement.GetString() ?? "" : "";
                if (!string.IsNullOrEmpty(requestId) && !requestId.Equals(settings.RequestId, StringComparison.Ordinal)) continue;
                if (string.IsNullOrEmpty(requestId)
                    && (!root.TryGetProperty("updatedAtUtc", out JsonElement updatedAt)
                        || !updatedAt.TryGetInt64(out long statusTime)
                        || statusTime < invokedAtUtc - 1)) continue;
                string message = root.TryGetProperty("message", out JsonElement messageElement)
                    ? messageElement.GetString() ?? ""
                    : "";
                bool applied = settings.Enabled && state == "applied";
                bool disabled = !settings.Enabled && state == "disabled";
                if (applied || disabled)
                {
                    StatusPathByProcess[processId] = path;
                    result = new FrameCapProcessResult(true, state, message);
                    return true;
                }
                if (state is "error" or "waiting")
                {
                    StatusPathByProcess[processId] = path;
                    result = new FrameCapProcessResult(false, state, message);
                    return true;
                }
            }
            catch (IOException)
            {
                // The game may be replacing its status file; try again below.
            }
            catch (JsonException)
            {
                // Ignore an incomplete status write and report the start result instead.
            }
        }
        return false;
    }

    private static IEnumerable<string> FindSandboxStatusPaths(string currentName, string legacyName)
    {
        string systemDrive = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
        string profileName = Path.GetFileName(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        if (string.IsNullOrWhiteSpace(profileName)) profileName = Environment.UserName;
        string sandboxUserRoot = Path.Combine(systemDrive, "Sandbox", profileName);
        if (!Directory.Exists(sandboxUserRoot)) yield break;

        foreach (string boxRoot in Directory.EnumerateDirectories(sandboxUserRoot))
        {
            string appDataRoot = Path.Combine(boxRoot, "user", "current", "AppData", "Local", "DeskrawlHubClient");
            yield return Path.Combine(appDataRoot, "frame-cap", currentName);
            yield return Path.Combine(appDataRoot, "frame-cap", legacyName);
            yield return Path.Combine(appDataRoot, currentName);
            yield return Path.Combine(appDataRoot, legacyName);
        }
    }

    private static string DescribeStartError(uint code) => code switch
    {
        87 => "限帧组件无法读取设置。",
        1400 => "游戏窗口还未就绪。",
        1460 => "游戏窗口暂时没有响应。",
        _ => $"游戏拒绝载入限帧组件（错误 {code}）。",
    };

    private static string DescribeException(Exception ex)
    {
        if (ex is Win32Exception native && native.NativeErrorCode is 5 or 740)
            return "中控没有权限访问游戏进程。";
        if (ex is TimeoutException) return ex.Message;
        if (ex is Win32Exception) return "无法安全连接到游戏进程。";
        return ex.GetBaseException().Message;
    }
}
