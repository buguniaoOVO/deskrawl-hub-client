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

internal sealed record GlobalFrameCapSettings
{
    public bool Enabled { get; init; }
    public int Fps { get; init; } = 10;
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
}

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
    private readonly Dictionary<int, int> _processByPort = [];
    private readonly Dictionary<int, (int ProcessId, int Fps)> _appliedByPort = [];
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

    internal async Task<AccountFrameCapStatus[]> SetGlobalLimitAsync(bool enabled, int fps)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidateSetting(fps);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _settings = new GlobalFrameCapSettings { Enabled = enabled, Fps = fps };
            PersistSettings();

            foreach ((int port, int processId) in _processByPort.Where(pair => pair.Value > 0).ToArray())
            {
                AccountFrameCapStatus status = await ApplyForAccountAsync(port, processId, _settings)
                    .ConfigureAwait(false);
                _statusesByPort[port] = status;
                CacheResult(port, processId, status, enabled ? fps : 0);
            }

            return StatusSnapshot();
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task<AccountFrameCapStatus[]> SyncAccountProcessesAsync(IReadOnlyDictionary<int, int> processByPort)
    {
        if (_disposed) return [];
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var incoming = processByPort
                .Where(pair => pair.Key > 0 && pair.Value >= 0)
                .ToDictionary(pair => pair.Key, pair => pair.Value);
            int[] ports = _processByPort.Keys.Concat(incoming.Keys).Distinct().ToArray();
            var changed = new List<AccountFrameCapStatus>();

            foreach (int port in ports)
            {
                int previousProcess = _processByPort.GetValueOrDefault(port);
                int currentProcess = incoming.GetValueOrDefault(port);
                if (currentProcess <= 0)
                {
                    _processByPort.Remove(port);
                    _appliedByPort.Remove(port);
                    if (previousProcess > 0 || !_statusesByPort.ContainsKey(port))
                    {
                        AccountFrameCapStatus waiting = CreateWaitingStatus(port, _settings, 0);
                        if (SetStatusIfChanged(waiting)) changed.Add(waiting);
                    }
                    continue;
                }

                _processByPort[port] = currentProcess;
                int desiredFps = _settings.Enabled ? _settings.Fps : 0;
                if (_appliedByPort.TryGetValue(port, out var applied)
                    && applied.ProcessId == currentProcess && applied.Fps == desiredFps)
                    continue;

                AccountFrameCapStatus status = await ApplyForAccountAsync(port, currentProcess, _settings)
                    .ConfigureAwait(false);
                CacheResult(port, currentProcess, status, desiredFps);
                if (SetStatusIfChanged(status)) changed.Add(status);
            }

            return changed.ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    private void CacheResult(int port, int processId, AccountFrameCapStatus status, int desiredFps)
    {
        if (status.Applied || status.State is "restart-required" or "error")
            _appliedByPort[port] = (processId, desiredFps);
        else
            _appliedByPort.Remove(port);
    }

    private async Task<AccountFrameCapStatus> ApplyForAccountAsync(
        int port,
        int processId,
        GlobalFrameCapSettings setting)
    {
        var command = new FrameCapClientSettings
        {
            Enabled = setting.Enabled,
            Fps = setting.Fps,
            RequestId = Guid.NewGuid().ToString("N"),
        };
        string settingsPath = Path.Combine(_accountDirectory, $"{port}-{processId}.json");
        string pluginPath = EnsurePlugin();
        FrameCapProcessResult result = await Task.Run(() =>
            FrameCapLiveInjector.Apply(processId, command, settingsPath, pluginPath, _dataRoot)).ConfigureAwait(false);
        bool applied = result.Success && (setting.Enabled ? result.State == "applied" : result.State == "disabled");
        if (!result.Success)
            _log($"Frame cap operation failed for account port {port}, PID {processId}: {result.Message}");
        return new AccountFrameCapStatus(port, setting.Enabled ? setting.Fps : 0, processId, applied, result.Message)
        {
            State = result.State,
        };
    }

    private static AccountFrameCapStatus CreateWaitingStatus(int port, GlobalFrameCapSettings setting, int processId)
    {
        string message = setting.Enabled ? "全局锁帧已开启，游戏启动后应用。" : "全局锁帧已关闭。";
        string state = setting.Enabled ? "waiting" : "disabled";
        return new AccountFrameCapStatus(port, setting.Enabled ? setting.Fps : 0, processId, !setting.Enabled, message)
        {
            State = state,
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
                    _settings = saved;
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
                };
                PersistSettings();
            }
        }
        catch (Exception ex)
        {
            _log("Global frame-cap settings could not be read: " + ex.GetBaseException().Message);
        }
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
