using System.Diagnostics;
using System.Drawing;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace DeskrawlHubClient;

internal sealed class MainForm : Form
{
    private const int DefaultPort = 8899;
    private const string WikiEquipmentUrl = "https://x1x.tw/deskrawl/zh-CN/items/?kind=equipment";
    private static readonly JsonSerializerOptions ClientSettingsJsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly HttpClient WikiHttp = CreateWikiHttpClient();
    private static readonly Regex WikiEquipmentRowRegex = new(
        @"<li\b(?=[^>]*\bdata-kind\s*=\s*[""']equipment[""'])[^>]*>.*?</li>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Color Canvas = Color.White;

    // 更新检测在界面上不再要求用户填仓库地址：默认内置官方仓库，配置文件里的空值也回落到它。
    private const string DefaultUpdateRepositoryUrl = "https://github.com/buguniaoOVO/deskrawl-hub-client";

    private sealed record ClientUpdateSettings
    {
        public string RepositoryUrl { get; init; } = DefaultUpdateRepositoryUrl;
        public bool Enabled { get; init; } = true;
        public string IgnoredVersion { get; init; } = "";
        public long LastCheckedUtc { get; init; }
    }

    private sealed record WikiEquipmentEntry(string Id, string Name, string NameEn, string Rarity, string QualityName);
    private readonly SemaphoreSlim _wikiRefreshGate = new(1, 1);

    private readonly WebView2 _browser = new() { Dock = DockStyle.Fill, Visible = false };
    private readonly Panel _startup = new() { Dock = DockStyle.Fill, BackColor = Canvas };
    private readonly Label _status = new()
    {
        AutoSize = true,
        Font = new Font("Microsoft YaHei UI", 12f, FontStyle.Regular),
        ForeColor = Color.FromArgb(27, 36, 48),
        Text = "正在接通本机助手…",
    };
    private readonly Button _retry = new()
    {
        AutoSize = true,
        BackColor = Color.FromArgb(31, 111, 235),
        FlatStyle = FlatStyle.Flat,
        ForeColor = Color.White,
        UseVisualStyleBackColor = false,
        FlatAppearance = { BorderSize = 0 },
        Cursor = Cursors.Hand,
        Text = "重新连接",
        Visible = false,
    };
    private readonly HttpClient _http = new(new SocketsHttpHandler { UseProxy = false })
    {
        Timeout = TimeSpan.FromSeconds(10),
    };
    private CoreWebView2? _webCore;

    private Process? _engine;
    private string _dataRoot = "";
    private string _clientSettingsPath = "";
    private string _sharedConfigPath = "";
    private string _engineConfigPath = "";
    private string _enginePath = "";
    private string _engineDirectory = "";
    private bool _ownsEngine;
    private int _port;
    private bool _started;
    private bool _closing;
    private bool _allowExit;
    private AccountWorkspace? _workspace;
    private HelperPane? _helperPane;
    private FrameCapLiveManager? _frameCapManager;
    private DeployManager? _deployManager;
    private System.Threading.Timer? _frameCapSyncTimer;
    private readonly SemaphoreSlim _frameCapSyncGate = new(1, 1);
    // 账号归属判定用的工作区快照：按端口给锁帧目标找到账号 id。最多 15 秒复用一次，避免定时同步反复扫描进程。
    private WorkspaceAccount[] _frameCapAccounts = [];
    private DateTime _frameCapAccountsAt = DateTime.MinValue;

    internal MainForm()
    {
        Text = "桌破控制台";
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1540, 980);
        MinimumSize = new Size(900, 620);
        BackColor = Canvas;
        ShowInTaskbar = true;
        Font = new Font("Microsoft YaHei UI", 9f);
        AutoScaleMode = AutoScaleMode.Dpi;
        ShowIcon = true;

        _startup.Controls.Add(_status);
        _startup.Controls.Add(_retry);
        _startup.Resize += (_, _) => LayoutStartup();
        _retry.Click += async (_, _) => await StartClientAsync();

        Controls.Add(_browser);
        Controls.Add(_startup);
        Shown += async (_, _) => await StartClientAsync();
        FormClosing += OnFormClosing;
    }

    private async Task StartClientAsync()
    {
        if (_started || _closing) return;
        _started = true;
        _retry.Visible = false;
        _status.Text = "正在接通本机助手…";
        LayoutStartup();

        try
        {
            var runtime = PrepareRuntime();
            _dataRoot = runtime.DataRoot;
            _clientSettingsPath = Path.Combine(_dataRoot, "client-settings.json");
            _sharedConfigPath = runtime.SharedConfigPath;
            _engineConfigPath = runtime.EngineConfigPath;
            _enginePath = runtime.EnginePath;
            _engineDirectory = runtime.EngineDirectory;
            _frameCapManager = new FrameCapLiveManager(_dataRoot, WriteLog);
            StopEmbeddedEngineProcesses(runtime.EnginePath);
            _port = FindAvailablePort(ReadConfiguredPort(_engineConfigPath));
            StartEngine(runtime.EnginePath, runtime.EngineDirectory, _port);
            await WaitForEngineAsync(_port);
            _workspace = new AccountWorkspace(_dataRoot, _engineConfigPath, _port);
            _deployManager = new DeployManager(_dataRoot, _workspace, WriteLog);
            await SyncFrameCapAccountsAsync(publishStatus: false);
            _frameCapSyncTimer = new System.Threading.Timer(
                _ => _ = SyncFrameCapAccountsAsync(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10));

            var userData = Path.Combine(_dataRoot, "WebView2");
            Directory.CreateDirectory(userData);
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: userData);
            await _browser.EnsureCoreWebView2Async(environment);
            _webCore = _browser.CoreWebView2;
            ConfigureBrowser(_webCore);
            _browser.Source = new Uri($"http://127.0.0.1:{_port}/", UriKind.Absolute);
            _browser.Visible = true;
            _startup.Visible = false;
        }
        catch (Exception ex)
        {
            _started = false;
            _status.Text = "连接失败\r\n" + ex.Message;
            _retry.Visible = true;
            LayoutStartup();
            WriteLog(ex.ToString());
        }
    }

    private (string DataRoot, string EngineDirectory, string EnginePath, string EngineConfigPath, string SharedConfigPath) PrepareRuntime()
    {
        string dataRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DeskrawlHubClient");
        byte[] payload = ReadEnginePayload();
        string digest = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        string engineDirectory = Path.Combine(dataRoot, "runtime", digest[..16]);
        Directory.CreateDirectory(engineDirectory);

        string enginePath = Path.Combine(engineDirectory, "DeskrawlHubEngine.exe");
        if (!File.Exists(enginePath))
        {
            string stagingPath = enginePath + ".writing";
            File.WriteAllBytes(stagingPath, payload);
            File.Move(stagingPath, enginePath, overwrite: true);
        }

        string sharedConfigPath = Path.Combine(dataRoot, "config.json");
        if (!File.Exists(sharedConfigPath))
        {
            string desktopConfig = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "config.json");
            if (File.Exists(desktopConfig))
                File.Copy(desktopConfig, sharedConfigPath);
        }
        string engineConfigPath = Path.Combine(engineDirectory, "config.json");
        if (!File.Exists(engineConfigPath) && File.Exists(sharedConfigPath))
            File.Copy(sharedConfigPath, engineConfigPath);

        return (dataRoot, engineDirectory, enginePath, engineConfigPath, sharedConfigPath);
    }

    private static byte[] ReadEnginePayload()
    {
        using var compressed = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("DeskrawlHubClient.Assets.engine.payload.gz")
            ?? throw new InvalidOperationException("客户端内的运行模块无法读取。");
        using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzip.CopyTo(output);
        return output.ToArray();
    }

    private void ConfigureBrowser(CoreWebView2 core)
    {
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.AddWebResourceRequestedFilter($"http://127.0.0.1:{_port}/*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += ServeClientAssets;

        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            OpenExternalWebLink(e.Uri);
        };
        core.WebMessageReceived += (_, e) => HandleWebMessage(core, e);
        core.NavigationStarting += (_, e) =>
        {
            if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri)
                && uri.Scheme is "http" or "https"
                && (uri.Host != "127.0.0.1" || uri.Port != _port))
            {
                e.Cancel = true;
                OpenExternalWebLink(e.Uri);
            }
        };
        core.ProcessFailed += (_, e) =>
        {
            if (!_closing)
                WriteLog("WebView2 process failed: " + e.ProcessFailedKind);
        };
    }

    private void HandleWebMessage(CoreWebView2 core, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            if (!Uri.TryCreate(e.Source, UriKind.Absolute, out var source)
                || source.Host != "127.0.0.1" || source.Port != _port) return;
            using var message = JsonDocument.Parse(e.WebMessageAsJson);
            var root = message.RootElement;
            if (root.ValueKind == JsonValueKind.String
                && string.Equals(root.GetString(), "exit-client", StringComparison.Ordinal))
            {
                _allowExit = true;
                Close();
                return;
            }

            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("type", out var typeElement)) return;
            string? type = typeElement.GetString();
            if (type == "workspace-request")
            {
                _ = HandleWorkspaceRequestAsync(core, root.Clone());
            }
            else if (type == "load-update-settings")
            {
                PostClientUpdateSettings(core, "update-settings-loaded", ReadClientUpdateSettings());
            }
            else if (type == "load-frame-cap-settings")
            {
                PostGlobalFrameCapState(core, "frame-cap-settings-loaded");
            }
            else if (type == "set-global-frame-cap"
                && root.TryGetProperty("fps", out JsonElement accountFpsElement)
                && accountFpsElement.TryGetInt32(out int accountFps)
                && root.TryGetProperty("enabled", out JsonElement accountEnabledElement)
                && (accountEnabledElement.ValueKind is JsonValueKind.True or JsonValueKind.False))
            {
                _ = SetGlobalFrameCapAsync(accountEnabledElement.GetBoolean(), accountFps, core);
            }
            else if (type == "set-account-frame-cap")
            {
                if (TryReadAccountFrameCap(root, out string frameCapAccount, out bool frameCapEnabled, out int frameCapFps))
                    _ = SetAccountFrameCapAsync(frameCapAccount, frameCapEnabled, frameCapFps, core);
                else
                    PostFrameCapError(core, "账号锁帧设置没有保存：缺少账号、开关或帧率。");
            }
            else if (type == "save-update-settings"
                && root.TryGetProperty("settings", out var settingsElement))
            {
                var settings = JsonSerializer.Deserialize<ClientUpdateSettings>(
                    settingsElement.GetRawText(), ClientSettingsJsonOptions) ?? new ClientUpdateSettings();
                string savedRepository = NormalizeGithubRepository(settings.RepositoryUrl);
                settings = settings with
                {
                    RepositoryUrl = savedRepository.Length > 0 ? savedRepository : DefaultUpdateRepositoryUrl,
                    IgnoredVersion = settings.IgnoredVersion?.Trim() ?? "",
                    LastCheckedUtc = Math.Max(0, settings.LastCheckedUtc),
                };
                Directory.CreateDirectory(Path.GetDirectoryName(_clientSettingsPath)!);
                WriteConfigIfChanged(_clientSettingsPath,
                    JsonSerializer.Serialize(settings, ClientSettingsJsonOptions));
                PostClientUpdateSettings(core, "update-settings-saved", settings);
            }
        }
        catch (Exception ex)
        {
            WriteLog("Client settings message failed: " + ex.Message);
        }
    }

    /// <summary>解析 {type:'set-account-frame-cap', account, enabled, fps}；任何一项缺失都返回 false。</summary>
    private static bool TryReadAccountFrameCap(JsonElement root, out string account, out bool enabled, out int fps)
    {
        account = "";
        enabled = false;
        fps = 0;
        if (!root.TryGetProperty("account", out JsonElement accountElement)
            || accountElement.ValueKind != JsonValueKind.String) return false;
        account = accountElement.GetString() ?? "";
        if (account.Length == 0) return false;
        if (!root.TryGetProperty("enabled", out JsonElement enabledElement)
            || enabledElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
        enabled = enabledElement.GetBoolean();
        if (!root.TryGetProperty("fps", out JsonElement fpsElement) || !fpsElement.TryGetInt32(out fps)) return false;
        return fps is 10 or 30;
    }

    private async Task HandleWorkspaceRequestAsync(CoreWebView2 core, JsonElement request)
    {
        string requestId = request.GetProperty("requestId").GetString() ?? "";
        try
        {
            if (_workspace is null) throw new InvalidOperationException("账号环境尚未就绪。");
            string action = request.GetProperty("action").GetString() ?? "";
            string id = request.TryGetProperty("id", out var idValue) ? idValue.GetString() ?? "" : "";
            object? result = null;
            switch (action)
            {
                case "list":
                    result = await _workspace.RefreshAsync();
                    break;
                case "lineage-list":
                    result = await _workspace.ReadLineageSummariesAsync();
                    break;
                case "lineage-report":
                    result = await _workspace.ReadLineageReportsAsync();
                    break;
                case "auction-inventory":
                    result = await _workspace.ReadAuctionInventoryAsync();
                    break;
                case "launch-helper":
                case "launch-game":
                    result = new { message = await _workspace.LaunchAsync(id, action == "launch-helper") };
                    break;
                case "save":
                    await _workspace.SaveProfileAsync(id, request.GetProperty("helperDirectory").GetString() ?? "",
                        request.GetProperty("gameExecutable").GetString() ?? "", request.GetProperty("gameLaunchMode").GetString() ?? "steam");
                    result = await _workspace.RefreshAsync(true);
                    break;
                case "hide-account":
                case "unhide-account":
                    result = await _workspace.SetHiddenAsync(id, action == "hide-account");
                    break;
                case "hidden-list":
                    result = await _workspace.ListHiddenAsync();
                    break;
                case "add-account":
                    result = await _workspace.AddAccountAsync(
                        request.TryGetProperty("sandbox", out var sandboxValue) ? sandboxValue.GetString() ?? "" : "");
                    break;
                case "detect-ports":
                    result = await Deploy().DetectPortsAsync(
                        request.TryGetProperty("sandbox", out JsonElement portSandbox) ? portSandbox.GetString() ?? "" : "");
                    break;
                case "deploy-helper":
                    result = await Deploy().DeployHelperAsync(
                        new DeployHelperRequest(
                            request.TryGetProperty("sandbox", out JsonElement deploySandbox) ? deploySandbox.GetString() ?? "" : "",
                            request.TryGetProperty("port", out JsonElement deployPort) && deployPort.TryGetInt32(out int deployPortValue)
                                ? deployPortValue : 0,
                            request.TryGetProperty("packagePath", out JsonElement deployPackage) ? deployPackage.GetString() ?? "" : "",
                            request.TryGetProperty("packageUrl", out JsonElement deployUrl) ? deployUrl.GetString() ?? "" : "",
                            request.TryGetProperty("mode", out JsonElement deployMode) ? deployMode.GetString() ?? "" : ""),
                        steps => PostDeployProgress(core, requestId, steps));
                    break;
                case "browse-helper":
                    using (var dialog = new FolderBrowserDialog { Description = "选择此环境的 X1X 助手目录", UseDescriptionForTitle = true })
                        result = new { path = dialog.ShowDialog(this) == DialogResult.OK ? dialog.SelectedPath : "" };
                    break;
                case "browse-game":
                    using (var dialog = new OpenFileDialog { Title = "选择 Deskrawl.exe", Filter = "Deskrawl|Deskrawl.exe", CheckFileExists = true })
                        result = new { path = dialog.ShowDialog(this) == DialogResult.OK ? dialog.FileName : "" };
                    break;
                case "open-helper":
                case "open-browser":
                    var connection = await _workspace.ConnectAsync(id);
                    if (action == "open-browser")
                        OpenExternalWebLink($"https://x1x.tw/deskrawl/helper/?lang=zh-CN#token={connection.Token}&port={connection.Port}");
                    else
                    {
                        _helperPane?.Dispose();
                        var pane = new HelperPane(_workspace, connection, _dataRoot,
                            connection.Sandbox.Length == 0 ? "主机" : "沙箱 " + connection.Sandbox, CloseHelperPane);
                        _helperPane = pane;
                        Controls.Add(pane);
                        pane.BringToFront();
                        await pane.InitializeAsync();
                    }
                    result = new { message = action == "open-browser" ? "已打开此环境的配对页面。" : "" };
                    break;
                default:
                    throw new InvalidOperationException("未知的账号操作。");
            }
            if (!_closing) core.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "workspace-result", requestId, ok = true, result }, ClientSettingsJsonOptions));
        }
        catch (Exception ex)
        {
            // Pairing credentials and remote page addresses stay out of logs.
            if (!_closing) core.PostWebMessageAsJson(JsonSerializer.Serialize(new
            {
                type = "workspace-result", requestId, ok = false,
                error = ex is HttpRequestException or TaskCanceledException ? "助手连接超时，请确认目标助手正在运行后重试。" : ex.Message,
            }, ClientSettingsJsonOptions));
        }
    }

    private DeployManager Deploy() =>
        _deployManager ?? throw new InvalidOperationException("部署模块尚未就绪，请稍后重试。");

    /// <summary>部署进度：每完成一步推一次，前端按 steps 逐条更新。</summary>
    private void PostDeployProgress(CoreWebView2 core, string requestId, DeployStep[] steps)
    {
        if (_closing || IsDisposed) return;
        try
        {
            BeginInvoke((Action)(() =>
            {
                try
                {
                    core.PostWebMessageAsJson(JsonSerializer.Serialize(new
                    {
                        type = "deploy-progress",
                        requestId,
                        steps,
                    }, ClientSettingsJsonOptions));
                }
                catch (InvalidOperationException) { }
            }));
        }
        catch (InvalidOperationException) { }
    }

    private void CloseHelperPane()
    {
        _helperPane?.Dispose();
        _helperPane = null;
        _browser.BringToFront();
    }

    private ClientUpdateSettings ReadClientUpdateSettings()
    {
        ClientUpdateSettings settings = new();
        try
        {
            if (File.Exists(_clientSettingsPath))
                settings = JsonSerializer.Deserialize<ClientUpdateSettings>(
                    File.ReadAllText(_clientSettingsPath, Encoding.UTF8), ClientSettingsJsonOptions)
                    ?? new ClientUpdateSettings();
        }
        catch (Exception ex)
        {
            WriteLog("Client settings read failed: " + ex.Message);
        }
        // 仓库地址为空时用内置仓库，更新检测不会因为缺配置而空转。
        string repository = NormalizeGithubRepository(settings.RepositoryUrl);
        return repository.Length > 0
            ? settings with { RepositoryUrl = repository }
            : settings with { RepositoryUrl = DefaultUpdateRepositoryUrl };
    }

    private static void PostClientUpdateSettings(CoreWebView2 core, string type, ClientUpdateSettings settings)
    {
        core.PostWebMessageAsJson(JsonSerializer.Serialize(new { type, settings }, ClientSettingsJsonOptions));
    }

    private async Task<AccountFrameCapStatus[]> SyncFrameCapAccountsAsync(bool publishStatus = true, bool waitForSync = false)
    {
        if (_closing || _frameCapManager is null || _port <= 0)
            return [];
        bool acquired = waitForSync
            ? await _frameCapSyncGate.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false)
            : await _frameCapSyncGate.WaitAsync(0).ConfigureAwait(false);
        if (!acquired) return [];

        try
        {
            Dictionary<int, FrameCapPortTarget>? targets = await ReadFrameCapTargetsAsync().ConfigureAwait(false);
            if (targets is null) return [];

            AccountFrameCapStatus[] changes = await _frameCapManager
                .SyncAccountProcessesAsync(targets).ConfigureAwait(false);
            if (publishStatus && changes.Length > 0 && _webCore is { } core && !IsDisposed)
            {
                try
                {
                    BeginInvoke((Action)(() => PostGlobalFrameCapState(core, "frame-cap-account-statuses", changes)));
                }
                catch (InvalidOperationException) { }
            }
            return changes;
        }
        catch (Exception ex)
        {
            if (!_closing) WriteLog("Account frame-cap process sync failed: " + ex.GetBaseException().Message);
            return [];
        }
        finally
        {
            _frameCapSyncGate.Release();
        }
    }

    /// <summary>
    /// 组装「端口 → 游戏进程 + 账号」：游戏进程来自 /api/overview，账号归属来自工作区快照
    /// （WorkspaceAccount.Id / ControlPort / GamePids）。两边都对不上时账号留空，只按全局设置走，
    /// 不会把某个账号的覆盖项套到别的账号头上。
    /// </summary>
    private async Task<Dictionary<int, FrameCapPortTarget>?> ReadFrameCapTargetsAsync()
    {
        var gameByPort = new Dictionary<int, int>();
        using (HttpResponseMessage response = await _http.GetAsync(
            $"http://127.0.0.1:{_port}/api/overview").ConfigureAwait(false))
        {
            if (!response.IsSuccessStatusCode) return null;
            using JsonDocument document = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync().ConfigureAwait(false));
            if (!document.RootElement.TryGetProperty("instances", out JsonElement instances)
                || instances.ValueKind != JsonValueKind.Array)
                return null;

            foreach (JsonElement instance in instances.EnumerateArray())
            {
                if (!instance.TryGetProperty("port", out JsonElement portElement)
                    || !portElement.TryGetInt32(out int port) || port <= 0)
                    continue;
                int pid = 0;
                if (instance.TryGetProperty("game", out JsonElement game)
                    && game.ValueKind == JsonValueKind.Object
                    && game.TryGetProperty("running", out JsonElement running)
                    && running.ValueKind == JsonValueKind.True
                    && game.TryGetProperty("pid", out JsonElement pidElement)
                    && pidElement.TryGetInt32(out int gamePid))
                    pid = gamePid;
                gameByPort[port] = pid;
            }
        }

        WorkspaceAccount[] accounts = await ReadFrameCapAccountsAsync().ConfigureAwait(false);
        var ownerByPort = new Dictionary<int, WorkspaceAccount>();
        foreach (WorkspaceAccount account in accounts)
            if (account.ControlPort > 0) ownerByPort[account.ControlPort] = account;

        var targets = new Dictionary<int, FrameCapPortTarget>();
        // 所有沙盘的盒根，一次同步只查一遍：SbieApi 每次调用都要过驱动。
        string[] boxRoots = BoxRootsForSync();
        foreach ((int port, int processId) in gameByPort)
        {
            string accountId = "";
            if (ownerByPort.TryGetValue(port, out WorkspaceAccount? owner))
            {
                // 端口一致，但游戏进程不属于该账号时说明工作区快照过期：宁可回落到全局，也不套用别人的设置。
                bool pidAgrees = processId <= 0
                    || owner.GamePids.Length == 0
                    || owner.GamePids.Contains(processId);
                if (pidAgrees) accountId = owner.Id;
            }
            targets[port] = new FrameCapPortTarget(port, processId, accountId, boxRoots);
        }

        // 工作区里已经有控制端口、但引擎概览暂时没列出的账号：只在游戏进程能唯一确定时补上。
        foreach (WorkspaceAccount account in ownerByPort.Values)
        {
            if (targets.ContainsKey(account.ControlPort)) continue;
            targets[account.ControlPort] = new FrameCapPortTarget(account.ControlPort,
                account.GamePids.Length == 1 ? account.GamePids[0] : 0, account.Id, boxRoots);
        }

        return targets;
    }

    private DateTime _boxRootsAt;
    private string[] _boxRoots = [];
    private string _boxRootsSignature = "";

    /// <summary>
    /// 所有沙盘的盒根（SbieApi_QueryBoxPath 的真实目录，可能不在 C:\Sandbox）。
    /// 沙箱里的游戏把锁帧回执写在盒子内，主机侧读不到，必须按盒根换算路径；
    /// 回执文件名带 PID、只认本进程号，所以一次把所有盒根都给出去也不会串号。
    /// </summary>
    private string[] BoxRootsForSync()
    {
        if (_boxRoots.Length > 0 && DateTime.UtcNow - _boxRootsAt < TimeSpan.FromSeconds(30)) return _boxRoots;
        var roots = new List<string>();
        var diagnostic = new StringBuilder();
        try
        {
            SandboxRuntime? sandbox = _workspace?.Sandbox;
            if (sandbox is null)
            {
                diagnostic.Append("workspace=null;");
            }
            else
            {
                string[] boxes = sandbox.Boxes();
                diagnostic.Append("boxes=").Append(boxes.Length);
                foreach (string box in boxes)
                {
                    string root = sandbox.Root(box);
                    diagnostic.Append("|").Append(box).Append("->").Append(root.Length == 0 ? "?" : root);
                    if (root.Length > 0 && Directory.Exists(root)) roots.Add(root);
                }
            }
        }
        catch (Exception ex)
        {
            diagnostic.Append("sandbox-api-failed=").Append(ex.GetBaseException().Message);
        }

        // SbieApi 拿不到时按 Sandboxie 的默认布局兜底：<盘>:\Sandbox|SANDBOX[\用户]\<盒>。
        // 盒根下面一定会有 user 或 drive 目录，用这个特征确认，避免把普通文件夹当成沙盘。
        foreach (string drive in Environment.GetLogicalDrives())
        {
            foreach (string folder in new[] { "Sandbox", "SANDBOX" })
            {
                string basePath = Path.Combine(drive, folder);
                if (!Directory.Exists(basePath)) continue;
                foreach (string level1 in EnumerateDirectoriesSafe(basePath))
                {
                    if (LooksLikeBoxRoot(level1)) roots.Add(level1);
                    foreach (string level2 in EnumerateDirectoriesSafe(level1))
                        if (LooksLikeBoxRoot(level2)) roots.Add(level2);
                }
            }
        }

        string[] resolved = roots.Distinct(StringComparer.OrdinalIgnoreCase).Take(64).ToArray();
        _boxRoots = resolved;
        _boxRootsAt = DateTime.UtcNow;
        // 只在结果变化时记一行：同步定时器每 10 秒就会走到这里。
        string signature = string.Join("|", resolved) + "#" + diagnostic;
        if (signature != _boxRootsSignature)
        {
            _boxRootsSignature = signature;
            WriteLog($"Frame cap: 沙盘盒根 {resolved.Length} 个（{string.Join(" | ", resolved)}）{diagnostic}");
        }
        return _boxRoots;
    }

    private static IEnumerable<string> EnumerateDirectoriesSafe(string path)
    {
        try { return Directory.EnumerateDirectories(path).ToArray(); }
        catch { return []; }
    }

    private static bool LooksLikeBoxRoot(string path)
    {
        try { return Directory.Exists(Path.Combine(path, "user")) || Directory.Exists(Path.Combine(path, "drive")); }
        catch { return false; }
    }

    /// <summary>锁帧归属判定用的账号快照；15 秒内复用，避免同步定时器反复枚举进程。</summary>
    private async Task<WorkspaceAccount[]> ReadFrameCapAccountsAsync()
    {
        if (_workspace is null) return [];
        if (_frameCapAccounts.Length > 0
            && DateTime.UtcNow - _frameCapAccountsAt < TimeSpan.FromSeconds(15))
            return _frameCapAccounts;
        try
        {
            WorkspaceAccount[] accounts = await _workspace.RefreshAsync().ConfigureAwait(false);
            _frameCapAccounts = accounts;
            _frameCapAccountsAt = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            if (!_closing) WriteLog("Frame-cap account snapshot failed: " + ex.GetBaseException().Message);
        }
        return _frameCapAccounts;
    }

    private async Task SetAccountFrameCapAsync(string accountId, bool enabled, int fps, CoreWebView2 responseCore)
    {
        try
        {
            if (_frameCapManager is null) throw new InvalidOperationException("锁帧设置尚未就绪。");
            // 先把端口归属刷新一遍，保证单账号设置立刻作用到正确的端口上。
            await SyncFrameCapAccountsAsync(publishStatus: false, waitForSync: true).ConfigureAwait(false);
            AccountFrameCapStatus[] statuses = await _frameCapManager
                .SetAccountLimitAsync(accountId, enabled, fps).ConfigureAwait(false);
            if (!IsDisposed)
            {
                try
                {
                    BeginInvoke((Action)(() => PostGlobalFrameCapState(
                        responseCore, "frame-cap-settings-updated", statuses)));
                }
                catch (InvalidOperationException) { }
            }
        }
        catch (Exception ex)
        {
            WriteLog("Account frame-cap update failed: " + ex.GetBaseException().Message);
            PostFrameCapError(responseCore, "账号锁帧设置没有保存：" + FrameCapErrorText(ex));
        }
    }

    private static string FrameCapErrorText(Exception ex)
    {
        Exception inner = ex.GetBaseException();
        return inner is ArgumentException ? inner.Message : "帧率或账号标识无效，请刷新后重试。";
    }

    private void PostFrameCapError(CoreWebView2 core, string message)
    {
        if (IsDisposed) return;
        try
        {
            BeginInvoke((Action)(() =>
            {
                try
                {
                    core.PostWebMessageAsJson(JsonSerializer.Serialize(new
                    {
                        type = "global-frame-cap-error",
                        message,
                    }, ClientSettingsJsonOptions));
                }
                catch (InvalidOperationException) { }
            }));
        }
        catch (InvalidOperationException) { }
    }

    private async Task SetGlobalFrameCapAsync(bool enabled, int fps, CoreWebView2 responseCore)
    {
        try
        {
            if (_frameCapManager is null) throw new InvalidOperationException("全局锁帧设置尚未就绪。");
            await SyncFrameCapAccountsAsync(publishStatus: false, waitForSync: true).ConfigureAwait(false);
            AccountFrameCapStatus[] statuses = await _frameCapManager
                .SetGlobalLimitAsync(enabled, fps).ConfigureAwait(false);
            if (!IsDisposed)
            {
                try
                {
                    BeginInvoke((Action)(() => PostGlobalFrameCapState(
                        responseCore, "frame-cap-settings-updated", statuses)));
                }
                catch (InvalidOperationException) { }
            }
        }
        catch (Exception ex)
        {
            WriteLog("Global frame-cap update failed: " + ex.GetBaseException().Message);
            if (!IsDisposed)
            {
                try
                {
                    BeginInvoke((Action)(() => responseCore.PostWebMessageAsJson(JsonSerializer.Serialize(new
                    {
                        type = "global-frame-cap-error",
                        message = "全局锁帧设置没有保存。",
                    }, ClientSettingsJsonOptions))));
                }
                catch (InvalidOperationException) { }
            }
        }
    }

    private void PostGlobalFrameCapState(
        CoreWebView2 core,
        string type,
        IReadOnlyList<AccountFrameCapStatus>? changedStatuses = null)
    {
        GlobalFrameCapSettings settings = _frameCapManager?.SettingsSnapshot() ?? new GlobalFrameCapSettings();
        IReadOnlyList<AccountFrameCapStatus> statuses = changedStatuses ??
            _frameCapManager?.StatusSnapshot() ?? [];
        core.PostWebMessageAsJson(JsonSerializer.Serialize(new
        {
            type,
            settings,
            statuses,
        }, ClientSettingsJsonOptions));
    }

    private static string NormalizeGithubRepository(string? value)
    {
        string address = (value ?? "").Trim();
        if (!address.Contains("://", StringComparison.Ordinal)) address = "https://github.com/" + address.TrimStart('/');
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)) return "";

        string[] segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2) return "";
        string owner = Uri.UnescapeDataString(segments[0]);
        string repo = Uri.UnescapeDataString(segments[1]);
        if (repo.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) repo = repo[..^4];
        if (owner.Length == 0 || repo.Length == 0
            || owner.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            || repo.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'))) return "";
        return $"https://github.com/{owner}/{repo}";
    }

    private static HttpClient CreateWikiHttpClient()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) DeskrawlHubClient/1.3");
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("zh-CN,zh;q=0.9");
        return client;
    }

    private async Task RefreshWikiEquipmentAsync(CoreWebView2 core, bool force = false)
    {
        string cachePath = Path.Combine(_dataRoot, "wiki-equipment.json");
        if (!force && File.Exists(cachePath)
            && DateTime.UtcNow - File.GetLastWriteTimeUtc(cachePath) < TimeSpan.FromDays(1)) return;
        if (!await _wikiRefreshGate.WaitAsync(0)) return;

        try
        {
            if (!force && File.Exists(cachePath)
                && DateTime.UtcNow - File.GetLastWriteTimeUtc(cachePath) < TimeSpan.FromDays(1)) return;

            using var response = await WikiHttp.GetAsync(WikiEquipmentUrl);
            response.EnsureSuccessStatusCode();
            string html = await response.Content.ReadAsStringAsync();
            var items = ParseWikiEquipment(html);
            if (items.Count < 100)
                throw new InvalidDataException($"Wiki returned only {items.Count} equipment entries.");

            Directory.CreateDirectory(_dataRoot);
            string payload = JsonSerializer.Serialize(new
            {
                sourceUrl = WikiEquipmentUrl,
                updatedAtUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                items,
            }, ClientSettingsJsonOptions);
            WriteConfigIfChanged(cachePath, payload);
            core.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "wiki-equipment-updated" }, ClientSettingsJsonOptions));
        }
        catch (Exception ex)
        {
            WriteLog("Wiki equipment refresh failed: " + ex.Message);
        }
        finally
        {
            _wikiRefreshGate.Release();
        }
    }

    private static List<WikiEquipmentEntry> ParseWikiEquipment(string html)
    {
        var items = new List<WikiEquipmentEntry>();
        foreach (Match match in WikiEquipmentRowRegex.Matches(html))
        {
            string row = match.Value;
            int tagEnd = row.IndexOf('>');
            if (tagEnd < 0) continue;
            string attributes = row[..(tagEnd + 1)];
            string body = row[(tagEnd + 1)..];
            string id = ReadWikiAttribute(attributes, "data-id");
            if (id.StartsWith("item:", StringComparison.OrdinalIgnoreCase)) id = id[5..];
            string rarity = ReadWikiAttribute(attributes, "data-r");
            string name = ReadWikiElement(body, @"<b\b[^>]*>(?<value>.*?)</b>");
            string nameEn = ReadWikiElement(body, @"<span\b[^>]*class=[""']en[""'][^>]*>(?<value>.*?)</span>");
            string detail = ReadWikiElement(body, @"<small\b[^>]*>(?<value>.*?)</small>");
            string qualityName = Regex.Split(detail, @"\s*[·•]\s*")[0].Trim();
            if (id.Length == 0 || name.Length == 0 || nameEn.Length == 0 || rarity.Length == 0) continue;
            items.Add(new WikiEquipmentEntry(id, name, nameEn, rarity, qualityName));
        }
        return items;
    }

    private static string ReadWikiAttribute(string tag, string name)
    {
        var match = Regex.Match(tag, $@"\b{Regex.Escape(name)}\s*=\s*[""'](?<value>[^""']*)[""']", RegexOptions.IgnoreCase);
        return match.Success ? WebUtility.HtmlDecode(match.Groups["value"].Value).Trim() : "";
    }

    private static string ReadWikiElement(string html, string pattern)
    {
        var match = Regex.Match(html, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (!match.Success) return "";
        string text = Regex.Replace(match.Groups["value"].Value, "<[^>]+>", "");
        return WebUtility.HtmlDecode(text).Trim();
    }

    private void ServeClientAssets(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        if (!Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var uri) || uri.Port != _port)
            return;

        if (uri.AbsolutePath == "/wiki-equipment.json")
        {
            string cachePath = Path.Combine(_dataRoot, "wiki-equipment.json");
            byte[] json;
            try
            {
                if (File.Exists(cachePath))
                {
                    json = File.ReadAllBytes(cachePath);
                    if (DateTime.UtcNow - File.GetLastWriteTimeUtc(cachePath) >= TimeSpan.FromDays(1))
                        _ = RefreshWikiEquipmentAsync(_browser.CoreWebView2);
                }
                else
                {
                    json = Encoding.UTF8.GetBytes("{\"items\":[]}");
                    _ = RefreshWikiEquipmentAsync(_browser.CoreWebView2, force: true);
                }
            }
            catch (Exception ex)
            {
                WriteLog("Wiki equipment cache read failed: " + ex.Message);
                json = Encoding.UTF8.GetBytes("{\"items\":[]}");
            }
            e.Response = _browser.CoreWebView2.Environment.CreateWebResourceResponse(
                new MemoryStream(json), 200, "OK",
                "Content-Type: application/json; charset=utf-8\r\nCache-Control: no-store\r\n");
            return;
        }

        string? resourceName = uri.AbsolutePath switch
        {
            "/" or "/index.html" => "DeskrawlHubClient.WebAssets.index.html",
            "/client.css" => "DeskrawlHubClient.WebAssets.client.css",
            "/client.js" => "DeskrawlHubClient.WebAssets.client.js",
            "/client-icon.png" or "/favicon.png" => "DeskrawlHubClient.WebAssets.client-icon.png",
            _ => null,
        };
        if (resourceName is null) return;
        WriteLog("Served client asset: " + uri.AbsolutePath);

        byte[] bytes;
        using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName))
        {
            if (stream is null) return;
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            bytes = memory.ToArray();
        }

        string contentType = resourceName.EndsWith(".css", StringComparison.OrdinalIgnoreCase)
            ? "text/css; charset=utf-8"
            : resourceName.EndsWith(".js", StringComparison.OrdinalIgnoreCase)
                ? "text/javascript; charset=utf-8"
                : resourceName.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                    ? "image/png"
                    : resourceName.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)
                        ? "image/svg+xml"
                        : "text/html; charset=utf-8";
        e.Response = _browser.CoreWebView2.Environment.CreateWebResourceResponse(
            new MemoryStream(bytes), 200, "OK",
            $"Content-Type: {contentType}\r\nCache-Control: no-store\r\n");
    }

    private static void OpenExternalWebLink(string address)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https")) return;
        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch
        {
            // External links do not block local control actions.
        }
    }

    private static int ReadConfiguredPort(string configPath)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(configPath, Encoding.UTF8));
            if (document.RootElement.TryGetProperty("server", out var server)
                && server.TryGetProperty("port", out var port)
                && port.TryGetInt32(out int value)
                && value is > 0 and <= 65535)
                return value;
        }
        catch
        {
            // The control engine has its own defaults when the optional config is absent.
        }
        return DefaultPort;
    }

    private static void WriteConfigIfChanged(string path, string content)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        if (File.Exists(path) && string.Equals(File.ReadAllText(path, Encoding.UTF8), content, StringComparison.Ordinal)) return;
        string staging = path + ".writing";
        File.WriteAllText(staging, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(staging, path, overwrite: true);
    }

    private static int FindAvailablePort(int preferred)
    {
        for (int port = preferred; port < Math.Min(preferred + 40, 65535); port++)
        {
            try
            {
                using var listener = new TcpListener(IPAddress.Loopback, port);
                listener.Start();
                listener.Stop();
                return port;
            }
            catch (SocketException)
            {
                // Existing controller processes may still occupy older ports.
            }
        }

        using var ephemeral = new TcpListener(IPAddress.Loopback, 0);
        ephemeral.Start();
        int selected = ((IPEndPoint)ephemeral.LocalEndpoint).Port;
        ephemeral.Stop();
        return selected;
    }

    private void StartEngine(string enginePath, string workingDirectory, int port)
    {
        var start = new ProcessStartInfo
        {
            FileName = enginePath,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        start.ArgumentList.Add("--host");
        start.ArgumentList.Add("127.0.0.1");
        start.ArgumentList.Add("--port");
        start.ArgumentList.Add(port.ToString());
        start.ArgumentList.Add("--no-open");
        start.ArgumentList.Add("--no-minimize");

        _engine = Process.Start(start) ?? throw new InvalidOperationException("本机运行模块没有启动。");
        _ownsEngine = true;
    }

    private async Task WaitForEngineAsync(int port)
    {
        var deadline = DateTime.UtcNow.AddSeconds(25);
        while (DateTime.UtcNow < deadline)
        {
            if (_closing) throw new OperationCanceledException("客户端正在关闭。");
            if (_engine?.HasExited == true)
                throw new InvalidOperationException($"本机运行模块已退出，代码：{_engine.ExitCode}。\r\n日志：{GetLogPath()}");
            try
            {
                using var response = await _http.GetAsync($"http://127.0.0.1:{port}/api/overview");
                if (response.IsSuccessStatusCode) return;
            }
            catch
            {
                // The local listener may need another short startup interval.
            }
            await Task.Delay(250);
        }
        throw new TimeoutException("等待本机助手超时。请检查 Sandboxie 与助手实例状态。");
    }

    private void LayoutStartup()
    {
        int centerX = _startup.ClientSize.Width / 2;
        int top = Math.Max(24, (_startup.ClientSize.Height - 104) / 2);
        _status.Location = new Point(centerX - _status.PreferredWidth / 2, top);
        _retry.Location = new Point(centerX - _retry.PreferredSize.Width / 2, top + 56);
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (!_allowExit && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            WindowState = FormWindowState.Minimized;
            return;
        }

        _closing = true;
        _helperPane?.Dispose();
        _workspace?.Dispose();
        _frameCapSyncTimer?.Dispose();
        _frameCapManager?.Dispose();
        _deployManager?.Dispose();
        _http.Dispose();
        if (_ownsEngine && _engine is { HasExited: false })
        {
            try
            {
                if (File.Exists(_engineConfigPath) && !string.IsNullOrEmpty(_sharedConfigPath))
                    WriteConfigIfChanged(_sharedConfigPath, File.ReadAllText(_engineConfigPath, Encoding.UTF8));
            }
            catch { }
        }
        if (_ownsEngine && !string.IsNullOrEmpty(_enginePath))
            StopEmbeddedEngineProcesses(_enginePath);
        _engine?.Dispose();
    }

    private static void StopEmbeddedEngineProcesses(string enginePath)
    {
        string expected = Path.GetFullPath(enginePath);
        string processName = Path.GetFileNameWithoutExtension(enginePath);
        foreach (var process in Process.GetProcessesByName(processName))
        {
            using (process)
            {
                try
                {
                    string actual = Path.GetFullPath(process.MainModule?.FileName ?? "");
                    if (string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                        process.Kill(entireProcessTree: false);
                }
                catch
                {
                    // An inaccessible or already-exited process does not block client startup or shutdown.
                }
            }
        }
    }

    private string GetLogPath()
    {
        if (string.IsNullOrEmpty(_dataRoot))
            _dataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeskrawlHubClient");
        return Path.Combine(_dataRoot, "client.log");
    }

    private void WriteLog(string message)
    {
        try
        {
            string path = GetLogPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}", Encoding.UTF8);
        }
        catch
        {
            // Logging is best-effort.
        }
    }
}
