<p align="center">
  <a href="https://github.com/buguniaoOVO/deskrawl-hub-client/stargazers"><img src="docs/star-support.svg" alt="给桌破中控点个 Star" width="360"></a>
  <img src="docs/afdian-support.png" alt="扫码为阿丸好饿发电" width="280">
</p>

# 桌破中控客户端

桌破中控客户端 1.0 使用 WinForms、WebView2 和内置运行模块，在桌面窗口中提供控制总览、账号实例、掉落记录与运行设置。关闭窗口会将客户端缩小到任务栏；退出按钮会关闭客户端及其内置运行模块。

## 构建

在 Windows x64 安装 .NET 8 SDK，然后运行：

```powershell
& .\publish.ps1
```

生成文件位于 `publish\桌破中控.exe`。客户端使用系统 Microsoft Edge WebView2 Runtime。

运行 `install.ps1` 可将发布文件复制到桌面，并把指定的旧桌面程序文件设为隐藏属性。详细部署说明见 [README-部署.md](README-部署.md)。

## 项目结构

- `MainForm.cs`：桌面窗口、运行模块启动和 WebView2 资源路由。
- `WebAssets/`：页面结构、样式和交互代码。
- `Assets/engine.payload.gz`：客户端内置运行模块。
- `Assets/demon-icon.png`：客户端图标源图。
- `publish.ps1`：生成 Windows x64 单文件客户端。
