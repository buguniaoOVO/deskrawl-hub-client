using System.Diagnostics;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
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
internal sealed record AccountLineageReport(string Id, int Port, string Name, string Character, string Label,
    double? Percentile, string[] Samples, string Error, JsonNode? Report);
internal sealed record AuctionItemRow(string Uid, string Key, string Name, string NameEn, string Kind, string Rarity,
    bool Ancient, bool BlackMist, int ItemLevel, string Where, int Page, int Index, int Count);
internal sealed record AuctionAccountInventory(string Id, int Port, string Name, string Character, string Area, string Mode,
    string Error, AuctionItemRow[] Items);
internal sealed record AuctionInventoryResult(bool Supported, string Reason, AuctionAccountInventory[] Accounts);

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
    private readonly ConcurrentDictionary<string, (DateTime Expires, AccountLineageReport Value)> _lineageReportCache = new(StringComparer.OrdinalIgnoreCase);
    private (DateTime At, AuctionInventoryResult Value)? _auctionCache;
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
        AccountLineageReport[] reports = await ReadLineageReportsAsync();
        return reports.Select(report => new AccountLineageSnapshot(report.Id, report.Port, report.Character,
            report.Label, report.Percentile, report.Samples, report.Error)).ToArray();
    }

    internal async Task<AccountLineageReport[]> ReadLineageReportsAsync()
    {
        WorkspaceAccount[] accounts = await RefreshAsync();
        return await Task.WhenAll(accounts.Select(ReadLineageReportAsync));
    }

    private async Task<AccountLineageReport> ReadLineageReportAsync(WorkspaceAccount account)
    {
        if (_lineageReportCache.TryGetValue(account.Id, out var cached) && cached.Expires > DateTime.UtcNow)
            return cached.Value;

        AccountLineageReport result;
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
            JsonNode? report = JsonNode.Parse(await response.Content.ReadAsStringAsync());
            string character = report?["char"]?.GetValue<string>() ?? "";
            if (character.Length == 0)
            {
                result = new AccountLineageReport(account.Id, account.ControlPort, account.Name, "", "等待角色", null, [],
                    report?["error"]?.GetValue<string>() ?? "", report);
            }
            else
            {
                JsonObject? verdict = report?["verdict"] as JsonObject;
                string label = verdict?["label"]?.GetValue<string>() ?? "";
                double? percentile = verdict?["pct"] is JsonValue pctValue && pctValue.TryGetValue<double>(out double pct)
                    ? pct : null;
                var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (verdict?["used"] is JsonArray usedItems)
                    foreach (JsonNode? item in usedItems)
                        if (item is JsonValue value && value.TryGetValue<string>(out string? key) && !string.IsNullOrEmpty(key))
                            used.Add(key);

                var samples = new List<string>();
                if (report?["families"] is JsonObject families)
                foreach (KeyValuePair<string, JsonNode?> family in families)
                {
                    if (!used.Contains(family.Key) || family.Value is not JsonObject detail) continue;
                    int n = Int(detail["n"]);
                    if (n > 0) samples.Add($"{FamilyName(family.Key)} {n}");
                }

                result = new AccountLineageReport(account.Id, account.ControlPort, account.Name, character,
                    label.Length > 0 ? label : "样本不足", percentile, samples.ToArray(), "", report);
            }
        }
        catch (Exception ex)
        {
            string message = ex is HttpRequestException or TaskCanceledException ? "读取超时" : ex.Message;
            result = new AccountLineageReport(account.Id, account.ControlPort, account.Name, "", "暂不可用", null, [], message, null);
        }

        _lineageReportCache[account.Id] = (DateTime.UtcNow.AddSeconds(45), result);
        return result;
    }

    internal async Task<AuctionInventoryResult> ReadAuctionInventoryAsync()
    {
        if (_auctionCache is { } cached && DateTime.UtcNow - cached.At < TimeSpan.FromSeconds(20))
            return cached.Value;
        WorkspaceAccount[] accounts = await RefreshAsync();
        var rows = new List<AuctionAccountInventory>();
        bool supported = false;
        string reason = "助手未提供拍卖行接口";
        foreach (WorkspaceAccount account in accounts)
        {
            AuctionAccountInventory row = await ReadAuctionAccountAsync(account, feature =>
            {
                if (feature) supported = true;
            });
            rows.Add(row);
        }
        if (!supported)
            reason = "当前 X1X 助手没有拍卖行操作接口，客户端无法代替游戏完成上架。";
        var result = new AuctionInventoryResult(supported, reason, rows.ToArray());
        _auctionCache = (DateTime.UtcNow, result);
        return result;
    }

    private async Task<AuctionAccountInventory> ReadAuctionAccountAsync(WorkspaceAccount account, Action<bool> onFeature)
    {
        try
        {
            if (!account.HelperRunning || account.ControlPort == 0)
                throw new InvalidOperationException("助手未运行");
            HelperConnection connection = await ConnectAsync(account.Id);
            if (!IsConnectionOwner(connection, connection.Port))
                throw new InvalidOperationException("助手端口已变化，请刷新环境状态");

            using var statusRequest = new HttpRequestMessage(HttpMethod.Get,
                $"http://127.0.0.1:{connection.Port}/control/status");
            statusRequest.Headers.Add("X-Deskrawl-Token", connection.Token);
            statusRequest.Headers.Add("Origin", "https://x1x.tw");
            using HttpResponseMessage statusResponse = await _http.SendAsync(statusRequest);
            statusResponse.EnsureSuccessStatusCode();
            JsonNode? status = JsonNode.Parse(await statusResponse.Content.ReadAsStringAsync());
            if (status?["features"] is JsonArray features)
                onFeature(features.Any(node => string.Equals(node?.GetValue<string>(), "auction", StringComparison.OrdinalIgnoreCase)));
            int apiPort = Int(status?["api"]?["port"]);
            if (apiPort == 0) apiPort = connection.Port - 1;

            using var stateRequest = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{apiPort}/v1/state");
            stateRequest.Headers.Add("X-Deskrawl-Token", connection.Token);
            stateRequest.Headers.Add("Origin", "https://x1x.tw");
            using HttpResponseMessage stateResponse = await _http.SendAsync(stateRequest);
            stateResponse.EnsureSuccessStatusCode();
            JsonNode? state = JsonNode.Parse(await stateResponse.Content.ReadAsStringAsync());
            JsonNode? character = state?["character"];
            var itemNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var namesRequest = new HttpRequestMessage(HttpMethod.Get,
                    $"http://127.0.0.1:{connection.Port}/control/gear_names");
                namesRequest.Headers.Add("X-Deskrawl-Token", connection.Token);
                namesRequest.Headers.Add("Origin", "https://x1x.tw");
                using HttpResponseMessage namesResponse = await _http.SendAsync(namesRequest);
                if (namesResponse.IsSuccessStatusCode)
                {
                    JsonNode? catalog = JsonNode.Parse(await namesResponse.Content.ReadAsStringAsync());
                    if (catalog?["items"] is JsonObject entries)
                        foreach (KeyValuePair<string, JsonNode?> entry in entries)
                        {
                            if (entry.Value?["names"] is not JsonObject names) continue;
                            string localized = Text(names["zh-CN"]);
                            if (localized.Length == 0) localized = Text(names["zh-TW"]);
                            if (localized.Length > 0) itemNames[entry.Key] = localized;
                        }
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException) { }
            try
            {
                using var textRequest = new HttpRequestMessage(HttpMethod.Get,
                    $"http://127.0.0.1:{connection.Port}/i18n.json");
                textRequest.Headers.Add("X-Deskrawl-Token", connection.Token);
                textRequest.Headers.Add("Origin", "https://x1x.tw");
                using HttpResponseMessage textResponse = await _http.SendAsync(textRequest);
                if (textResponse.IsSuccessStatusCode)
                {
                    JsonNode? text = JsonNode.Parse(await textResponse.Content.ReadAsStringAsync());
                    JsonObject? zh = text?["zh-CN"] as JsonObject ?? text?["zh-TW"] as JsonObject;
                    if (zh is not null)
                        foreach (KeyValuePair<string, JsonNode?> entry in zh)
                        {
                            if (!entry.Key.StartsWith("Rein_", StringComparison.OrdinalIgnoreCase)) continue;
                            string localized = Simplified(Text(entry.Value));
                            if (localized.Length > 0) itemNames[entry.Key] = localized;
                        }
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException) { }
            var items = new List<AuctionItemRow>();
            if (state?["inventory"]?["items"] is JsonArray bag) AddAuctionItems(items, bag, "背包", 0, itemNames);
            if (state?["storage"]?["pages"] is JsonArray pages)
                foreach (JsonNode? page in pages)
                    if (page?["items"] is JsonArray stored)
                        AddAuctionItems(items, stored, "仓库", Int(page["page"]), itemNames);
            return new AuctionAccountInventory(account.Id, account.ControlPort, account.Name,
                character?["name"]?.GetValue<string>() ?? "", character?["area"]?.GetValue<string>() ?? "",
                character?["mode"]?.GetValue<string>() ?? "", "",
                items.OrderByDescending(item => item.Rarity == "Divine").ThenByDescending(item => item.BlackMist)
                    .ThenByDescending(item => item.Ancient).ThenByDescending(item => item.ItemLevel).ToArray());
        }
        catch (Exception ex)
        {
            string message = ex is HttpRequestException or TaskCanceledException ? "读取超时" : ex.Message;
            return new AuctionAccountInventory(account.Id, account.ControlPort, account.Name, "", "", "", message, []);
        }
    }

    private static void AddAuctionItems(List<AuctionItemRow> rows, JsonArray items, string where, int page,
        Dictionary<string, string> names)
    {
        foreach (JsonNode? item in items)
        {
            if (item is not JsonObject record || !IsAuctionEligible(record)) continue;
            int index = Int(record["location"]?["index"]);
            string key = Text(record["key"]);
            string name = names.TryGetValue(key, out string? localized) ? localized : Text(record["name"]);
            if (name.Length == 0) name = key;
            rows.Add(new AuctionItemRow(
                Text(record["uid"]), key, name, Text(record["name"]),
                Text(record["type"]), Text(record["rarity"]), Bool(record["ancient"]), Bool(record["black_mist"]),
                Int(record["item_level"]), where, page, index, Math.Max(1, Int(record["count"]))));
        }
    }

    private static bool IsAuctionEligible(JsonObject item)
    {
        if (Text(item["uid"]).Length == 0 || Bool(item["locked"])) return false;
        string type = Text(item["type"]);
        if (type is not ("Equipment" or "Consumable" or "Rune")) return false;
        if (Int(item["upgrade_level"]) > 0 || Int(item["sockets_used"]) > 0) return false;
        if (item["flags"] is JsonArray flags
            && flags.Any(node => string.Equals(node?.GetValue<string>(), "UNMINTED", StringComparison.OrdinalIgnoreCase)))
            return false;
        if (item["minted"] is JsonValue minted && minted.TryGetValue<bool>(out bool isMinted) && !isMinted) return false;
        string rarity = Text(item["rarity"]);
        string key = Text(item["key"]);
        // The game's Auction House takes Divine and Rune Set gear, Black Mist pieces, legendary runes
        // and reins only - an ordinary Legendary piece cannot be listed.
        return Bool(item["black_mist"])
            || rarity is "Divine" or "RuneSet"
            || (type == "Rune" && rarity == "Legendary")
            || (type == "Consumable" && rarity == "Legendary" && key.StartsWith("Rein_", StringComparison.OrdinalIgnoreCase));
    }

    private static string Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out string? text) ? text ?? "" : "";

    private const uint LcMapSimplifiedChinese = 0x02000000;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int LCMapStringW(int locale, uint flags, string source, int sourceLength,
        [Out] char[] destination, int destinationLength);

    private static string Simplified(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        try
        {
            var buffer = new char[text.Length];
            int length = LCMapStringW(0x0804, LcMapSimplifiedChinese, text, text.Length, buffer, buffer.Length);
            return length > 0 ? new string(buffer, 0, Math.Min(length, buffer.Length)) : text;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return text;
        }
    }

    private static int Int(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<int>(out int number) ? number : 0;

    private static bool Bool(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<bool>(out bool flag) && flag;

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
