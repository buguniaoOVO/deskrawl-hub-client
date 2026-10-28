using System.Diagnostics;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace DeskrawlHubClient;

internal sealed record AccountProfile
{
    public string Sandbox { get; init; } = "";
    public string HelperDirectory { get; set; } = "";
    public string PairingFingerprint { get; set; } = "";
    public string GameExecutable { get; set; } = "";
    public string GameLaunchMode { get; set; } = "steam";
}

internal sealed record WorkspaceAccount(string Id, string Name, string Sandbox, string HelperDirectory,
    int ControlPort, bool HelperRunning, int[] GamePids, string GameExecutable, string GameLaunchMode, string Notice);
internal sealed record HelperConnection(string Sandbox, int Port, string Token, string Directory);
internal sealed record AccountLineageSnapshot(string Id, int Port, string Character, string Label,
    double? Percentile, string[] Samples, string Error);

internal sealed class AccountWorkspace : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _path;
    private readonly string _engineConfigPath;
    private readonly Uri _engine;
    private readonly HttpClient _http = new(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromSeconds(12) };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<AccountProfile> _profiles;
    private readonly Dictionary<string, DateTime> _launches = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (DateTime Expires, AccountLineageSnapshot Value)> _lineageCache = new(StringComparer.OrdinalIgnoreCase);
    private SandboxRuntime? _sandbox;
    private WorkspaceAccount[] _accounts = [];
    private JsonArray _installs = [];
    private DateTime _lastInstalls;
    private string _steam = "";
    private string _game = "";
    private string _appId = "";

    internal AccountWorkspace(string dataRoot, string engineConfigPath, int port)
    {
        _path = Path.Combine(dataRoot, "account-workspace.json");
        _engineConfigPath = engineConfigPath;
        _engine = new Uri($"http://127.0.0.1:{port}/");
        _profiles = File.Exists(_path)
            ? JsonSerializer.Deserialize<List<AccountProfile>>(File.ReadAllText(_path), JsonOptions) ?? []
            : [];
    }

    internal static string Id(string box) => box.Length == 0 ? "host" : "box:" + box;
    private static string Name(string box) => box.Length == 0 ? "主机" : "沙箱 " + box;
    private AccountProfile Profile(string id) => _profiles.FirstOrDefault(p => Id(p.Sandbox).Equals(id, StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidOperationException("账号环境已变更，请刷新后重试。");

    internal async Task<WorkspaceAccount[]> RefreshAsync(bool force = false)
    {
        await _gate.WaitAsync();
        try { return await RefreshCoreAsync(force); }
        finally { _gate.Release(); }
    }

    private async Task<WorkspaceAccount[]> RefreshCoreAsync(bool force)
    {
        JsonObject config = ReadJson(_engineConfigPath) as JsonObject ?? new();
        _sandbox ??= new SandboxRuntime(config["sandboxie_dir"]?.GetValue<string>() ?? "");
        if (force || DateTime.UtcNow - _lastInstalls > TimeSpan.FromMinutes(1))
        {
            try
            {
                var data = JsonNode.Parse(await _http.GetStringAsync(new Uri(_engine, "api/config")));
                _installs = data?["installs"]?.DeepClone() as JsonArray ?? [];
                _lastInstalls = DateTime.UtcNow;
            }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) { }
        }
        return await Task.Run(() => Discover(config));
    }

    private WorkspaceAccount[] Discover(JsonObject config)
    {
        SandboxRuntime sandbox = _sandbox!;
        string[] boxes = new[] { "" }.Concat(sandbox.Boxes()).ToArray();
        Dictionary<int, int> listeners = SandboxRuntime.Listeners();
        var helperProcesses = new List<(string Box, string Directory, int Pid)>();
        var games = new List<(string Box, string Path, int Pid)>();
        foreach (string processName in new[] { "pythonw", "python", "Deskrawl" })
        foreach (Process process in Process.GetProcessesByName(processName))
        using (process)
        {
            try
            {
                string? box = sandbox.BoxOf(process.Id);
                if (box is null) continue;
                string path = SandboxRuntime.ProcessPath(process.Id);
                if (processName == "Deskrawl") games.Add((box, path, process.Id));
                else
                {
                    if (path.Length == 0) continue;
                    string root = Path.GetDirectoryName(Path.GetDirectoryName(path)) ?? "";
                    if (IsHelper(root, box)) helperProcesses.Add((box, root, process.Id));
                }
            }
            catch (System.ComponentModel.Win32Exception) { }
            catch (InvalidOperationException) { }
        }
        FindSteamAndGame(games.Select(g => g.Path).FirstOrDefault(p => p.Length > 0));
        var accounts = new List<WorkspaceAccount>();
        foreach (string box in boxes)
        {
            var profile = _profiles.FirstOrDefault(p => p.Sandbox.Equals(box, StringComparison.OrdinalIgnoreCase));
            if (profile is null) { profile = new AccountProfile { Sandbox = box }; _profiles.Add(profile); }
            var active = helperProcesses.Where(p => p.Box.Equals(box, StringComparison.OrdinalIgnoreCase)).ToArray();
            var activeRoots = active.Select(p => p.Directory).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            string notice = "";
            if (activeRoots.Length == 1)
            {
                profile.HelperDirectory = sandbox.LogicalPath(activeRoots[0], box);
                string fingerprint = Fingerprint(profile.HelperDirectory, box);
                if (fingerprint.Length > 0) profile.PairingFingerprint = fingerprint;
            }
            else if (activeRoots.Length > 1)
                notice = "此环境运行了多份助手，请先在各自的助手页面确认使用哪一份。";
            else if (profile.HelperDirectory.Length > 0)
            {
                // Updates are followed within the same parent and only with the same pairing identity.
                string parent = Path.GetDirectoryName(profile.HelperDirectory) ?? "";
                string? updated = HelperChildren(parent, box)
                    .Where(p => profile.PairingFingerprint.Length > 0 && Fingerprint(p, box) == profile.PairingFingerprint)
                    .OrderByDescending(HelperVersion).FirstOrDefault();
                if (updated is not null) profile.HelperDirectory = updated;
            }
            if (profile.HelperDirectory.Length == 0)
            {
                var candidates = _installs.OfType<JsonObject>()
                    .Where(i => (i["sandbox"]?.GetValue<string>() ?? "").Equals(box, StringComparison.OrdinalIgnoreCase))
                    .Select(i => i["dir"]?.GetValue<string>() ?? "")
                    .Where(p => p.Length > 0)
                    .Select(p => sandbox.LogicalPath(p, box))
                    .Where(p => IsHelper(p, box)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                // Prefer the explicit host/box subfolder beside a known helper installation.
                var roots = config["scan_roots"] as JsonArray ?? [];
                var parents = roots.Select(r => r?.GetValue<string>() ?? "")
                    .Where(Directory.Exists).Select(p => Path.GetDirectoryName(p) ?? "")
                    .Concat(helperProcesses.Select(p => Path.GetDirectoryName(Path.GetDirectoryName(p.Directory)) ?? ""))
                    .Distinct(StringComparer.OrdinalIgnoreCase);
                var named = parents.SelectMany(p => HelperChildren(Path.Combine(p, box.Length == 0 ? "0" : box), box))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                string[] choices = named.Length > 0 ? named : candidates;
                if (choices.Select(p => Path.GetDirectoryName(p)).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1
                    && choices.Select(p => Fingerprint(p, box)).Distinct(StringComparer.Ordinal).Count() == 1)
                {
                    profile.HelperDirectory = choices.OrderByDescending(HelperVersion).First();
                    profile.PairingFingerprint = Fingerprint(profile.HelperDirectory, box);
                }
                else if (choices.Length > 0) notice = "发现多个助手目录或配对信息，请点击「环境设置」选择对应副本。";
            }
            if (profile.GameExecutable.Length == 0 && _game.Length > 0) profile.GameExecutable = _game;
            int[] ports = listeners.Where(l => IsControlPort(l.Key) && active.Any(p => p.Pid == l.Value))
                .Select(l => l.Key).ToArray();
            int port = ports.Length == 1 ? ports[0] : 0;
            if (port == 0 && activeRoots.Length <= 1 && profile.HelperDirectory.Length > 0)
            {
                JsonNode? saved = ReadJson(sandbox.ResolveFile(Path.Combine(profile.HelperDirectory, @"core\runtime\ports.json"), box));
                int value = saved?["control"]?.GetValue<int>() ?? 0;
                if (IsControlPort(value)) port = value;
            }
            if (profile.HelperDirectory.Length == 0 && notice.Length == 0) notice = "请选择此环境使用的 X1X 助手目录。";
            accounts.Add(new WorkspaceAccount(Id(box), Name(box), box, profile.HelperDirectory, port,
                active.Length > 0, games.Where(g => g.Box.Equals(box, StringComparison.OrdinalIgnoreCase)).Select(g => g.Pid).ToArray(),
                profile.GameExecutable, profile.GameLaunchMode, notice));
        }
        Save();
        return _accounts = accounts.OrderBy(a => a.Sandbox.Length > 0)
            .ThenBy(a => a.Sandbox, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static bool IsControlPort(int port) => port >= 8766 && port <= 8996 && (port - 8766) % 10 == 0;
    private bool IsHelper(string root, string box) => File.Exists(_sandbox!.ResolveFile(Path.Combine(root, @"core\app\deskrawl_helper.py"), box))
        && File.Exists(_sandbox.ResolveFile(Path.Combine(root, @"runtime\pythonw.exe"), box));
    private IEnumerable<string> HelperChildren(string parent, string box)
    {
        if (parent.Length == 0) yield break;
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in _sandbox!.FileCandidates(parent, box).Where(Directory.Exists))
        {
            string[] directories;
            try { directories = Directory.GetDirectories(path, "X1X-Deskrawl-Helper-*"); }
            catch (UnauthorizedAccessException) { continue; }
            foreach (string dir in directories)
            {
                string logical = _sandbox.LogicalPath(dir, box);
                if (found.Add(logical) && IsHelper(logical, box)) yield return logical;
            }
        }
    }
    private static Version HelperVersion(string path)
    {
        var match = Regex.Match(Path.GetFileName(path), @"Helper-(\d+\.\d+(?:\.\d+)?)", RegexOptions.IgnoreCase);
        return Version.TryParse(match.Groups[1].Value, out var version) ? version : new Version(0, 0);
    }
    private string Token(string root, string box)
    {
        string path = _sandbox!.ResolveFile(Path.Combine(root, @"core\helper\runtime\token"), box);
        string token = File.Exists(path) ? File.ReadAllText(path).Trim() : "";
        return Regex.IsMatch(token, "^[0-9a-f]{64}$") ? token : "";
    }
    private string Fingerprint(string root, string box)
    {
        string token = Token(root, box);
        return token.Length > 0 ? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))) : "";
    }

    internal async Task SaveProfileAsync(string id, string helper, string game, string mode)
    {
        await _gate.WaitAsync();
        try
        {
            await RefreshCoreAsync(false);
            var profile = Profile(id);
            helper = helper.Trim().Trim('"');
            game = game.Trim().Trim('"');
            if (helper.Length > 0)
            {
                if (!Path.IsPathFullyQualified(helper)) throw new InvalidOperationException("助手目录需要填写完整路径。");
                helper = _sandbox!.LogicalPath(helper, profile.Sandbox);
                if (!IsHelper(helper, profile.Sandbox)) throw new InvalidOperationException("所选目录需要包含 runtime\\pythonw.exe 和 core\\app\\deskrawl_helper.py。");
                if (_accounts.First(a => a.Id == id).HelperRunning
                    && !helper.Equals(profile.HelperDirectory, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("此环境的助手正在运行，请先在助手页面退出后再更换目录。");
            }
            if (game.Length > 0 && (!Path.IsPathFullyQualified(game)
                || !Path.GetFileName(game).Equals("Deskrawl.exe", StringComparison.OrdinalIgnoreCase)
                || !File.Exists(_sandbox!.ResolveFile(game, profile.Sandbox))))
                throw new InvalidOperationException("请选择有效的 Deskrawl.exe。");
            if (mode is not ("steam" or "direct")) throw new InvalidOperationException("请选择游戏启动方式。");
            profile.HelperDirectory = helper;
            profile.PairingFingerprint = helper.Length > 0 ? Fingerprint(helper, profile.Sandbox) : "";
            profile.GameExecutable = game;
            profile.GameLaunchMode = mode;
            Save();
        }
        finally { _gate.Release(); }
    }

    internal async Task<string> LaunchAsync(string id, bool helper)
    {
        await _gate.WaitAsync();
        try
        {
            await RefreshCoreAsync(false);
            var profile = Profile(id);
            var account = _accounts.First(a => a.Id == id);
            string key = id + (helper ? ":helper" : ":game");
            if (_launches.TryGetValue(key, out DateTime last) && DateTime.UtcNow - last < TimeSpan.FromSeconds(20))
                return "启动请求已提交，正在等待进程出现。";
            if (helper && account.HelperRunning) return "此环境的助手已经运行，请打开「助手设置」。";
            if (!helper && account.GamePids.Length > 0) return "此环境的游戏已经运行。";
            if (helper)
            {
                if (account.Notice.Length > 0 || !IsHelper(profile.HelperDirectory, profile.Sandbox))
                    throw new InvalidOperationException(account.Notice.Length > 0 ? account.Notice : "请先选择此环境的助手目录。");
                using var started = _sandbox!.Start(profile.Sandbox, Path.Combine(profile.HelperDirectory, @"runtime\pythonw.exe"),
                    [Path.Combine(profile.HelperDirectory, @"core\app\deskrawl_helper.py"), "--open", "none"], true);
            }
            else if (profile.GameLaunchMode == "direct")
            {
                if (profile.GameExecutable.Length == 0 || !File.Exists(_sandbox!.ResolveFile(profile.GameExecutable, profile.Sandbox)))
                    throw new InvalidOperationException("请先在环境设置中选择 Deskrawl.exe。");
                using var started = _sandbox.Start(profile.Sandbox, profile.GameExecutable, [], false);
            }
            else
            {
                if (_steam.Length == 0 || _appId.Length == 0)
                    throw new InvalidOperationException("未找到 Steam 游戏安装记录，请在环境设置中选择 Deskrawl.exe，或改用直接启动。");
                using var started = _sandbox!.Start(profile.Sandbox, _steam, ["-applaunch", _appId], false);
            }
            _launches[key] = DateTime.UtcNow;
            return Name(profile.Sandbox) + (helper ? "助手" : "游戏") + "启动请求已提交，等待进程确认。";
        }
        finally { _gate.Release(); }
    }

    internal async Task<HelperConnection> ConnectAsync(string id)
    {
        await _gate.WaitAsync();
        try
        {
            await RefreshCoreAsync(false);
            var profile = Profile(id);
            var account = _accounts.First(a => a.Id == id);
            string token = Token(profile.HelperDirectory, profile.Sandbox);
            if (account.ControlPort == 0 || !account.HelperRunning)
                throw new InvalidOperationException("此环境的助手尚未启动，请先点击「启动助手」。");
            if (token.Length == 0) throw new InvalidOperationException("无法读取该助手的配对信息，请检查助手目录。");
            var connection = new HelperConnection(profile.Sandbox, account.ControlPort, token, profile.HelperDirectory);
            if (!IsConnectionOwner(connection))
                throw new InvalidOperationException("控制端口属于其他环境，已停止连接，请刷新后重试。");
            using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{connection.Port}/control/status");
            request.Headers.Add("X-Deskrawl-Token", token);
            using var response = await _http.SendAsync(request);
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                throw new InvalidOperationException("配对信息与运行中的助手不符，请在环境设置中选择该助手实际使用的目录。");
            response.EnsureSuccessStatusCode();
            JsonNode? status = JsonNode.Parse(await response.Content.ReadAsStringAsync());
            string? actual = status?["instance"]?["sandbox"]?.GetValue<string>();
            if (actual is null || !actual.Equals(profile.Sandbox, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("助手返回的沙箱与当前账号环境不符，已停止连接。");
            string directory = status?["instance"]?["dir"]?.GetValue<string>() ?? "";
            if (directory.Length > 0 && !_sandbox!.LogicalPath(directory, profile.Sandbox)
                .Equals(profile.HelperDirectory, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("助手返回的目录与当前环境配置不符，请重新选择该助手目录。");
            return connection;
        }
        finally { _gate.Release(); }
    }

    internal async Task<AccountLineageSnapshot[]> ReadLineageSummariesAsync()
    {
        WorkspaceAccount[] accounts = await RefreshAsync();
        return await Task.WhenAll(accounts.Select(ReadLineageSummaryAsync));
    }

    private async Task<AccountLineageSnapshot> ReadLineageSummaryAsync(WorkspaceAccount account)
    {
        if (_lineageCache.TryGetValue(account.Id, out var cached) && cached.Expires > DateTime.UtcNow)
            return cached.Value;

        AccountLineageSnapshot result;
        try
        {
            if (!account.HelperRunning || account.ControlPort == 0)
                throw new InvalidOperationException("助手未运行");

            HelperConnection connection = await ConnectAsync(account.Id);
            if (!IsConnectionOwner(connection, connection.Port))
                throw new InvalidOperationException("助手端口已变化，请刷新环境状态");

            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"http://127.0.0.1:{connection.Port}/control/lineage");
            request.Headers.Add("X-Deskrawl-Token", connection.Token);
            request.Headers.Add("Origin", "https://x1x.tw");
            using HttpResponseMessage response = await _http.SendAsync(request);
            response.EnsureSuccessStatusCode();
            using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
            JsonElement root = document.RootElement;
            string character = ReadString(root, "char");
            if (character.Length == 0)
            {
                result = new AccountLineageSnapshot(account.Id, account.ControlPort, "", "等待角色", null, [],
                    ReadString(root, "error"));
            }
            else
            {
                JsonElement verdict = root.TryGetProperty("verdict", out JsonElement v) && v.ValueKind == JsonValueKind.Object
                    ? v : default;
                string label = verdict.ValueKind == JsonValueKind.Object ? ReadString(verdict, "label") : "";
                double? percentile = verdict.ValueKind == JsonValueKind.Object
                    && verdict.TryGetProperty("pct", out JsonElement pct) && pct.TryGetDouble(out double value)
                    ? value : null;
                var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (verdict.ValueKind == JsonValueKind.Object && verdict.TryGetProperty("used", out JsonElement usedItems)
                    && usedItems.ValueKind == JsonValueKind.Array)
                    foreach (JsonElement item in usedItems.EnumerateArray())
                        if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } key)
                            used.Add(key);

                var samples = new List<string>();
                if (root.TryGetProperty("families", out JsonElement families) && families.ValueKind == JsonValueKind.Object)
                foreach (JsonProperty family in families.EnumerateObject())
                {
                    if (!used.Contains(family.Name) || family.Value.ValueKind != JsonValueKind.Object
                        || !family.Value.TryGetProperty("n", out JsonElement count) || !count.TryGetInt32(out int n) || n <= 0)
                        continue;
                    samples.Add($"{FamilyName(family.Name)} {n}");
                }

                result = new AccountLineageSnapshot(account.Id, account.ControlPort, character,
                    label.Length > 0 ? label : "样本不足", percentile, samples.ToArray(), "");
            }
        }
        catch (Exception ex)
        {
            string message = ex is HttpRequestException or TaskCanceledException
                ? "读取超时"
                : ex.Message;
            result = new AccountLineageSnapshot(account.Id, account.ControlPort, "", "暂不可用", null, [], message);
        }

        _lineageCache[account.Id] = (DateTime.UtcNow.AddSeconds(45), result);
        return result;
    }

    private static string FamilyName(string key) => key switch
    {
        "gamble" => "赌博",
        "ancient" => "远古",
        "black_mist" => "黑雾",
        _ => key,
    };

    private static string ReadString(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out JsonElement item)
        && item.ValueKind == JsonValueKind.String ? item.GetString() ?? "" : "";

    internal bool IsConnectionOwner(HelperConnection connection, int? requestedPort = null)
    {
        var ports = SandboxRuntime.Listeners();
        return ports.TryGetValue(connection.Port, out int pid)
            && OwnsHelperProcess(connection, pid)
            && (requestedPort is null || requestedPort == connection.Port
                || (requestedPort == connection.Port - 1 && ports.TryGetValue(requestedPort.Value, out int apiPid)
                    && OwnsHelperProcess(connection, apiPid)));
    }

    private bool OwnsHelperProcess(HelperConnection connection, int pid)
    {
        if (!string.Equals(_sandbox?.BoxOf(pid), connection.Sandbox, StringComparison.OrdinalIgnoreCase)) return false;
        string processPath = SandboxRuntime.ProcessPath(pid);
        if (processPath.Length == 0) return false;
        string directory = Path.GetDirectoryName(Path.GetDirectoryName(processPath)) ?? "";
        return _sandbox!.LogicalPath(directory, connection.Sandbox).Equals(connection.Directory, StringComparison.OrdinalIgnoreCase);
    }

    private void FindSteamAndGame(string? runningGame)
    {
        if (_game.Length == 0 && runningGame is not null) _game = runningGame;
        if (_steam.Length == 0)
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            string root = key?.GetValue("SteamPath") as string ?? "";
            string exe = Path.Combine(root, "steam.exe");
            if (File.Exists(exe)) _steam = exe;
        }
        if (_appId.Length > 0) return;
        var libraries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (_steam.Length > 0)
        {
            string steamRoot = Path.GetDirectoryName(_steam)!;
            libraries.Add(steamRoot);
            string file = Path.Combine(steamRoot, @"steamapps\libraryfolders.vdf");
            if (File.Exists(file))
            foreach (Match match in Regex.Matches(File.ReadAllText(file), "\"path\"\\s+\"([^\"]+)\""))
                libraries.Add(match.Groups[1].Value.Replace(@"\\", @"\"));
        }
        if (_game.Length > 0)
        {
            string? steamapps = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(_game)));
            if (steamapps is not null) libraries.Add(Path.GetDirectoryName(steamapps) ?? "");
        }
        foreach (string library in libraries)
        {
            string apps = Path.Combine(library, "steamapps");
            if (!Directory.Exists(apps)) continue;
            foreach (string manifest in Directory.EnumerateFiles(apps, "appmanifest_*.acf"))
            {
                string text = File.ReadAllText(manifest);
                string dir = Regex.Match(text, "\"installdir\"\\s+\"([^\"]+)\"").Groups[1].Value;
                if (!dir.Equals("Deskrawl", StringComparison.OrdinalIgnoreCase)) continue;
                string exe = Path.Combine(apps, "common", dir, "Deskrawl.exe");
                if (!File.Exists(exe)) continue;
                _game = exe;
                _appId = Regex.Match(text, "\"appid\"\\s+\"(\\d+)\"").Groups[1].Value;
                return;
            }
        }
    }

    private static JsonNode? ReadJson(string path)
    {
        try { return File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) : null; }
        catch (JsonException) { return null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
    private void Save()
    {
        string json = JsonSerializer.Serialize(_profiles, JsonOptions);
        if (File.Exists(_path) && File.ReadAllText(_path) == json) return;
        File.WriteAllText(_path + ".writing", json, new UTF8Encoding(false));
        File.Move(_path + ".writing", _path, true);
    }
    public void Dispose() => _http.Dispose();
}
