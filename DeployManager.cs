using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DeskrawlHubClient;

/// <summary>部署流程里的一步；state = pending | running | ok | failed | skipped。</summary>
internal sealed record DeployStep(string Id, string Title, string State, string Detail);

internal sealed record PortProbe(bool Ok, string Version, string Detail);
internal sealed record PortPairing(bool TokenFound, string HelperDirectory);
internal sealed record PortCandidate(int Port, int ProcessId, string ProcessPath, string Sandbox,
    PortProbe Probe, string[] Evidence, PortPairing Pairing);
internal sealed record PortSuggestion(string? AccountId, int Port);
internal sealed record DetectPortsResult(PortCandidate[] Candidates, PortSuggestion? Suggestion);

/// <summary>deploy-helper 的请求：sandbox 为空 = 主机；port&gt;0 = 手动指定控制端口；packagePath 非空 = 本地包/目录。</summary>
internal sealed record DeployHelperRequest(string Sandbox, int Port, string PackagePath, string PackageUrl, string Mode);

internal sealed record DeployHelperResult(bool Ok, DeployStep[] Steps, string AccountId, int Port, string Message);

/// <summary>部署过程中某一类可预期的失败；消息直接给用户看。</summary>
internal sealed class DeployStepException(string message) : Exception(message);

/// <summary>
/// 助手部署：探测端口归属、下载/解包官方助手包、落位、配对、启动、等待控制端口。
///
/// 下载源与校验规则来自助手自身的更新器（core/app/updater.py，1.54.x）：
///   · 更新清单 https://pub-a7fb98aa695440ce83c6954022a90bc6.r2.dev/deskrawl/update/latest.json
///   · 包地址必须落在同一个 r2.dev 下载空间，文件名必须等于清单的 folder + ".zip"
///   · 清单用 size + sha256 钉死安装包（签名由助手自己验；这里在 TLS 之上再核对 size/sha256）
///   · 安装包是 ZipCrypto 加密的，口令固定为 "x1x.tw"，用 Windows 自带的 tar.exe 解压
///   · 包内顶层目录名形如 X1X-Deskrawl-Helper-1.54.2-build26337088
///
/// 端口：助手只有 (8766+10i, 8765+10i) 这 12 组端口对（官方网页 CSP 也只放行 8766–8876），
/// 部署时由本类挑一组「control 与 api 都没人监听、且真的能 bind」的，用 --port 传给助手。
///
/// 已知限制（不要误以为已经验过）：
///   · **不校验更新清单的 Ed25519 签名**。清单可信度目前只靠 TLS、固定 r2.dev 下载域名、
///     以及清单里钉死的大小/SHA-256；助手自己的更新器会验签，中控这一层没有。
///   · 只查 IPv4 监听表（助手固定绑 127.0.0.1）。
///   · 沙箱账号的落位目录是宿主真实目录（%LOCALAPPDATA%\DeskrawlHubClient\helpers\<环境>\<版本>），
///     盒子内进程读到的是同一份文件，它写出的运行状态（ports.json / token / 日志）会被 Sandboxie
///     重定向进盒子，读取时必须走 SandboxRuntime.FileCandidates / ResolveFile。
/// </summary>
internal sealed class DeployManager : IDisposable
{
    internal const string ManifestUrl =
        "https://pub-a7fb98aa695440ce83c6954022a90bc6.r2.dev/deskrawl/update/latest.json";

    private static readonly string[] AllowedPackagePrefixes =
    [
        "https://pub-a7fb98aa695440ce83c6954022a90bc6.r2.dev/deskrawl/",
    ];

    private const string PackagePassword = "x1x.tw";
    private const long MaxManifestBytes = 64 * 1024;
    private const long MaxPackageBytes = 300L * 1024 * 1024;
    private const long MaxExtractedBytes = 2L * 1024 * 1024 * 1024;
    private const int MaxRedirects = 4;

    // 助手端口对：control = 8766 + 10 * i，api = control - 1，i = 0..11（deskrawl_helper.py 的 PORT_PAIRS）。
    // x1x.tw 面板的 CSP 只放行 8766–8876，所以部署时不给到 8886 以上。
    private const int HelperControlPortStart = 8766;
    private const int HelperPortPairCount = 12;
    private const int HelperControlPortLast = HelperControlPortStart + 10 * (HelperPortPairCount - 1);
    private static readonly TimeSpan PortWaitTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan PortPollInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);

    private static readonly Regex FolderPattern = new(
        @"^X1X-Deskrawl-Helper-(\d+\.\d+\.\d+)-build(\d+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly string _dataRoot;
    private readonly string _helperRoot;
    private readonly AccountWorkspace? _workspace;
    private readonly Action<string> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HttpClient _http;
    private readonly HttpClient _probe;

    internal DeployManager(string dataRoot, AccountWorkspace? workspace, Action<string> log)
    {
        _dataRoot = dataRoot;
        _helperRoot = Path.Combine(dataRoot, "helpers");
        _workspace = workspace;
        _log = log;
        _http = new HttpClient(new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        })
        {
            Timeout = TimeSpan.FromMinutes(5),
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("DeskrawlHubClient/1.2");
        _probe = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = ProbeTimeout };
    }

    private SandboxRuntime Sandbox => _workspace?.Sandbox
        ?? throw new DeployStepException("Sandboxie 运行环境尚未就绪，请稍后重试。");

    // ---------------------------------------------------------------- 探测端口

    /// <summary>
    /// 列出控制端口候选：监听端口表 + 进程路径 + 沙盘归属 + /control 探测 + 配对文件。
    /// sandbox 为空表示不限（返回全部），"主机"/"host" 表示只看主机，其它值是沙盘名。
    /// 证据如实分层：无 token 的 401 只能证明「这是助手」，归属必须靠配对令牌 + 带 token 的 200
    /// 且 instance.sandbox / instance.dir 对得上；探测没通过的候选也照样列出来（前端显示「未确认」）。
    /// </summary>
    internal async Task<DetectPortsResult> DetectPortsAsync(string sandbox, CancellationToken cancellationToken = default)
    {
        string box = NormalizeBox(sandbox);
        bool restrict = !string.IsNullOrWhiteSpace(sandbox) && !IsHostAlias(sandbox);
        Dictionary<int, int> listeners = SandboxRuntime.Listeners();
        var candidates = new List<PortCandidate>();

        foreach ((int port, int processId) in listeners.OrderBy(pair => pair.Key))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string processPath = SandboxRuntime.ProcessPath(processId);
            if (!AccountWorkspace.IsControlPort(port) && processPath.Length == 0) continue;

            string? processBox;
            try
            {
                processBox = Sandbox.BoxOf(processId);
            }
            catch (Exception ex) when (ex is InvalidOperationException or EntryPointNotFoundException)
            {
                processBox = null;
            }
            // null = 问不到 Sandboxie（不算主机）；"" = 确实在主机。
            if (processBox is null) continue;
            if (restrict && !string.Equals(processBox, box, StringComparison.OrdinalIgnoreCase)) continue;

            // 沙箱进程的镜像路径是逻辑路径，宿主上可能不存在：目录校验与令牌读取都要走 FileCandidates 映射。
            string logicalDirectory = HelperDirectoryOf(processPath);
            string helperDirectory = ResolveDirectory(logicalDirectory, processBox);
            bool helperProcess = IsHelperDirectoryResolved(logicalDirectory, processBox);
            bool tokenFound = TryReadToken(logicalDirectory, processBox, out string token);
            ProbeResult probe = await ProbeAsync(port, token, cancellationToken).ConfigureAwait(false);
            if (!helperProcess && !probe.HelperEndpoint) continue;

            bool identityMatches = probe.Probe.Ok && IdentityMatches(probe, processBox, helperDirectory, out _);
            var evidence = new List<string> { $"监听端口 {port}（PID {processId}）" };
            if (processPath.Length > 0) evidence.Add("进程 " + Path.GetFileName(processPath));
            evidence.Add(processBox.Length == 0 ? "属于主机" : "属于沙盘 " + processBox);
            if (helperProcess) evidence.Add("目录校验通过（pythonw.exe + deskrawl_helper.py）");
            else if (probe.HelperEndpoint) evidence.Add("端口应答了控制接口，说明是助手");
            if (tokenFound) evidence.Add("找到配对令牌");
            else if (!probe.Probe.Ok) evidence.Add("没找到配对令牌，无法确认归属");
            if (probe.Probe.Ok)
            {
                evidence.Add(probe.Probe.Version.Length > 0
                    ? $"带 token 探测 200：{probe.Probe.Version}"
                    : "带 token 探测 200");
                if (probe.InstanceSandbox is not null)
                    evidence.Add("助手报告沙箱 " + DisplayBox(probe.InstanceSandbox));
                evidence.Add(identityMatches ? "助手报告的目录与进程目录一致" : "助手报告的目录与进程目录不一致");
            }
            else
            {
                evidence.Add("探测未通过：" + probe.Probe.Detail);
            }

            candidates.Add(new PortCandidate(port, processId, processPath, processBox, probe.Probe, evidence.ToArray(),
                new PortPairing(tokenFound, helperDirectory)));
        }

        PortSuggestion? suggestion = BuildSuggestion(candidates, box, restrict);
        return new DetectPortsResult(candidates.OrderBy(c => c.Port).ToArray(), suggestion);
    }

    private static PortSuggestion? BuildSuggestion(List<PortCandidate> candidates, string box, bool restrict)
    {
        if (candidates.Count == 0) return null;
        var pool = candidates;
        if (restrict)
        {
            var sameBox = candidates
                .Where(c => string.Equals(c.Sandbox, box, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (sameBox.Length > 0) pool = [.. sameBox];
        }

        PortCandidate? best = pool
            .OrderByDescending(c => c.Probe.Ok)
            .ThenByDescending(c => c.Pairing.TokenFound)
            .ThenBy(c => c.Port)
            .FirstOrDefault();
        return best is null ? null : new PortSuggestion(AccountWorkspace.Id(best.Sandbox), best.Port);
    }

    private static string DisplayBox(string sandbox) => sandbox.Length == 0 ? "主机" : sandbox;

    private sealed record ProbeResult(PortProbe Probe, bool HelperEndpoint, string? InstanceSandbox,
        string InstanceDirectory);

    /// <summary>
    /// 带 token 探测 /control/status。只有 200 才是权威身份；401 / 421 / 403 只能证明「这是个助手」。
    /// </summary>
    private async Task<ProbeResult> ProbeAsync(int port, string token, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/control/status");
            if (token.Length > 0) request.Headers.TryAddWithoutValidation("X-Deskrawl-Token", token);
            request.Headers.TryAddWithoutValidation("Origin", "https://x1x.tw");
            using HttpResponseMessage response = await _probe.SendAsync(request, cancellationToken).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                ParseStatus(body, out string version, out string? instanceSandbox, out string instanceDirectory);
                return new ProbeResult(new PortProbe(true, version, "带 token 探测通过"), true, instanceSandbox,
                    instanceDirectory);
            }
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                return new ProbeResult(new PortProbe(false, "", "401：是助手 control 口，但没有配对令牌，无法确认归属"),
                    true, null, "");
            if (response.StatusCode == HttpStatusCode.MisdirectedRequest)
                return new ProbeResult(new PortProbe(false, "", "421：是助手，但 Host 头不对"), true, null, "");
            if (response.StatusCode == HttpStatusCode.Forbidden)
                return new ProbeResult(new PortProbe(false, "", "403：是助手，但 Origin 不允许"), true, null, "");
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)
                return new ProbeResult(new PortProbe(false, "", $"控制接口返回 {(int)response.StatusCode}，不是助手"),
                    false, null, "");
            return new ProbeResult(new PortProbe(false, "", $"控制接口返回 {(int)response.StatusCode}"), false, null, "");
        }
        catch (HttpRequestException)
        {
            return new ProbeResult(new PortProbe(false, "", "端口没有响应"), false, null, "");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ProbeResult(new PortProbe(false, "", "探测超时"), false, null, "");
        }
    }

    private static void ParseStatus(string body, out string version, out string? instanceSandbox,
        out string instanceDirectory)
    {
        version = "";
        instanceSandbox = null;
        instanceDirectory = "";
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            JsonElement root = document.RootElement;
            string raw = ReadString(root, "version");
            if (raw.Length > 0) version = "v" + raw;
            if (root.TryGetProperty("instance", out JsonElement instance) && instance.ValueKind == JsonValueKind.Object)
            {
                if (instance.TryGetProperty("sandbox", out JsonElement sandbox)
                    && sandbox.ValueKind == JsonValueKind.String)
                    instanceSandbox = sandbox.GetString() ?? "";
                instanceDirectory = ReadString(instance, "dir");
            }
        }
        catch (JsonException)
        {
            // 响应不是 JSON 时只当作「能连通」，版本与身份留空。
        }
    }

    /// <summary>
    /// 身份确认（唯一权威）：instance.sandbox 与目标环境一致，且 instance.dir（逻辑路径）与助手目录一致。
    /// 1.24.0 以前的助手没有 instance.sandbox，这时只能靠 token 200 判定。
    /// </summary>
    private bool IdentityMatches(ProbeResult probe, string box, string directory, out string reason)
    {
        reason = "";
        if (!probe.Probe.Ok)
        {
            reason = "控制接口没有通过配对探测";
            return false;
        }
        if (probe.InstanceSandbox is null) return true;
        if (!string.Equals(probe.InstanceSandbox, box, StringComparison.OrdinalIgnoreCase))
        {
            reason = $"助手报告的沙箱是「{DisplayBox(probe.InstanceSandbox)}」，不是本次目标";
            return false;
        }
        if (probe.InstanceDirectory.Length > 0 && directory.Length > 0)
        {
            string reported = PathKey(probe.InstanceDirectory, box);
            string expected = PathKey(directory, box);
            if (reported.Length > 0 && expected.Length > 0
                && !string.Equals(reported, expected, StringComparison.OrdinalIgnoreCase))
            {
                reason = "助手报告的目录与本次部署目录不一致";
                return false;
            }
        }
        return true;
    }

    /// <summary>比较用的路径键：逻辑/物理统一换回逻辑路径，去掉尾部分隔符并忽略大小写。</summary>
    private string PathKey(string path, string box)
    {
        if (path.Length == 0) return "";
        return TryLogicalPath(path, box).Replace('/', '\\').TrimEnd('\\').ToLowerInvariant();
    }

    /// <summary>逻辑路径换算；Sandboxie 不可用或换算失败时退回原路径（绝不让路径换算中断部署）。</summary>
    private string TryLogicalPath(string path, string box)
    {
        if (path.Length == 0 || box.Length == 0 || _workspace is null) return path;
        try
        {
            return Sandbox.LogicalPath(path, box);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or DeployStepException)
        {
            return path;
        }
    }

    // ---------------------------------------------------------------- 端口对分配

    /// <summary>
    /// 从 8766–8876 的 12 组端口对里挑一组 control 与 api 都空闲的。全被占用返回 null。
    /// 判据：监听表里没有该端口，且真实 TcpListener 能 bind（Windows/Hyper-V 保留端口不应答但 bind 会失败）。
    /// </summary>
    private static (int Control, int Api)? AllocatePortPair()
    {
        Dictionary<int, int> listeners = SandboxRuntime.Listeners();
        for (int index = 0; index < HelperPortPairCount; index++)
        {
            int control = HelperControlPortStart + index * 10;
            int api = control - 1;
            if (listeners.ContainsKey(control) || listeners.ContainsKey(api)) continue;
            if (!CanBind(control) || !CanBind(api)) continue;
            return (control, api);
        }
        return null;
    }

    private static bool CanBind(int port)
    {
        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    /// <summary>手动指定的端口优先，否则自动分配；分配不到就明确报「端口被占用」。</summary>
    private (int Control, string Detail) ResolvePortPair(int requestedPort)
    {
        Dictionary<int, int> listeners = SandboxRuntime.Listeners();
        if (requestedPort > 0)
        {
            int api = requestedPort - 1;
            if (requestedPort < 2)
                throw new DeployStepException("指定的端口无效：" + requestedPort);
            if (listeners.ContainsKey(requestedPort) || listeners.ContainsKey(api)
                || !CanBind(requestedPort) || !CanBind(api))
                throw new DeployStepException(
                    $"端口被占用：指定的控制端口 {requestedPort}（api {api}）已经在监听或被 Windows 保留，换一对端口，或先关掉占用它的程序。");
            string range = requestedPort is >= HelperControlPortStart and <= HelperControlPortLast
                ? ""
                : $"（注意：{requestedPort} 超出官网面板支持范围 8766–8876，网页控制台可能连不上）";
            return (requestedPort, $"控制端口对 {requestedPort}/{api}{range}。");
        }

        (int Control, int Api)? pair = AllocatePortPair();
        if (pair is null)
            throw new DeployStepException(
                "端口被占用：8766–8876 这 12 组端口对全被其他程序或 Windows 保留。请关闭不用的助手；若是 Hyper-V / WSL / Docker 保留，可用 netsh interface ipv4 show excludedportrange protocol=tcp 查看。");
        return (pair.Value.Control, $"自动分配控制端口对 {pair.Value.Control}/{pair.Value.Api}（8766–8876）。");
    }

    // ---------------------------------------------------------------- 部署助手

    internal async Task<DeployHelperResult> DeployHelperAsync(DeployHelperRequest request,
        Action<DeployStep[]>? progress, CancellationToken cancellationToken = default)
    {
        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            DeployStep[] busy = CreateSteps();
            busy[0] = busy[0] with { State = "failed", Detail = "另一个部署任务还在进行，请等它结束后再试。" };
            for (int index = 1; index < busy.Length; index++)
                busy[index] = busy[index] with { State = "skipped", Detail = "上一个部署任务还没结束。" };
            return new DeployHelperResult(false, busy, AccountWorkspace.Id(NormalizeBox(request.Sandbox)), 0,
                "另一个部署任务还在进行，请等它结束后再试。");
        }

        var book = new StepBook(CreateSteps(), progress);
        string box = NormalizeBox(request.Sandbox);
        string accountId = AccountWorkspace.Id(box);
        string staging = "";
        int port = 0;
        try
        {
            // 1. 环境检查
            book.Begin("precheck");
            ValidateSandbox(box);
            await EnsureNotOccupiedAsync(accountId, box, request.Port).ConfigureAwait(false);
            book.Ok("precheck", box.Length == 0 ? "主机环境检查通过。" : $"沙盘「{box}」检查通过。");

            // 2. 准备安装包（本地包 / 下载清单）
            book.Begin("package");
            HelperPackage package = await ResolvePackageAsync(request, box, book, cancellationToken).ConfigureAwait(false);
            book.Publish();

            // 3. 下载
            book.Begin("download");
            if (package.ZipPath.Length == 0)
            {
                book.Skip("download", "使用本地目录，无需下载。");
            }
            else if (package.DownloadUrl.Length == 0)
            {
                book.Skip("download", "使用本地安装包，无需下载。");
            }
            else
            {
                string downloaded = await DownloadAsync(package, book, cancellationToken).ConfigureAwait(false);
                package = package with { ZipPath = downloaded };
                book.Ok("download", $"已下载 {FormatSize(package.Size)}，SHA-256 校验通过。");
            }

            // 4. 解包
            book.Begin("extract");
            if (package.SourceDirectory.Length > 0)
            {
                book.Skip("extract", "使用本地目录，无需解包。");
            }
            else
            {
                staging = Path.Combine(_helperRoot, ".staging",
                    package.Folder + "-" + Guid.NewGuid().ToString("N")[..8]);
                Directory.CreateDirectory(staging);
                int files = await ExtractPackageAsync(package, staging, cancellationToken).ConfigureAwait(false);
                book.Ok("extract", $"解包 {files} 个文件，目录结构检查通过。");
            }

            // 5. 落位
            book.Begin("install");
            string installDirectory;
            if (package.SourceDirectory.Length > 0)
            {
                // 用户给的本地目录已经是完整的助手目录：直接用它，不搬动（避免破坏用户自己的安装）。
                installDirectory = package.SourceDirectory;
                book.Ok("install", "使用本地助手目录：" + installDirectory);
            }
            else
            {
                installDirectory = await LandAsync(package, staging, book, cancellationToken).ConfigureAwait(false);
            }
            book.Publish();

            // 6. 配对
            book.Begin("pair");
            string token = EnsurePairingToken(installDirectory, box, out bool generated);
            book.Ok("pair", generated ? "已生成新的配对令牌。" : "沿用已有的配对令牌。");

            // 7. 启动：先挑一组空闲端口对（8766–8876），再 --port 交给助手
            book.Begin("launch");
            (int controlPort, string portDetail) = ResolvePortPair(request.Port);
            await LaunchAsync(installDirectory, box, accountId, controlPort, book, cancellationToken)
                .ConfigureAwait(false);
            book.Ok("launch", (box.Length == 0
                ? "已提交主机助手启动请求。"
                : $"已在沙盘「{box}」里提交助手启动请求。") + portDetail);

            // 8. 等控制端口（并确认身份：instance.sandbox / instance.dir 要对得上）
            book.Begin("port");
            port = await WaitForPortAsync(installDirectory, box, token, controlPort, cancellationToken)
                .ConfigureAwait(false);
            book.Ok("port", $"控制端口 {port}（api {port - 1}）已就绪，身份确认通过。");

            // 9. 写回账号档案
            book.Begin("register");
            accountId = await RegisterAsync(installDirectory, box, accountId).ConfigureAwait(false);
            book.Ok("register", "已写回账号档案。");

            string message = (box.Length == 0 ? "主机助手" : $"沙盘「{box}」的助手") + $"已部署完成，控制端口 {port}。";
            _log($"[deploy] {message} dir={installDirectory}");
            return new DeployHelperResult(true, book.Snapshot(), accountId, port, message);
        }
        catch (DeployStepException ex)
        {
            book.FailCurrent(ex.Message);
            _log("[deploy] failed: " + ex.Message);
            return new DeployHelperResult(false, book.Snapshot(), accountId, port, ex.Message);
        }
        catch (OperationCanceledException)
        {
            book.FailCurrent("部署已取消。");
            return new DeployHelperResult(false, book.Snapshot(), accountId, port, "部署已取消。");
        }
        catch (Exception ex)
        {
            string message = "部署失败：" + ex.GetBaseException().Message;
            book.FailCurrent(message);
            _log("[deploy] failed: " + ex);
            return new DeployHelperResult(false, book.Snapshot(), accountId, port, message);
        }
        finally
        {
            CleanupStaging(staging);
            _gate.Release();
        }
    }

    private void ValidateSandbox(string box)
    {
        if (box.Length == 0) return;
        if (!Sandbox.Available || Sandbox.StartPath.Length == 0)
            throw new DeployStepException("没有检测到 Sandboxie（SbieDll.dll / Start.exe）。请先安装并启动 Sandboxie-Plus，再部署到沙盘。");
        string[] boxes = Sandbox.Boxes();
        if (!boxes.Contains(box, StringComparer.OrdinalIgnoreCase))
            throw new DeployStepException($"Sandboxie 里没有名为「{box}」的沙盘，请先在 Sandboxie 中创建它。");
    }

    /// <summary>已有实例占用：同一账号的助手在跑、或目标端口上已经有助手在听。</summary>
    private async Task EnsureNotOccupiedAsync(string accountId, string box, int requestedPort)
    {
        // 先刷新一次，避免拿过期的快照判断「有没有在跑」，把正在运行的助手覆盖掉。
        WorkspaceAccount[] accounts = _workspace is null
            ? []
            : await _workspace.RefreshAsync().ConfigureAwait(false);
        WorkspaceAccount? account = accounts.FirstOrDefault(a =>
            string.Equals(a.Id, accountId, StringComparison.OrdinalIgnoreCase));
        if (account is { HelperRunning: true })
            throw new DeployStepException("已有实例占用：这个环境的助手正在运行。请先在助手页面关闭它，再部署。");

        if (requestedPort <= 0) return;
        Dictionary<int, int> listeners = SandboxRuntime.Listeners();
        if (!listeners.TryGetValue(requestedPort, out int pid)) return;
        string path = SandboxRuntime.ProcessPath(pid);
        string pidBox;
        try
        {
            pidBox = _workspace is null ? box : Sandbox.BoxOf(pid) ?? box;
        }
        catch (Exception ex) when (ex is InvalidOperationException or EntryPointNotFoundException or DeployStepException)
        {
            pidBox = box;
        }
        if (IsHelperDirectoryResolved(HelperDirectoryOf(path), pidBox) || AccountWorkspace.IsControlPort(requestedPort))
            throw new DeployStepException($"已有实例占用：端口 {requestedPort} 上已经有助手在监听（PID {pid}）。");
    }

    private async Task<HelperPackage> ResolvePackageAsync(DeployHelperRequest request, string box, StepBook book,
        CancellationToken cancellationToken)
    {
        string local = (request.PackagePath ?? "").Trim().Trim('"');
        if (local.Length > 0)
        {
            if (Directory.Exists(local))
            {
                string? localFolder = FindHelperFolder(local);
                if (localFolder is null)
                    throw new DeployStepException("指定的目录不是助手目录：需要同时包含 core\\app\\deskrawl_helper.py 和 runtime\\pythonw.exe。");
                book.Ok("package", "使用本地助手目录：" + localFolder);
                return new HelperPackage(box, Path.GetFileName(localFolder), "", "", "", 0, localFolder);
            }
            if (!File.Exists(local))
                throw new DeployStepException("找不到指定的安装包：" + local);
            if (!local.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                throw new DeployStepException("本地安装包需要是 .zip，或者是解压好的助手目录。");
            string? folderFromZip = TryReadFolderFromZip(local);
            string folder = folderFromZip ?? "X1X-Deskrawl-Helper-local";
            long size = new FileInfo(local).Length;
            book.Ok("package", $"使用本地安装包：{Path.GetFileName(local)}（{FormatSize(size)}）");
            return new HelperPackage(box, folder, local, "", "", size, "");
        }

        // 官方更新清单
        string manifestAddress = string.IsNullOrWhiteSpace(request.PackageUrl) ? ManifestUrl : request.PackageUrl.Trim();
        HelperManifestInfo manifest = await FetchManifestAsync(manifestAddress, request.Mode, cancellationToken)
            .ConfigureAwait(false);
        book.Ok("package", $"官方更新源：助手 {manifest.Version}（{manifest.Folder}，{FormatSize(manifest.Size)}）");
        return new HelperPackage(box, manifest.Folder, "", manifest.ZipUrl, manifest.Sha256, manifest.Size, "");
    }

    private async Task<HelperManifestInfo> FetchManifestAsync(string address, string mode,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out Uri? uri))
            throw new DeployStepException("更新源地址无效：" + address);

        string separator = address.Contains('?') ? "&" : "?";
        string requestAddress = address + separator + "t=" + DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string body;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, requestAddress);
            request.Headers.TryAddWithoutValidation("Cache-Control", "no-cache");
            using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new DeployStepException($"取更新信息失败：服务器返回 HTTP {(int)response.StatusCode}。请稍后重试，或改用本地安装包。");
            await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (buffer.Length > MaxManifestBytes)
                throw new DeployStepException("更新信息太大，已拒绝。");
            body = Encoding.UTF8.GetString(buffer.ToArray());
        }
        catch (DeployStepException) { throw; }
        catch (HttpRequestException ex)
        {
            throw new DeployStepException("连不到更新服务器：" + ex.GetBaseException().Message
                + "。请检查网络，或改用本地安装包。");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new DeployStepException("连接更新服务器超时。请检查网络，或改用本地安装包。");
        }

        HelperManifestInfo manifest = ParseManifest(body);
        if (mode.Length > 0 && mode.Equals("local", StringComparison.OrdinalIgnoreCase))
            throw new DeployStepException("当前是本地安装包模式，但请求里没有给出 packagePath。");
        return manifest;
    }

    /// <summary>按助手更新器的规则校验清单：产品、版本、目录名、下载地址前缀、大小、哈希。</summary>
    private static HelperManifestInfo ParseManifest(string body)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            throw new DeployStepException("更新信息格式不对，已拒绝。");
        }

        using (document)
        {
            if (!document.RootElement.TryGetProperty("manifest", out JsonElement manifest)
                || manifest.ValueKind != JsonValueKind.Object)
                throw new DeployStepException("更新信息缺少 manifest 段，已拒绝。");

            string product = ReadString(manifest, "product");
            string version = ReadString(manifest, "version");
            string folder = ReadString(manifest, "folder");
            string zipUrl = ReadString(manifest, "zip_url");
            string sha256 = ReadString(manifest, "sha256");
            long size = manifest.TryGetProperty("size", out JsonElement sizeElement)
                && sizeElement.TryGetInt64(out long sizeValue) ? sizeValue : 0;

            if (!string.Equals(product, "x1x-deskrawl-helper", StringComparison.Ordinal))
                throw new DeployStepException("更新信息不是这个助手的，已拒绝。");
            Match match = FolderPattern.Match(folder);
            if (!match.Success || !string.Equals(match.Groups[1].Value, version, StringComparison.Ordinal))
                throw new DeployStepException("更新信息里的目录名不对，已拒绝。");
            if (size <= 0 || size > MaxPackageBytes)
                throw new DeployStepException("更新信息里的安装包大小不合理，已拒绝。");
            if (!Regex.IsMatch(sha256, "^[0-9a-f]{64}$"))
                throw new DeployStepException("更新信息里的 SHA-256 格式不对，已拒绝。");
            if (!Uri.TryCreate(zipUrl, UriKind.Absolute, out Uri? zipUri)
                || zipUri.Scheme != Uri.UriSchemeHttps
                || !AllowedPackagePrefixes.Any(prefix => zipUrl.StartsWith(prefix, StringComparison.Ordinal))
                || !zipUrl.EndsWith(folder + ".zip", StringComparison.Ordinal))
                throw new DeployStepException("更新信息里的下载地址不在 X1X 的下载空间，已拒绝。");

            return new HelperManifestInfo(version, folder, zipUrl, sha256, size);
        }
    }

    private async Task<string> DownloadAsync(HelperPackage package, StepBook book, CancellationToken cancellationToken)
    {
        string directory = Path.Combine(_helperRoot, "packages");
        Directory.CreateDirectory(directory);
        string destination = Path.Combine(directory, package.Folder + ".zip");
        if (File.Exists(destination) && new FileInfo(destination).Length == package.Size
            && await HashMatchesAsync(destination, package.Sha256, cancellationToken).ConfigureAwait(false))
        {
            book.Publish();
            return destination;
        }

        string part = destination + ".part";
        if (File.Exists(part)) File.Delete(part);

        string current = package.DownloadUrl;
        for (int hop = 0; hop <= MaxRedirects; hop++)
        {
            if (!Uri.TryCreate(current, UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttps)
                throw new DeployStepException("下载地址不是 https，已停止。");
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            using HttpResponseMessage response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (IsRedirect(response.StatusCode))
            {
                string? location = response.Headers.Location?.ToString();
                if (location is null)
                    throw new DeployStepException($"下载时服务器返回 {(int)response.StatusCode}，但没有给出跳转地址。");
                current = location.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                    ? location : new Uri(uri, location).ToString();
                if (!AllowedPackagePrefixes.Any(prefix => current.StartsWith(prefix, StringComparison.Ordinal)))
                    throw new DeployStepException("下载被跳转到非官方地址，已停止。");
                continue;
            }

            if (response.StatusCode == HttpStatusCode.NotFound)
                throw new DeployStepException("服务器上没有这个安装包（HTTP 404），请稍后再试。");
            if (!response.IsSuccessStatusCode)
                throw new DeployStepException($"下载安装包失败：服务器返回 HTTP {(int)response.StatusCode}。");

            await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var target = new FileStream(part, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                1024 * 128, useAsync: true);
            byte[] buffer = new byte[1024 * 128];
            long total = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                total += read;
                if (total > MaxPackageBytes)
                    throw new DeployStepException("安装包超过允许的大小上限，已停止下载。");
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
            await target.FlushAsync(cancellationToken).ConfigureAwait(false);
            if (package.Size > 0 && total != package.Size)
            {
                File.Delete(part);
                throw new DeployStepException($"下载的安装包大小不对（{FormatSize(total)}，应为 {FormatSize(package.Size)}），已丢弃。");
            }
            break;
        }

        if (!File.Exists(part))
            throw new DeployStepException("下载时跳转次数过多，已停止。请稍后重试。");
        if (!await HashMatchesAsync(part, package.Sha256, cancellationToken).ConfigureAwait(false))
        {
            File.Delete(part);
            throw new DeployStepException("下载的安装包 SHA-256 与更新信息不符，已丢弃；请稍后重试。");
        }
        File.Move(part, destination, overwrite: true);
        return destination;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new DeployStepException("下载失败（网络）：" + ex.GetBaseException().Message + "。请检查网络后重试。");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new DeployStepException("下载超时。请检查网络后重试，或改用本地安装包。");
        }
    }

    private static bool IsRedirect(HttpStatusCode status) =>
        status is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    private static async Task<bool> HashMatchesAsync(string path, string expected, CancellationToken cancellationToken)
    {
        if (expected.Length == 0) return true;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 128, useAsync: true);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant() == expected.ToLowerInvariant();
    }

    /// <summary>解包：普通 zip 用 .NET；加密（ZipCrypto）包用 Windows 自带 tar.exe，并逐条校验条目路径。</summary>
    private async Task<int> ExtractPackageAsync(HelperPackage package, string staging,
        CancellationToken cancellationToken)
    {
        try
        {
            return ExtractWithZipArchive(package.ZipPath, staging, package.Folder);
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or IOException)
        {
            // ZipCrypto 加密包（官方发布包就是这样），.NET 读不出内容：换 Windows 自带的 tar.exe。
            // 注意 DeployStepException（路径穿越等安全检查）不在这里被吞掉。
            ResetDirectory(staging);
        }

        return await ExtractWithTarAsync(package.ZipPath, staging, package.Folder, cancellationToken)
            .ConfigureAwait(false);
    }

    private static int ExtractWithZipArchive(string zipPath, string staging, string folder)
    {
        using ZipArchive archive = ZipFile.OpenRead(zipPath);
        string root = Path.GetFullPath(staging) + Path.DirectorySeparatorChar;
        long total = 0;
        int count = 0;
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            string name = entry.FullName.Replace('\\', '/');
            if (name.EndsWith("/", StringComparison.Ordinal)) continue;
            ValidateEntryName(name, folder);
            string destination = Path.GetFullPath(Path.Combine(staging, name.Replace('/', Path.DirectorySeparatorChar)));
            if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new DeployStepException($"安装包里的路径尝试越界：{name}");
            total += entry.Length;
            if (total > MaxExtractedBytes)
                throw new DeployStepException("安装包解压后超过允许的大小上限，已停止。");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, overwrite: true);
            count++;
        }
        VerifyExtracted(staging, folder);
        return count;
    }

    private async Task<int> ExtractWithTarAsync(string zipPath, string staging, string folder,
        CancellationToken cancellationToken)
    {
        string tar = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "tar.exe");
        if (!File.Exists(tar))
            throw new DeployStepException("系统里没有 tar.exe（需要 Windows 10 1803 以上），无法解开加密的助手安装包。");

        (int listCode, string listing, string listError) =
            await RunAsync(tar, ["-tf", zipPath], cancellationToken).ConfigureAwait(false);
        if (listCode != 0)
            throw new DeployStepException("读取安装包目录失败：" + FirstLine(listError));

        string[] names = listing.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim().TrimEnd('\r'))
            .Where(line => line.Length > 0).ToArray();
        if (names.Length == 0)
            throw new DeployStepException("安装包是空的，已拒绝。");
        foreach (string name in names)
        {
            if (name.EndsWith("/", StringComparison.Ordinal)) continue;
            ValidateEntryName(name.Replace('\\', '/'), folder);
        }

        Directory.CreateDirectory(staging);
        (int code, _, string error) = await RunAsync(tar,
            ["-xf", zipPath, "-C", staging, "--passphrase", PackagePassword], cancellationToken)
            .ConfigureAwait(false);
        if (code != 0)
            throw new DeployStepException("解压安装包失败：" + FirstLine(error));
        VerifyExtracted(staging, folder);
        return names.Count(name => !name.EndsWith("/", StringComparison.Ordinal));
    }

    /// <summary>包内条目必须都在 &lt;folder&gt;/ 下面，拒绝绝对路径、.. 和别的一级目录。</summary>
    private static void ValidateEntryName(string name, string folder)
    {
        if (name.Length == 0) throw new DeployStepException("安装包里有空文件名，已拒绝。");
        if (name.StartsWith("/", StringComparison.Ordinal) || name.Contains(':', StringComparison.Ordinal))
            throw new DeployStepException($"安装包里有绝对路径：{name}，已拒绝。");
        string[] segments = name.Split('/');
        if (segments.Any(segment => segment == ".."))
            throw new DeployStepException($"安装包里的路径尝试越界：{name}，已拒绝。");
        if (!string.Equals(segments[0], folder, StringComparison.OrdinalIgnoreCase))
            throw new DeployStepException($"安装包里的文件不在 {folder}\\ 下面：{name}，已拒绝。");
    }

    private static void VerifyExtracted(string staging, string folder)
    {
        string directory = Path.Combine(staging, folder);
        if (!File.Exists(Path.Combine(directory, @"core\app\deskrawl_helper.py"))
            || !File.Exists(Path.Combine(directory, @"runtime\pythonw.exe")))
            throw new DeployStepException("安装包内容不完整：缺少 core\\app\\deskrawl_helper.py 或 runtime\\pythonw.exe。");
    }

    private async Task<string> LandAsync(HelperPackage package, string staging, StepBook book,
        CancellationToken cancellationToken)
    {
        string source = package.SourceDirectory.Length > 0
            ? package.SourceDirectory
            : Path.Combine(staging, package.Folder);
        string key = SafeName(package.Box.Length == 0 ? "host" : package.Box);
        string parent = Path.Combine(_helperRoot, key);
        Directory.CreateDirectory(parent);
        string target = Path.Combine(parent, package.Folder);
        book.Publish();

        if (Directory.Exists(target))
        {
            if (IsHelperDirectory(target))
            {
                book.Ok("install", "目标目录已有同版本助手，直接复用：" + target);
                return target;
            }
            string backup = target + ".old-" + DateTime.Now.ToString("yyyyMMddHHmmss");
            Directory.Move(target, backup);
            _log("[deploy] 目标目录不是完整助手，已改名备份：" + backup);
        }

        if (string.Equals(Path.GetFullPath(Path.GetDirectoryName(source) ?? ""),
            Path.GetFullPath(parent), StringComparison.OrdinalIgnoreCase))
        {
            Directory.Move(source, target);
        }
        else
        {
            await CopyDirectoryAsync(source, target, cancellationToken).ConfigureAwait(false);
        }

        if (!IsHelperDirectory(target))
            throw new DeployStepException("落位后的目录不完整：" + target);
        Directory.CreateDirectory(Path.Combine(target, @"core\runtime"));
        book.Ok("install", "已落位到 " + target);
        return target;
    }

    private static async Task CopyDirectoryAsync(string source, string target, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(target);
        foreach (string directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, directory)));
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string destination = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read,
                1024 * 128, useAsync: true);
            await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None,
                1024 * 128, useAsync: true);
            await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
        }
    }
    /// <summary>
    /// 配对令牌：已有合法令牌就沿用（沙箱里可能被重定向，读取走 FileCandidates）；
    /// 没有就在我们落位的真实目录里生成一个 64 位十六进制令牌。
    /// </summary>
    private string EnsurePairingToken(string directory, string box, out bool generated)
    {
        string logicalPath = Path.Combine(directory, @"core\helper\runtime\token");
        string existingPath = ResolveExisting(logicalPath, box);
        if (File.Exists(existingPath))
        {
            string existing = File.ReadAllText(existingPath).Trim();
            if (Regex.IsMatch(existing, "^[0-9a-fA-F]{64}$"))
            {
                generated = false;
                return existing.ToLowerInvariant();
            }
        }

        string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        Directory.CreateDirectory(Path.GetDirectoryName(logicalPath)!);
        File.WriteAllText(logicalPath, token + Environment.NewLine, new UTF8Encoding(false));
        generated = true;
        return token;
    }

    /// <summary>
    /// 启动助手：主机走 /dfp，沙箱走 /box:&lt;名&gt; /hide_window（都封装在 SandboxRuntime.Start 里）。
    /// 实参逐项进 ArgumentList：pythonw.exe + deskrawl_helper.py --open none --port &lt;control&gt;。
    /// </summary>
    private async Task LaunchAsync(string directory, string box, string accountId, int controlPort, StepBook book,
        CancellationToken cancellationToken)
    {
        WorkspaceAccount[] accounts = _workspace is null ? [] : await _workspace.RefreshAsync().ConfigureAwait(false);
        WorkspaceAccount? account = accounts.FirstOrDefault(a =>
            string.Equals(a.Id, accountId, StringComparison.OrdinalIgnoreCase));
        if (account is { HelperRunning: true })
        {
            book.Skip("launch", "这个环境的助手已经在运行，没有重复启动。");
            return;
        }

        string pythonw = Path.Combine(directory, @"runtime\pythonw.exe");
        string script = Path.Combine(directory, @"core\app\deskrawl_helper.py");
        if (!File.Exists(pythonw) || !File.Exists(script))
            throw new DeployStepException("落位后的助手缺少 runtime\\pythonw.exe 或 core\\app\\deskrawl_helper.py。");

        try
        {
            using Process started = Sandbox.Start(box, pythonw,
                [script, "--open", "none", "--port", controlPort.ToString(CultureInfo.InvariantCulture)],
                hidden: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            throw new DeployStepException("启动助手失败：" + ex.GetBaseException().Message);
        }

        book.Ok("launch", (box.Length == 0
            ? "已提交主机助手启动请求。"
            : $"已在沙盘「{box}」里提交助手启动请求。") + $"控制端口 {controlPort}。");
        await Task.Delay(400, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>等控制端口：每 500ms 探测一次，最多 60 秒；200 之后还要过身份确认（沙箱 + 目录）。</summary>
    private async Task<int> WaitForPortAsync(string directory, string box, string token, int controlPort,
        CancellationToken cancellationToken)
    {
        DateTime deadline = DateTime.UtcNow + PortWaitTimeout;
        var observed = new List<int>();
        string mismatch = "";
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (int candidate in CandidatePorts(directory, box, controlPort))
            {
                if (!observed.Contains(candidate)) observed.Add(candidate);
                ProbeResult probe = await ProbeAsync(candidate, token, cancellationToken).ConfigureAwait(false);
                if (!probe.Probe.Ok) continue;
                if (IdentityMatches(probe, box, directory, out string reason)) return candidate;
                mismatch = reason;
            }
            await Task.Delay(PortPollInterval, cancellationToken).ConfigureAwait(false);
        }

        if (mismatch.Length > 0)
            throw new DeployStepException("连到了别的助手：" + mismatch + "。已停止，请刷新后重试。");
        string hint = observed.Count > 0
            ? $"探测过的端口：{string.Join("、", observed.Select(port => port.ToString(CultureInfo.InvariantCulture)))}。"
            : "";
        throw new DeployStepException(
            $"助手启动后 {PortWaitTimeout.TotalSeconds:0} 秒内没有响应控制接口。{hint}常见原因是杀毒软件拦截、缺少文件或端口被占用；也可以手动指定端口后重试。");
    }

    /// <summary>候选端口：分配/指定的 control 口 &gt; ports.json 里的 control &gt; 属于这份助手目录的监听端口。</summary>
    private IEnumerable<int> CandidatePorts(string directory, string box, int controlPort)
    {
        var result = new List<int>();
        if (controlPort > 0) result.Add(controlPort);

        try
        {
            string portsFile = ResolveExisting(Path.Combine(directory, @"core\runtime\ports.json"), box);
            if (File.Exists(portsFile))
            {
                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(portsFile));
                if (document.RootElement.TryGetProperty("control", out JsonElement value)
                    && value.TryGetInt32(out int port) && port > 0 && !result.Contains(port))
                    result.Add(port);
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // ports.json 还没写出来是正常的启动中间态。
        }

        Dictionary<int, int> listeners = SandboxRuntime.Listeners();
        foreach ((int port, int pid) in listeners)
        {
            if (result.Contains(port)) continue;
            string path = SandboxRuntime.ProcessPath(pid);
            if (path.Length == 0) continue;
            string logicalDirectory = HelperDirectoryOf(path);
            if (!IsHelperDirectoryResolved(logicalDirectory, box)) continue;
            string owner = PathKey(ResolveDirectory(logicalDirectory, box), box);
            if (owner != PathKey(directory, box) && PathKey(logicalDirectory, box) != PathKey(directory, box)) continue;
            result.Add(port);
        }
        return result;
    }

    /// <summary>把部署好的目录写回账号档案（配对指纹一并刷新）。</summary>
    private async Task<string> RegisterAsync(string directory, string box, string accountId)
    {
        if (_workspace is null) throw new DeployStepException("账号环境尚未就绪，无法写回助手目录。");
        // 沙箱账号用逻辑路径存档案，AccountWorkspace 之后会用 ResolveFile 映射回物理文件。
        string logical = box.Length == 0 ? directory : SafeLogicalPath(directory, box);
        await _workspace.RecordDeployedHelperAsync(accountId, logical).ConfigureAwait(false);
        await _workspace.RefreshAsync(true).ConfigureAwait(false);
        WorkspaceAccount[] accounts = _workspace.AccountsSnapshot();
        // 隐藏起来的账号不会出现在快照里，这不影响部署结果：档案已经写好了。
        WorkspaceAccount? account = accounts.FirstOrDefault(a =>
            string.Equals(a.Id, accountId, StringComparison.OrdinalIgnoreCase));
        if (account is null)
            _log("[deploy] 账号档案已写入，但这个账号暂时不在快照里（可能被隐藏）：" + accountId);
        return account?.Id ?? accountId;
    }

    // ---------------------------------------------------------------- 小工具

    private string SafeLogicalPath(string path, string box) => TryLogicalPath(path, box);


    private bool TryReadToken(string directory, string box, out string token)
    {
        token = "";
        if (directory.Length == 0) return false;
        try
        {
            string path = ResolveExisting(Path.Combine(directory, @"core\helper\runtime\token"), box);
            if (!File.Exists(path)) return false;
            string value = File.ReadAllText(path).Trim();
            if (!Regex.IsMatch(value, "^[0-9a-fA-F]{64}$")) return false;
            token = value.ToLowerInvariant();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>把「沙盘里的逻辑目录」还原成真实目录（读得到 + 被重定向两种都试）。</summary>
    private string ResolveDirectory(string logical, string box)
    {
        if (logical.Length == 0 || Directory.Exists(logical) || box.Length == 0 || _workspace is null) return logical;
        try
        {
            foreach (string candidate in Sandbox.FileCandidates(logical, box))
                if (Directory.Exists(candidate)) return candidate;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or DeployStepException)
        {
            // 拿不到沙盘文件根时用逻辑路径。
        }
        return logical;
    }

    private string ResolveExisting(string logical, string box)
    {
        if (_workspace is null) return logical;
        try
        {
            return Sandbox.ResolveFile(logical, box);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or DeployStepException)
        {
            return logical;
        }
    }

    /// <summary>
    /// 是不是助手目录：逻辑路径与 FileCandidates 映射出的物理路径都试一遍。
    /// 沙箱进程的镜像路径是逻辑路径，直接 File.Exists 永远为假。
    /// </summary>
    private bool IsHelperDirectoryResolved(string directory, string box)
    {
        if (directory.Length == 0) return false;
        foreach (string candidate in DirectoryCandidates(directory, box))
        {
            try
            {
                if (File.Exists(Path.Combine(candidate, @"core\app\deskrawl_helper.py"))
                    && File.Exists(Path.Combine(candidate, @"runtime\pythonw.exe")))
                    return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // 路径形态异常时跳过这个候选。
            }
        }
        return false;
    }

    private List<string> DirectoryCandidates(string directory, string box)
    {
        var candidates = new List<string> { directory };
        if (box.Length == 0 || _workspace is null) return candidates;
        try
        {
            candidates.AddRange(Sandbox.FileCandidates(directory, box)
                .Where(path => !string.Equals(path, directory, StringComparison.OrdinalIgnoreCase)));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or DeployStepException)
        {
            // 拿不到沙盘文件根时只用逻辑路径。
        }
        return candidates;
    }

    /// <summary>&lt;dir&gt;\runtime\pythonw.exe → &lt;dir&gt;。</summary>
    private static string HelperDirectoryOf(string processPath)
    {
        if (processPath.Length == 0) return "";
        string runtime = Path.GetDirectoryName(processPath) ?? "";
        if (string.Equals(Path.GetFileName(runtime), "runtime", StringComparison.OrdinalIgnoreCase))
            return Path.GetDirectoryName(runtime) ?? "";
        return Path.GetDirectoryName(runtime) ?? "";
    }

    private static bool IsHelperDirectory(string directory) =>
        File.Exists(Path.Combine(directory, @"core\app\deskrawl_helper.py"))
        && File.Exists(Path.Combine(directory, @"runtime\pythonw.exe"));

    private static string? FindHelperFolder(string root)
    {
        if (IsHelperDirectory(root)) return Path.GetFullPath(root);
        try
        {
            foreach (string directory in Directory.EnumerateDirectories(root, "X1X-Deskrawl-Helper-*"))
                if (IsHelperDirectory(directory)) return Path.GetFullPath(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        return null;
    }

    private static string? TryReadFolderFromZip(string path)
    {
        try
        {
            using ZipArchive archive = ZipFile.OpenRead(path);
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                string name = entry.FullName.Replace('\\', '/');
                int slash = name.IndexOf('/');
                if (slash <= 0) continue;
                string folder = name[..slash];
                if (FolderPattern.IsMatch(folder)) return folder;
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
        return null;
    }

    private static void ResetDirectory(string directory)
    {
        if (!Directory.Exists(directory)) return;
        foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
        {
            try
            {
                if (Directory.Exists(entry)) Directory.Delete(entry, recursive: true);
                else File.Delete(entry);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 清理失败不影响后面的 tar 解包（tar 会覆盖同名文件）。
            }
        }
    }

    private void CleanupStaging(string staging)
    {
        if (staging.Length == 0 || !Directory.Exists(staging)) return;
        try
        {
            // 删除前确认还在我们自己的暂存目录里，避免误删。
            string full = Path.GetFullPath(staging);
            string root = Path.GetFullPath(Path.Combine(_helperRoot, ".staging")) + Path.DirectorySeparatorChar;
            if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) Directory.Delete(full, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log("[deploy] 暂存目录清理失败：" + staging);
        }
    }

    private static async Task<(int Code, string Output, string Error)> RunAsync(string fileName,
        IEnumerable<string> arguments, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using Process process = Process.Start(start) ?? throw new DeployStepException("无法运行 " + fileName + "。");
        Task<string> output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> error = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new DeployStepException($"{Path.GetFileName(fileName)} 执行超时。");
        }
        return (process.ExitCode, await output.ConfigureAwait(false), await error.ConfigureAwait(false));
    }

    private static string FirstLine(string text)
    {
        string trimmed = (text ?? "").Trim();
        if (trimmed.Length == 0) return "（没有更多信息）";
        int index = trimmed.IndexOfAny(['\r', '\n']);
        return index < 0 ? trimmed : trimmed[..index];
    }

    private static string ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? "" : "";

    private static string FormatSize(long bytes) => bytes >= 1024L * 1024
        ? $"{bytes / (1024d * 1024d):0.0} MB"
        : $"{Math.Max(1, bytes / 1024d):0} KB";

    internal static string NormalizeBox(string? sandbox)
    {
        string value = (sandbox ?? "").Trim();
        if (value.Length == 0 || IsHostAlias(value)) return "";
        if (value.StartsWith("box:", StringComparison.OrdinalIgnoreCase) && value.Length > 4) value = value[4..];
        return value;
    }

    private static bool IsHostAlias(string? sandbox)
    {
        string value = (sandbox ?? "").Trim();
        return value.Equals("host", StringComparison.OrdinalIgnoreCase)
            || value.Equals("主机", StringComparison.Ordinal);
    }

    public void Dispose()
    {
        _http.Dispose();
        _probe.Dispose();
        _gate.Dispose();
    }

    private static string SafeName(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (char character in value)
            builder.Append(Path.GetInvalidFileNameChars().Contains(character) || character is '/' or '\\' or ':'
                ? '_' : character);
        string name = builder.ToString().Trim();
        return name.Length == 0 ? "host" : name;
    }

    private static DeployStep[] CreateSteps() =>
    [
        new("precheck", "检查运行环境", "pending", ""),
        new("package", "准备助手安装包", "pending", ""),
        new("download", "下载助手安装包", "pending", ""),
        new("extract", "解包并检查内容", "pending", ""),
        new("install", "落位助手目录", "pending", ""),
        new("pair", "准备配对信息", "pending", ""),
        new("launch", "启动助手", "pending", ""),
        new("port", "等待控制端口", "pending", ""),
        new("register", "写回账号档案", "pending", ""),
    ];

    /// <summary>一个助手安装包：本地目录（SourceDirectory）或 zip（ZipPath）+ 期望的大小/哈希。</summary>
    private sealed record HelperPackage(string Box, string Folder, string ZipPath, string DownloadUrl,
        string Sha256, long Size, string SourceDirectory);

    private sealed record HelperManifestInfo(string Version, string Folder, string ZipUrl, string Sha256, long Size);

    /// <summary>逐步更新 + 每步推送进度。</summary>
    private sealed class StepBook(DeployStep[] steps, Action<DeployStep[]>? progress)
    {
        private readonly List<DeployStep> _steps = [.. steps];
        private string _current = "";

        internal DeployStep[] Snapshot() => _steps.ToArray();

        internal void Begin(string id)
        {
            _current = id;
            Update(id, "running", "");
            Publish();
        }

        internal void Ok(string id, string detail)
        {
            Update(id, "ok", detail);
            Publish();
        }

        internal void Skip(string id, string detail)
        {
            Update(id, "skipped", detail);
            Publish();
        }

        internal void FailCurrent(string detail)
        {
            if (_current.Length == 0)
            {
                Publish();
                return;
            }
            Update(_current, "failed", detail);
            foreach (DeployStep step in _steps.Where(step => step.State == "pending").ToArray())
                Update(step.Id, "skipped", "上一步失败后没有继续。");
            Publish();
        }

        internal void Publish()
        {
            if (progress is null) return;
            try
            {
                progress(Snapshot());
            }
            catch (Exception)
            {
                // 前端已经关掉时忽略推送失败，流程继续跑完。
            }
        }

        private void Update(string id, string state, string detail)
        {
            int index = _steps.FindIndex(step => step.Id == id);
            if (index < 0) return;
            _steps[index] = _steps[index] with { State = state, Detail = detail };
        }
    }
}
