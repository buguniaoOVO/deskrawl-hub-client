# 桌破控制台

发布包只有一个 Windows x64 客户端 EXE。客户端窗口、交互代码、文案、配色和图标由本项目重新实现；原有控制引擎压缩后随客户端发布，用来保持助手接口兼容。

客户端首次启动时把运行引擎和桌面 `config.json` 放入 `%LOCALAPPDATA%\DeskrawlHubClient`。之后的设置保存在该目录。客户端从自身内置资源启动本地引擎，控制和界面都通过本机回环地址通信。

客户端使用系统 Microsoft Edge WebView2 Runtime。运行日志保存在 `%LOCALAPPDATA%\DeskrawlHubClient\client.log`。

客户端采用白色工作区、蓝色导航和角色图标。页面保留控制总览、战利品记录与运行设置，账号卡片提供单实例控制。点击窗口关闭按钮时，客户端会最小化到任务栏并继续运行；点击界面右上角的退出图标会关闭客户端和内置引擎。

客户端版本为 `1.0`。设置页可填写 GitHub 仓库地址，启用后在启动时和每 12 小时读取一次最新 Release；可按版本关闭提醒。仓库地址留空时，GitHub 入口和更新检测保持待配置状态。检测偏好保存在 `%LOCALAPPDATA%\DeskrawlHubClient\client-settings.json`。

掉落记录按 X1X 简中装备图鉴校准装备名称与稀有度，每天刷新一次 Wiki 缓存；断网时继续使用本地助手上报的名称。

## 发布与安装

在 Windows x64 上运行：

```powershell
& .\publish.ps1
& .\install.ps1
```

发布文件位于 `publish\桌破中控.exe`。安装脚本会在 LocalAppData 备份原桌面主程序，把新客户端放到桌面为 `桌破中控.exe`，并隐藏旧服务端、旧客户端及旧 WebView2 缓存目录。

## 工程文件

- `MainForm.cs`：单文件宿主、内置引擎提取、隐藏启动与 WebView2 资源路由。
- `WebAssets\index.html`、`client.css`、`client.js`：全新页面结构、画面和交互。
- `Assets\engine.payload.gz`：随客户端封装的兼容运行模块。
- `generate-icon.py`：生成客户端 Windows 图标。
