<p align="center">
  <a href="https://github.com/buguniaoOVO/deskrawl-hub-client/stargazers"><img src="docs/star-support.svg" alt="给桌破中控点个 Star" width="360"></a>
  <img src="docs/afdian-support.png" alt="扫码为阿丸好饿发电" width="280">
</p>

# 桌破多开中控助手

桌破多开中控助手 1.2 使用 WinForms、WebView2 和内置运行模块，在桌面窗口中提供账号状态、掉落记录、血脉鉴定、拍卖助手、助手设置与运行设置。关闭窗口会将客户端缩小到任务栏；退出按钮会关闭客户端及其内置运行模块。

> **使用前请注意：本项目只提供桌面控制台，不包含 X1X 游戏助手本体或沙盒插件。** 请另行安装 [X1X 助手](https://x1x.tw/deskrawl/helper/)。在中控内为各环境选择助手目录后，可分别启动助手；启动游戏可在助手离线时使用。

账号状态页的账号卡片展示助手血脉评定、百分位和有效样本；点击「查看鉴定」进入独立的血脉鉴定页。

## 主机与沙箱管理

- 账号状态页保留主机及各 Sandboxie 沙箱的卡片，游戏与助手均可分别启动。
- 「环境设置」保存该环境使用的助手目录、游戏路径和启动方式。Steam 方式通过目标环境的 Steam 启动；直接启动方式运行所选的 Deskrawl.exe。
- 「助手设置」在客户端中打开 X1X 官网的助手页面。主机与各沙箱使用独立的 WebView2 存储，打开页面前核对配对信息、控制端口及沙箱归属。
- 「浏览器打开」使用当前助手的完整配对链接，可重新连接浏览器页面。浏览器访问本机网络的权限仍由浏览器管理。
- 助手更新后，中控在原助手目录的同级目录查找配对信息一致的新版本；目录或配对信息存在歧义时，需要在环境设置中选择。

账号环境保存在 LocalAppData 下的 DeskrawlHubClient/account-workspace.json。中控保持原助手的配对码，并通过 Sandboxie 的进程查询接口识别游戏与助手所属环境。内嵌的 X1X 官网页面需要联网。

## 控制台页面介绍

账号状态页集中展示助手连接、游戏进程、自动流程、角色信息和掉落统计。标题旁可统一显示或隐藏游戏窗口、开启或关闭全局锁帧；运行设置提供 10 帧和 30 帧挡位，账号卡片提供单账号日常操作。

完整账号状态页面：

![桌破中控完整账号状态页面](docs/screenshots/console-account-status.png)

账号总览区域：

![桌破中控账号总览与运行统计](docs/screenshots/console-overview.png)

账号状态页提供全局锁帧和全部游戏窗口显隐按钮。全局锁帧会应用到已识别的 Deskrawl 游戏进程，并在新进程启动后自动应用；帧率挡位可在运行设置中选择 10 或 30 帧。

## 血脉鉴定

「血脉鉴定」页读取各环境助手的 `/control/lineage` 记录，逐账号列出赌博出货、远古装备与黑雾装备的百分位、期望值、品质分布、可交易珍品和最近记录。页面只做汇总展示，记录来源与概率口径由 X1X 助手计算。

![桌破中控血脉鉴定页面](docs/screenshots/console-bloodline.png)

## 拍卖助手

「拍卖助手」页扫描各账号背包与仓库，列出未锁定、未强化、未镶嵌、已铸造的传奇及以上装备与缰绳，作为上架候选。

当前 X1X 助手与游戏内助手没有拍卖行查询、空位上架或仓库取出的操作接口，因此 1.2 只提供候选清单与流程说明，不会模拟点击或代替游戏提交上架。待助手开放对应接口后，才能在客户端接入「暂停自动流程 → 回城 → 从仓库取出珍品 → 上架空位 → 恢复自动」的完整流程。

![桌破中控拍卖助手页面](docs/screenshots/console-auction.png)

Sandboxie 会把游戏写出的状态文件放在各自沙盒目录。客户端会从这些目录读取状态，显示锁帧是否已应用。帧率设置只修改 Unity 的目标帧率和垂直同步，不会主动压缩游戏内存。

## 多开运行示例

任务管理器截图展示了三个同时运行的 `Deskrawl.exe` 实例。截图时分别显示约 68.9 MB、59.6 MB 和 56.4 MB 内存占用；实际数值会随游戏状态变化。

![任务管理器中的多个 Deskrawl 游戏进程](docs/screenshots/multi-instance-processes.png)

## 构建

在 Windows x64 安装 .NET 8 SDK 和 MinGW x64，然后运行：

```powershell
& .\publish.ps1
```

生成文件位于 `publish\桌破中控.exe`。客户端使用系统 Microsoft Edge WebView2 Runtime。

如需让发布脚本自动覆盖本机安装版本，请在仓库根目录创建被 Git 忽略的 `install-path.local.txt`，写入目标 exe 的绝对路径。

运行 `install.ps1` 可将发布文件复制到桌面，并把指定的旧桌面程序文件设为隐藏属性。详细部署说明见 [README-部署.md](README-部署.md)。

## 项目结构

- `MainForm.cs`：桌面窗口、运行模块启动和 WebView2 资源路由。
- `WebAssets/`：页面结构、样式和交互代码。
- `Assets/engine.payload.gz`：客户端内置运行模块。
- `FrameCapLivePlugin/`：游戏进程限帧组件源码。
- `Assets/demon-icon.png`：客户端图标源图。
- `publish.ps1`：生成 Windows x64 单文件客户端。
