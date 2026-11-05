using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DeskrawlHubClient;

internal sealed record FrameCapClientSettings
{
    public bool Enabled { get; init; }
    public int Fps { get; init; } = 10;
    public string RequestId { get; init; } = "";
}

/// <summary>
/// 单个账号的锁帧覆盖项。enabled=false 是一条「显式关闭」记录，它同样会压过全局开关，
/// 这样用户可以把某个账号单独解锁而其它账号继续锁帧。
/// </summary>
internal sealed record AccountFrameCapOverride
{
    public bool Enabled { get; init; }
    public int Fps { get; init; } = 10;
}

/// <summary>
/// 锁帧设置：全局挡位 + 按账号 id（"host" / "box:沙盘名"，键统一小写）的覆盖表。
/// 未出现在 <see cref="Accounts"/> 里的账号沿用 Enabled/Fps，即原有全局行为不变。
/// </summary>
internal sealed record GlobalFrameCapSettings
{
    public bool Enabled { get; init; }
    public int Fps { get; init; } = 10;

    /// <summary>已显式设置的账号覆盖表（只保存显式设置过的账号）。</summary>
    public Dictionary<string, AccountFrameCapOverride> Accounts { get; init; } = [];
}

internal sealed record LegacyAccountFrameCapSettings
{
    public Dictionary<int, LegacyAccountFrameCapSetting> Accounts { get; init; } = [];
}

internal sealed record LegacyAccountFrameCapSetting
{
    public bool Enabled { get; init; }
    public int Fps { get; init; } = 10;
}

internal sealed record AccountFrameCapStatus(int Port, int Fps, int ProcessId, bool Applied, string Message)
{
    public string State { get; init; } = "waiting";

    /// <summary>该端口所属的账号 id（"host" / "box:名字"）；无法判定时为空串，状态消息不会写成别的账号。</summary>
    public string Account { get; init; } = "";
}

/// <summary>一个「端口 → 游戏进程 + 所属账号」的锁帧目标；BoxRoot 是该账号所在沙盘的盒根，主机为空。</summary>
internal sealed record FrameCapPortTarget(int Port, int ProcessId, string AccountId, string[]? BoxRoots = null);

internal sealed record FrameCapProcessResult(bool Success, string State, string Message);

internal sealed class FrameCapLiveManager : IDisposable
{
    private const string PluginResourceName = "DeskrawlHubClient.Assets.DeskrawlConsole.FrameCap.Live.dll";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly string _dataRoot;
    private readonly string _settingsPath;
    private readonly string _legacyAccountSettingsPath;
    private readonly string _accountDirectory;
    private readonly Action<string> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<int, AccountFrameCapStatus> _statusesByPort = new();
    private readonly Dictionary<int, FrameCapPortTarget> _targetsByPort = [];
    private readonly Dictionary<int, (int ProcessId, int Fps)> _appliedByPort = [];
    private readonly Dictionary<int, string> _lastFailureByPort = [];
    private readonly Dictionary<int, (int ProcessId, bool Enabled, int Fps, string RequestId)> _requestByPort = [];
    private GlobalFrameCapSettings _settings = new();
    private bool _disposed;

    internal FrameCapLiveManager(string dataRoot, Action<string> log)
    {
        _dataRoot = dataRoot;
        _settingsPath = Path.Combine(dataRoot, "frame-cap-settings.json");
        _legacyAccountSettingsPath = Path.Combine(dataRoot, "account-frame-cap-settings.json");
        _accountDirectory = Path.Combine(dataRoot, "frame-cap");
        _log = log;
        LoadSettings();
    }

    internal GlobalFrameCapSettings SettingsSnapshot() => _settings;

    internal AccountFrameCapStatus[] StatusSnapshot() =>
        _statusesByPort.Values.OrderBy(status => status.Port).ToArray();

    /// <summary>
    /// 全局开关的语义（一键总控）：开启 = 清空所有账号覆盖，并把同一挡位应用到全部端口；
    /// 关闭 = 同样清空覆盖，所有端口都不锁帧（0 帧）。清空覆盖是刻意的：按钮文案是「一键全开/全关」，
    /// 如果关掉全局却留着单账号覆盖，屏幕上的按钮与实际状态会互相矛盾；需要例外时重新打开单个账号即可。
    /// </summary>
    internal async Task<AccountFrameCapStatus[]> SetGlobalLimitAsync(bool enabled, int fps)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidateSetting(fps);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _settings = new GlobalFrameCapSettings { Enabled = enabled, Fps = fps, Accounts = [] };
            PersistSettings();

            var changed = new List<AccountFrameCapStatus>();
            foreach (FrameCapPortTarget target in _targetsByPort.Values.ToArray())
            {
                AccountFrameCapStatus status = target.ProcessId > 0
                    ? await ApplyAsync(target).ConfigureAwait(false)
                    : CreateWaitingStatus(target);
                if (SetStatusIfChanged(status)) changed.Add(status);
            }

            return changed.ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 单账号锁帧：写入/更新该账号的覆盖项并立即作用到它的端口。
    /// 只影响该账号自己的端口；账号暂时没有端口时只保存设置，等游戏/助手出现后由 sync 应用。
    /// </summary>
    internal async Task<AccountFrameCapStatus[]> SetAccountLimitAsync(string accountId, bool enabled, int fps)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidateAccountId(accountId);
        ValidateSetting(fps);
        string key = AccountKey(accountId);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var accounts = new Dictionary<string, AccountFrameCapOverride>(_settings.Accounts, StringComparer.OrdinalIgnoreCase)
            {
                [key] = new AccountFrameCapOverride { Enabled = enabled, Fps = fps },
            };
            _settings = new GlobalFrameCapSettings
            {
                Enabled = _settings.Enabled,
                Fps = _settings.Fps,
                Accounts = accounts,
            };
            PersistSettings();

            var changed = new List<AccountFrameCapStatus>();
            foreach (FrameCapPortTarget target in _targetsByPort.Values
                .Where(target => SameAccount(target.AccountId, accountId)).ToArray())
            {
                AccountFrameCapStatus status = target.ProcessId > 0
                    ? await ApplyAsync(target).ConfigureAwait(false)
                    : CreateWaitingStatus(target);
                if (SetStatusIfChanged(status)) changed.Add(status);
            }

            if (changed.Count == 0)
                _log($"Account frame-cap override saved for {accountId}; no port is attributed to it yet.");
            return changed.ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 同步「端口 → 游戏进程 + 账号」。每个端口按它所属账号的有效帧率设置：
    /// 账号有覆盖项就用覆盖项，否则沿用全局设置；账号无法判定时只按全局设置走，
    /// 绝不会套用其它账号的覆盖项。
    /// </summary>
    internal async Task<AccountFrameCapStatus[]> SyncAccountProcessesAsync(
        IReadOnlyDictionary<int, FrameCapPortTarget> targets)
    {
        if (_disposed) return [];
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var incoming = new Dictionary<int, FrameCapPortTarget>();
            foreach ((int port, FrameCapPortTarget target) in targets)
            {
                if (port <= 0) continue;
                incoming[port] = new FrameCapPortTarget(port, Math.Max(0, target.ProcessId), target.AccountId ?? "", target.BoxRoots);
            }

            int[] ports = _targetsByPort.Keys.Concat(incoming.Keys).Distinct().ToArray();
            var changed = new List<AccountFrameCapStatus>();

            foreach (int port in ports)
            {
                if (!incoming.TryGetValue(port, out FrameCapPortTarget? target))
                {
                    _targetsByPort.Remove(port);
                    _appliedByPort.Remove(port);
                    _requestByPort.Remove(port);
                    AccountFrameCapStatus removed = CreateWaitingStatus(new FrameCapPortTarget(port, 0, ""));
                    if (SetStatusIfChanged(removed)) changed.Add(removed);
                    continue;
                }

                _targetsByPort[port] = target;

                if (target.ProcessId <= 0)
                {
                    _appliedByPort.Remove(port);
                    AccountFrameCapStatus waiting = CreateWaitingStatus(target);
                    if (SetStatusIfChanged(waiting)) changed.Add(waiting);
                    continue;
                }

                int desiredFps = EffectiveFps(target.AccountId);
                if (_appliedByPort.TryGetValue(port, out var applied)
                    && applied.ProcessId == target.ProcessId && applied.Fps == desiredFps)
                    continue;

                AccountFrameCapStatus status = await ApplyAsync(target).ConfigureAwait(false);
                if (SetStatusIfChanged(status)) changed.Add(status);
            }

            return changed.ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>账号的有效帧率：0 = 不锁；有覆盖项按覆盖项，否则按全局。</summary>
    private int EffectiveFps(string accountId)
    {
        if (accountId.Length > 0 && _settings.Accounts.TryGetValue(AccountKey(accountId), out AccountFrameCapOverride? account))
            return account.Enabled ? account.Fps : 0;
        return _settings.Enabled ? _settings.Fps : 0;
    }

    private bool HasOverride(string accountId) =>
        accountId.Length > 0 && _settings.Accounts.ContainsKey(AccountKey(accountId));

    /// <summary>关闭锁帧时仍要写一个合法挡位进去，插件才会接受这份设置文件。</summary>
    private int FallbackFps(string accountId)
    {
        if (accountId.Length > 0 && _settings.Accounts.TryGetValue(AccountKey(accountId), out AccountFrameCapOverride? account)
            && account.Fps is 10 or 30)
            return account.Fps;
        return _settings.Fps is 10 or 30 ? _settings.Fps : 10;
    }

    private void CacheResult(int port, int processId, bool applied, string state, int desiredFps)
    {
        if (applied || state is "restart-required" or "error")
            _appliedByPort[port] = (processId, desiredFps);
        else
            _appliedByPort.Remove(port);
    }

    private async Task<AccountFrameCapStatus> ApplyAsync(FrameCapPortTarget target)
    {
        int fps = EffectiveFps(target.AccountId);
        bool enabled = fps > 0;
        // 同一目标同样的设置要复用同一个请求号：沙箱里的游戏过一两秒才写回执，
        // 每次重试都换号的话回执永远对不上，界面会一直停在「已下发、等回执」。
        int commandFps = enabled ? fps : FallbackFps(target.AccountId);
        string requestId = Guid.NewGuid().ToString("N");
        if (_requestByPort.TryGetValue(target.Port, out var previous)
            && previous.ProcessId == target.ProcessId
            && previous.Enabled == enabled
            && previous.Fps == commandFps)
            requestId = previous.RequestId;
        _requestByPort[target.Port] = (target.ProcessId, enabled, commandFps, requestId);
        var command = new FrameCapClientSettings
        {
            Enabled = enabled,
            Fps = commandFps,
            RequestId = requestId,
        };
        string settingsPath = Path.Combine(_accountDirectory, $"{target.Port}-{target.ProcessId}.json");
        string pluginPath = EnsurePlugin();
        FrameCapProcessResult result = await Task.Run(() =>
            FrameCapLiveInjector.Apply(target.ProcessId, command, settingsPath, pluginPath, _dataRoot, target.BoxRoots))
            .ConfigureAwait(false);
        bool applied = result.Success && (enabled ? result.State == "applied" : result.State == "disabled");
        // 同样的失败每 10 秒会重试一次：只在消息变化时记一行，别把日志刷满。
        if (!result.Success)
        {
            string line = $"{target.Port}|{target.ProcessId}|{result.State}|{result.Message}";
            if (!_lastFailureByPort.TryGetValue(target.Port, out string? last) || last != line)
            {
                _lastFailureByPort[target.Port] = line;
                string roots = target.BoxRoots is { Length: > 0 } ? string.Join(" | ", target.BoxRoots) : "(主机)";
                _log($"Frame cap operation failed for port {target.Port} (account {Describe(target.AccountId)}), PID {target.ProcessId}, state {result.State}: {result.Message} | request={command.RequestId} | boxRoots={roots}");
            }
        }
        else
        {
            _lastFailureByPort.Remove(target.Port);
        }
        CacheResult(target.Port, target.ProcessId, applied, result.State, fps);
        return new AccountFrameCapStatus(target.Port, fps, target.ProcessId, applied, result.Message)
        {
            State = result.State,
            Account = target.AccountId,
        };
    }

    private AccountFrameCapStatus CreateWaitingStatus(FrameCapPortTarget target)
    {
        int fps = EffectiveFps(target.AccountId);
        bool enabled = fps > 0;
        bool scoped = HasOverride(target.AccountId);
        string message = enabled
            ? scoped
                ? $"此账号已单独设为 {fps} 帧，游戏启动后应用。"
                : $"全局锁帧已开启（{fps} 帧），游戏启动后应用。"
            : scoped
                ? "此账号已单独关闭锁帧。"
                : "全局锁帧已关闭。";
        return new AccountFrameCapStatus(target.Port, fps, target.ProcessId, !enabled, message)
        {
            State = enabled ? "waiting" : "disabled",
            Account = target.AccountId,
        };
    }

    private bool SetStatusIfChanged(AccountFrameCapStatus status)
    {
        if (_statusesByPort.TryGetValue(status.Port, out AccountFrameCapStatus? previous) && previous == status)
            return false;
        _statusesByPort[status.Port] = status;
        return true;
    }

    private static void ValidateSetting(int fps)
    {
        if (fps is not (10 or 30)) throw new ArgumentOutOfRangeException(nameof(fps), "帧率挡位只能选择 10 或 30。");
    }

    /// <summary>账号 id 形如 "host" / "box:沙盘名"（见 AccountWorkspace.Id）。</summary>
    private static void ValidateAccountId(string accountId)
    {
        string value = (accountId ?? "").Trim();
        if (value.Length == 0) throw new ArgumentException("账号标识不能为空。", nameof(accountId));
        if (value.Equals("host", StringComparison.OrdinalIgnoreCase)) return;
        if (value.StartsWith("box:", StringComparison.OrdinalIgnoreCase) && value.Length > 4) return;
        throw new ArgumentException($"无法识别的账号标识：{accountId}。", nameof(accountId));
    }

    // 键保留前端传进来的原始写法（"host" / "box:名字"）——回传给前端时必须原样带回，否则卡片上按账号 id 取状态会取不到；
    // 比较时用 OrdinalIgnoreCase，沙盘名大小写不一致也能命中。
    private static string AccountKey(string accountId) => (accountId ?? "").Trim();

    private static bool SameAccount(string left, string right) =>
        left.Length > 0 && right.Length > 0 && string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static string Describe(string accountId) => accountId.Length > 0 ? accountId : "未归属";

    private void LoadSettings()
    {
        try
        {
            bool loadedGlobal = false;
            if (File.Exists(_settingsPath))
            {
                GlobalFrameCapSettings? saved = JsonSerializer.Deserialize<GlobalFrameCapSettings>(
                    File.ReadAllText(_settingsPath, Encoding.UTF8), JsonOptions);
                if (saved is not null && saved.Fps is 10 or 30)
                {
                    _settings = new GlobalFrameCapSettings
                    {
                        Enabled = saved.Enabled,
                        Fps = saved.Fps,
                        Accounts = SanitizeOverrides(saved.Accounts),
                    };
                    loadedGlobal = true;
                }
            }

            if (File.Exists(_legacyAccountSettingsPath)
                && (!loadedGlobal
                    || File.GetLastWriteTimeUtc(_legacyAccountSettingsPath) > File.GetLastWriteTimeUtc(_settingsPath)))
            {
                LegacyAccountFrameCapSettings? legacy = JsonSerializer.Deserialize<LegacyAccountFrameCapSettings>(
                    File.ReadAllText(_legacyAccountSettingsPath, Encoding.UTF8), JsonOptions);
                LegacyAccountFrameCapSetting[] enabledSettings = (legacy?.Accounts.Values
                    ?? Enumerable.Empty<LegacyAccountFrameCapSetting>())
                    .Where(setting => setting.Enabled && setting.Fps is 10 or 30)
                    .ToArray();
                int[] rates = enabledSettings.Select(setting => setting.Fps).Distinct().ToArray();
                _settings = new GlobalFrameCapSettings
                {
                    Enabled = enabledSettings.Length > 0,
                    Fps = rates.Length == 1 ? rates[0] : 10,
                    Accounts = [],
                };
                PersistSettings();
            }
        }
        catch (Exception ex)
        {
            _log("Global frame-cap settings could not be read: " + ex.GetBaseException().Message);
        }
    }

    /// <summary>只接受合法覆盖项，旧文件里多余的/坏掉的记录直接丢弃，避免一次坏写入永久生效。</summary>
    private static Dictionary<string, AccountFrameCapOverride> SanitizeOverrides(
        Dictionary<string, AccountFrameCapOverride>? accounts)
    {
        var result = new Dictionary<string, AccountFrameCapOverride>(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, AccountFrameCapOverride> pair in accounts ?? [])
        {
            string key = AccountKey(pair.Key);
            if (key.Length == 0 || pair.Value is null || pair.Value.Fps is not (10 or 30)) continue;
            result[key] = new AccountFrameCapOverride { Enabled = pair.Value.Enabled, Fps = pair.Value.Fps };
        }
        return result;
    }

    private void PersistSettings()
    {
        Directory.CreateDirectory(_dataRoot);
        string temporaryPath = _settingsPath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(_settings, JsonOptions), new UTF8Encoding(false));
        File.Move(temporaryPath, _settingsPath, overwrite: true);
    }

    private string EnsurePlugin()
    {
        using Stream resource = typeof(FrameCapLiveManager).Assembly.GetManifestResourceStream(PluginResourceName)
            ?? throw new FileNotFoundException("内置限帧组件缺失。");
        using var buffer = new MemoryStream();
        resource.CopyTo(buffer);
        byte[] bytes = buffer.ToArray();
        string hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()[..12];
        string pluginDirectory = Path.Combine(_dataRoot, "plugins");
        Directory.CreateDirectory(pluginDirectory);
        string destination = Path.Combine(pluginDirectory, $"DeskrawlConsole.FrameCap.Live.{hash}.dll");
        if (File.Exists(destination))
        {
            string expectedHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            string existingHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(destination))).ToLowerInvariant();
            if (existingHash == expectedHash) return destination;
            destination = Path.Combine(pluginDirectory, $"DeskrawlConsole.FrameCap.Live.{hash}.{Guid.NewGuid():N}.dll");
        }

        string staging = destination + ".tmp";
        File.WriteAllBytes(staging, bytes);
        File.Move(staging, destination, overwrite: false);
        return destination;
    }

    public void Dispose() => _disposed = true;
}
