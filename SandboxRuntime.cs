using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace DeskrawlHubClient;

// Sandbox identity comes from the driver, independently of helper ports.
internal sealed class SandboxRuntime
{
    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    private delegate int EnumBoxes(int index, StringBuilder name);
    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    private delegate int QueryProcess(IntPtr pid, StringBuilder box, IntPtr image, IntPtr sid, IntPtr session);
    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    private delegate int QueryBoxPath(string box, StringBuilder file, StringBuilder key, StringBuilder ipc,
        ref uint fileBytes, ref uint keyBytes, ref uint ipcBytes);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern uint QueryDosDevice(string device, StringBuilder target, int length);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref int size);
    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
    [DllImport("iphlpapi.dll")]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int family, int tableClass, uint reserved);

    private readonly EnumBoxes? _enumBoxes;
    private readonly QueryProcess? _queryProcess;
    private readonly QueryBoxPath? _queryBoxPath;
    private readonly int _outsideStatus;
    internal string StartPath { get; } = "";
    internal bool Available => _queryProcess is not null;

    internal SandboxRuntime(string configuredDirectory)
    {
        var candidates = new List<string> { configuredDirectory };
        using (var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\SbieSvc"))
        {
            string command = Environment.ExpandEnvironmentVariables(key?.GetValue("ImagePath") as string ?? "");
            var match = System.Text.RegularExpressions.Regex.Match(command, "^\"?(.+?\\.exe)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (match.Success) candidates.Add(Path.GetDirectoryName(match.Groups[1].Value.Replace(@"\??\", "")) ?? "");
        }
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Sandboxie-Plus"));
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Sandboxie"));
        foreach (string candidate in candidates.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string directory = Path.GetFullPath(candidate);
            if (!File.Exists(Path.Combine(directory, "Start.exe"))) continue;
            StartPath = Path.Combine(directory, "Start.exe");
            try
            {
                // Keep the module loaded while its delegates are in use.
                IntPtr module = NativeLibrary.Load(Path.Combine(directory, "SbieDll.dll"));
                _enumBoxes = Marshal.GetDelegateForFunctionPointer<EnumBoxes>(NativeLibrary.GetExport(module, "SbieApi_EnumBoxes"));
                _queryProcess = Marshal.GetDelegateForFunctionPointer<QueryProcess>(NativeLibrary.GetExport(module, "SbieApi_QueryProcess"));
                _queryBoxPath = Marshal.GetDelegateForFunctionPointer<QueryBoxPath>(NativeLibrary.GetExport(module, "SbieApi_QueryBoxPath"));
                _outsideStatus = _queryProcess((IntPtr)Environment.ProcessId, new StringBuilder(34), IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                if (_outsideStatus == 0)
                    throw new InvalidOperationException("请在主机上运行中控，以管理主机及各个沙箱。");
                break;
            }
            catch (DllNotFoundException) { }
            catch (BadImageFormatException) { }
        }
    }

    internal string[] Boxes()
    {
        if (_enumBoxes is null) return [];
        var boxes = new List<string>();
        int index = -1;
        for (int count = 0; count < 512; count++)
        {
            var name = new StringBuilder(34);
            index = _enumBoxes(index, name);
            if (index < 0) break;
            if (name.Length > 0) boxes.Add(name.ToString());
        }
        return boxes.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    internal string? BoxOf(int pid)
    {
        if (_queryProcess is null) return StartPath.Length == 0 ? "" : null;
        var name = new StringBuilder(34);
        int result = _queryProcess((IntPtr)pid, name, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (result == 0) return name.Length > 0 ? name.ToString() : null;
        return result == _outsideStatus ? "" : null;
    }

    internal string Root(string box)
    {
        if (box.Length == 0 || _queryBoxPath is null) return "";
        var path = new StringBuilder(16384);
        var key = new StringBuilder(16384);
        var ipc = new StringBuilder(16384);
        uint bytes = 32768, keyBytes = 32768, ipcBytes = 32768;
        if (_queryBoxPath(box, path, key, ipc, ref bytes, ref keyBytes, ref ipcBytes) != 0) return "";
        string raw = path.ToString();
        if (raw.StartsWith(@"\??\", StringComparison.Ordinal)) return raw[4..];
        foreach (string drive in Environment.GetLogicalDrives())
        {
            var target = new StringBuilder(1024);
            if (QueryDosDevice(drive[..2], target, target.Capacity) != 0
                && raw.StartsWith(target + @"\", StringComparison.OrdinalIgnoreCase))
                return drive[..2] + raw[target.Length..];
        }
        return Path.IsPathFullyQualified(raw) && !raw.StartsWith(@"\Device\", StringComparison.OrdinalIgnoreCase) ? raw : "";
    }

    internal string LogicalPath(string path, string box)
    {
        string root = Root(box);
        if (root.Length == 0) return Path.GetFullPath(path);
        string relative = Path.GetRelativePath(root, path);
        if (relative.StartsWith(@"drive\", StringComparison.OrdinalIgnoreCase) && relative.Length > 8)
            return relative[6] + @":\" + relative[8..];
        if (relative.StartsWith(@"user\current\", StringComparison.OrdinalIgnoreCase))
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), relative[13..]);
        return Path.GetFullPath(path);
    }

    internal IEnumerable<string> FileCandidates(string logical, string box)
    {
        logical = Path.GetFullPath(logical);
        string root = Root(box);
        if (root.Length > 0)
        {
            string user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('\\') + "\\";
            if (logical.StartsWith(user, StringComparison.OrdinalIgnoreCase))
                yield return Path.Combine(root, @"user\current", logical[user.Length..]);
            if (logical.Length > 3 && logical[1] == ':')
                yield return Path.Combine(root, "drive", logical[..1], logical[3..]);
        }
        yield return logical;
    }

    internal string ResolveFile(string logical, string box) => FileCandidates(logical, box).FirstOrDefault(File.Exists) ?? logical;

    internal static Dictionary<int, int> Listeners()
    {
        var result = new Dictionary<int, int>();
        int size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, false, 2, 3, 0);
        if (size < 4) return result;
        IntPtr memory = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(memory, ref size, false, 2, 3, 0) != 0) return result;
            int count = Marshal.ReadInt32(memory);
            for (int i = 0; i < count && 4 + (i + 1) * 24 <= size; i++)
            {
                IntPtr row = memory + 4 + i * 24;
                int port = (ushort)IPAddress.NetworkToHostOrder((short)Marshal.ReadInt32(row, 8));
                result[port] = Marshal.ReadInt32(row, 20);
            }
        }
        finally { Marshal.FreeHGlobal(memory); }
        return result;
    }

    internal static string ProcessPath(int pid)
    {
        IntPtr handle = OpenProcess(0x1000, false, pid);
        if (handle == IntPtr.Zero) return "";
        try
        {
            var path = new StringBuilder(32768);
            int length = path.Capacity;
            return QueryFullProcessImageName(handle, 0, path, ref length) ? path.ToString() : "";
        }
        finally { CloseHandle(handle); }
    }

    internal Process Start(string box, string program, IEnumerable<string> arguments, bool hidden)
    {
        if (box.Length > 0 && !Boxes().Contains(box, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("目标沙箱不存在或已停用，请刷新账号列表。");
        if (box.Length > 0 && !Available)
            throw new InvalidOperationException("无法读取 Sandboxie，请检查运行设置中的安装目录。");
        if (StartPath.Length > 0 && !Available)
            throw new InvalidOperationException("无法确认 Sandboxie 进程归属，已停止启动请求。请检查 Sandboxie 安装。");
        var info = new ProcessStartInfo
        {
            FileName = StartPath.Length > 0 ? StartPath : program,
            WorkingDirectory = Directory.Exists(Path.GetDirectoryName(program)) ? Path.GetDirectoryName(program)! : Environment.CurrentDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = hidden ? ProcessWindowStyle.Hidden : ProcessWindowStyle.Normal,
        };
        if (StartPath.Length > 0)
        {
            info.ArgumentList.Add(box.Length > 0 ? "/box:" + box : "/dfp");
            if (hidden) info.ArgumentList.Add("/hide_window");
            info.ArgumentList.Add(program);
        }
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        return Process.Start(info) ?? throw new InvalidOperationException("启动请求未能提交。");
    }
}
