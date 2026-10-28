using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace DeskrawlHubClient;

internal sealed class HelperPane : UserControl
{
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    private readonly Label _status = new() { AutoSize = true, Text = "正在加载助手设置…", Margin = new Padding(12, 10, 6, 6) };
    private readonly HttpClient _http = new(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromSeconds(100) };
    private readonly AccountWorkspace _workspace;
    private readonly HelperConnection _connection;
    private readonly string _dataRoot;
    private bool _disposed;

    internal HelperPane(AccountWorkspace workspace, HelperConnection connection, string dataRoot, string name, Action back)
    {
        _workspace = workspace;
        _connection = connection;
        _dataRoot = dataRoot;
        Dock = DockStyle.Fill;
        BackColor = Color.White;
        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 52, Padding = new Padding(8), AutoSize = true };
        Button Add(string text, Action click)
        {
            var button = new Button { Text = text, AutoSize = true, Height = 32, FlatStyle = FlatStyle.Flat };
            button.Click += (_, _) => click();
            toolbar.Controls.Add(button);
            return button;
        }
        Add("返回控制台", back);
        toolbar.Controls.Add(new Label { Text = name + " · 助手设置", AutoSize = true, Margin = new Padding(12, 10, 12, 6) });
        Add("刷新页面", () => _web.CoreWebView2?.Reload());
        Add("在浏览器打开", () =>
        {
            Process.Start(new ProcessStartInfo(PanelAddress()) { UseShellExecute = true });
        });
        toolbar.Controls.Add(_status);
        Controls.Add(_web);
        Controls.Add(toolbar);
    }

    private string PanelAddress() => $"https://x1x.tw/deskrawl/helper/?lang=zh-CN#token={_connection.Token}&port={_connection.Port}";

    internal async Task InitializeAsync()
    {
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(AccountWorkspace.Id(_connection.Sandbox))))[..20];
        string userData = Path.Combine(_dataRoot, "HelperWebViews", key);
        var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: userData);
        if (_disposed) return;
        await _web.EnsureCoreWebView2Async(environment);
        if (_disposed) return;
        var core = _web.CoreWebView2;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreHostObjectsAllowed = false;
        core.Settings.IsWebMessageEnabled = false;
        core.AddWebResourceRequestedFilter("http://127.0.0.1:*/*", CoreWebView2WebResourceContext.All);
        core.AddWebResourceRequestedFilter("http://localhost:*/*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += ProxyHelperRequest;
        core.NavigationStarting += (_, e) =>
        {
            if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri)
                && uri.Scheme == "https" && uri.Host == "x1x.tw"
                && uri.AbsolutePath.StartsWith("/deskrawl/helper/", StringComparison.Ordinal)) return;
            e.Cancel = true;
            _status.Text = "请从控制台选择账号环境；外部链接可在浏览器打开。";
        };
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http")
                Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        };
        core.NavigationCompleted += (_, e) => _status.Text = e.IsSuccess
            ? "设置保存至当前环境的 X1X 助手"
            : "页面加载失败，请检查网络后刷新。";
        core.Navigate(PanelAddress());
    }

    private async void ProxyHelperRequest(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        var deferral = e.GetDeferral();
        try
        {
            if (_disposed) return;
            var uri = new Uri(e.Request.Uri);
            int status;
            byte[] bytes;
            string contentType = "application/json; charset=utf-8";
            bool allowed = uri.IsLoopback && (uri.Port == _connection.Port || uri.Port == _connection.Port - 1)
                && e.Request.Method is "GET" or "POST" or "OPTIONS";
            if (!allowed || !_workspace.IsConnectionOwner(_connection, uri.Port))
            {
                status = 409;
                bytes = Encoding.UTF8.GetBytes("{\"error\":\"account_environment_changed\"}");
                _status.Text = "助手端口已变更，请返回控制台后重新打开。";
            }
            else if (e.Request.Method == "OPTIONS")
            {
                status = 204;
                bytes = [];
            }
            else
            {
                // Only the two verified ports of the selected environment are forwarded.
                var target = new Uri($"http://127.0.0.1:{uri.Port}{uri.PathAndQuery}");
                using var request = new HttpRequestMessage(new HttpMethod(e.Request.Method), target);
                // Supply the selected environment's token to both the control and data API.
                // The page's own no-token probe uses a separate response path below.
                request.Headers.TryAddWithoutValidation("X-Deskrawl-Token", _connection.Token);
                request.Headers.TryAddWithoutValidation("Origin", "https://x1x.tw");
                if (e.Request.Method == "POST")
                {
                    using var body = new MemoryStream();
                    if (e.Request.Content is not null) await e.Request.Content.CopyToAsync(body);
                    request.Content = new ByteArrayContent(body.ToArray());
                    request.Content.Headers.TryAddWithoutValidation("Content-Type", "application/json");
                }

                using var response = await _http.SendAsync(request);
                status = (int)response.StatusCode;
                bytes = await response.Content.ReadAsByteArrayAsync();
                contentType = response.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";
            }
            if (!_disposed)
                e.Response = _web.CoreWebView2.Environment.CreateWebResourceResponse(new MemoryStream(bytes), status, "Helper",
                    $"Content-Type: {contentType}\r\nCache-Control: no-store\r\nVary: Origin\r\nAccess-Control-Allow-Origin: https://x1x.tw\r\n"
                    + "Access-Control-Allow-Methods: GET, POST, OPTIONS\r\nAccess-Control-Allow-Headers: X-Deskrawl-Token, Content-Type\r\n"
                    + "Access-Control-Allow-Private-Network: true\r\n");
        }
        catch (Exception)
        {
            if (!_disposed)
            {
                _status.Text = "助手请求被拦截：请确认此环境的助手仍在运行，并允许本机网络访问。";
                e.Response = _web.CoreWebView2.Environment.CreateWebResourceResponse(
                    new MemoryStream(Encoding.UTF8.GetBytes("{\"error\":\"helper_unavailable\"}")), 502, "Helper unavailable",
                    "Content-Type: application/json\r\nAccess-Control-Allow-Origin: https://x1x.tw\r\nCache-Control: no-store\r\n");
            }
        }
        finally
        {
            try { deferral.Complete(); }
            catch (System.Runtime.InteropServices.COMException) { }
        }
    }

    protected override void Dispose(bool disposing)
    {
        _disposed = true;
        if (disposing) { _http.Dispose(); _web.Dispose(); }
        base.Dispose(disposing);
    }
}
