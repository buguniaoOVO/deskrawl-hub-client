# X1X Deskrawl 助手「一键部署」实现规格（供 C# 实现者照做）

- 版本：v1（2026-11-05）
- 读者：在「桌破中控」（WinForms + WebView2，仓库 `中控源\`）里实现「一键部署 / 更新 X1X 助手」的 C# 开发者
- 范围：主机（无沙箱）与 Sandboxie 沙箱两种环境的「安装 / 落位 / 配对 / 启动 / 校验」全流程
- 证据等级约定：
  - **实测** = 本次在本机（2026-11-05）真实发出的请求/响应、真实读取的文件、真实做的剥离实验
  - **源码** = 直接引用某个文件的行号（`文件:行`），可自行核对
  - **推断** = 由实测/源码外推，标注依据与置信度

被测样本（本机现状，实测）：

| 环境 | 助手目录（宿主视角） | 物理目录（沙箱内） | control / api 端口 | 版本 |
|---|---|---|---|---|
| 主机 | `D:\Tool\赛博养蛊\桌破\0\X1X-Deskrawl-Helper-1.54.2-build26337088` | 同左 | 8766 / 8765 | 1.54.2 |
| 沙箱 1 | `D:\Tool\赛博养蛊\桌破\1\X1X-Deskrawl-Helper-1.54.2-build26337088`（宿主上不存在） | `D:\SANDBOX\43591\1\drive\D\Tool\赛博养蛊\桌破\1\X1X-Deskrawl-Helper-1.54.2-build26337088` | 8776 / 8775 | 1.54.2 |
| 沙箱 2 | `D:\Tool\赛博养蛊\桌破\2\X1X-Deskrawl-Helper-1.54.2-build26337088`（宿主上不存在） | `D:\SANDBOX\43591\2\drive\D\Tool\赛博养蛊\桌破\2\X1X-Deskrawl-Helper-1.54.2-build26337088` | 8786 / 8785 | 1.54.2 |

（沙箱目录「宿主上不存在但进程里存在」是本文最重要的一个陷阱，见 §6.1 与 §7.5。）

---

## 0. 八问速查

| # | 问题 | 一句话结论 |
|---|---|---|
| 1 | 「安装」等于什么 | 解压一个 `X1X-Deskrawl-Helper-<ver>-build<buildid>` 目录，入口是 `runtime\pythonw.exe core\app\deskrawl_helper.py`；最小可跑集合见 §1.3（实测：删掉 `core\host`、`core\scoring` 会直接崩） |
| 2 | 配对信息在哪 | 助手目录下 `core\helper\runtime\token`（64 位小写十六进制，单行）；端口记录在 `core\runtime\ports.json`；沙箱内路径要用 `drive\<盘符>\…` 映射 |
| 3 | 端口如何决定 | 12 组端口对 `(8766+10i, 8765+10i)`，i=0..11；`--port` 可指定 control 口，api 恒为 control-1；选中的对写回 `ports.json` |
| 4 | 无 token 身份端点 | **不存在**。无 token 只能得到「这是不是助手」（401/302/CSP），身份必须靠 token 读 `/control/status` |
| 5 | 官方下载入口 | 网站无公开下载链接（面板文案指向 Discord 資源帖）；**唯一可自动化的是签名清单** `https://pub-a7fb98aa695440ce83c6954022a90bc6.r2.dev/deskrawl/update/latest.json`，zip 为 ZipCrypto 加密（密码 `x1x.tw`） |
| 6 | 沙箱落位 | 两条路：①按 SbieApi 盒子路径直接写 `<boxroot>\drive\D\…`（免 Start.exe，需先停进程）；②`Start.exe /box:NAME /silent /wait <程序> <参数>` 在盒内解包/复制 |
| 7 | 端口识别 | 监听表 → PID → 镜像路径（**逻辑路径**）→ `SbieApi_QueryProcess` 得盒子 → 两次 `GetDirectoryName` 得根 → IsHelper 校验 → 带 token 探测确认；证据分级见 §7.4 |
| 8 | 状态机 | 12 步（preflight → 清单 → 下载 → 校验 → 解包 → 落位 → 复核 → 配对 → 启动 → 等端口 → 身份确认 → 完成），23 类失败码与中文文案见 §8.3 |

---

## 1. 「安装」到底等于什么

### 1.1 目录布局（实测 ls，1.54.2）

```
X1X-Deskrawl-Helper-1.54.2-build26337088\
├─ CURRENT_BUILD.json              # 运行时生成（package_mode 写），记录 build 指针
├─ Start Helper 啟動助手.cmd        # 启动器
├─ Stop Helper 關閉助手.cmd
├─ Diagnostic Report 產生診斷報告.cmd
├─ README.txt / README-English.txt / README-简体中文.txt / LICENSE.txt / LICENSES.txt
├─ runtime\                        # 18 MB，441 文件 —— 嵌入式 CPython 3.11
│   ├─ python.exe / pythonw.exe / python311.dll / vcruntime140.dll
│   ├─ DLLs\
│   └─ Lib\
├─ core\                           # 39 MB，811 文件
│   ├─ app\                        # 主程序 + 16 个模块（1.2 MB）
│   │   ├─ deskrawl_helper.py      # 入口（5715 行，VERSION 在 304 行）
│   │   └─ updater.py / diagnose.py / lineage.py / ledger.py / runlog.py / drops.py …
│   ├─ autoloop\                   # 全自动循环（loop.py 等，1.7 MB）
│   ├─ helper\                     # 控制面板静态资源 + 只读数据 API（6.7 MB）
│   │   ├─ ui\                     # art.css、i18n.json、assets\game\（面板美术，由 x1x.tw 页面加载）
│   │   ├─ helper\                 # server.py（数据 API）、normalize.py、sources.py、api_schema.py…
│   │   └─ runtime\                # ★ token 在这里（首次启动生成）
│   ├─ host\                       # 22 MB，游戏内元件（hostctl.py、bin\、bundle\）
│   ├─ engine\                     # 游戏内存读取（procmem.py / reader_v2.py）
│   ├─ scoring\                    # 装备评分（gear_score2.py）
│   ├─ save\
│   └─ runtime\                    # ★ 运行状态：ports.json、app_state.json、runs.jsonl、instances\、autoloop\
└─ build\                          # 5.2 MB，按游戏版本的数据
    ├─ build.json                  # ★ package 模式判定 + 版本号 + 支持的 game assembly 哈希
    ├─ official.json               # 官方文件清单（1122 个文件的 sha256 + 签名）
    ├─ loc\                        # 本地化 CSV（352 KB）
    └─ out\                        # data\ scoring\ host_actions.json memory_map.json（4.8 MB）
```

### 1.2 入口与必需判定

| 事实 | 证据 |
|---|---|
| 启动入口 = `runtime\pythonw.exe` + `core\app\deskrawl_helper.py` | 实测 `Start Helper 啟動助手.cmd`：`start "" "%~dp0runtime\pythonw.exe" "%~dp0core\app\deskrawl_helper.py" %*` |
| 中控启动助手用的实参 | 源码 `AccountWorkspace.cs:341-342`：`pythonw.exe deskrawl_helper.py --open none`（不弹浏览器；`SandboxRuntime.Start(..., hidden:true)` 追加 `/hide_window`） |
| 「这是不是一个助手目录」的唯一判定（现有实现） | 源码 `AccountWorkspace.cs:202-203`：同时存在 `core\app\deskrawl_helper.py` 与 `runtime\pythonw.exe` |
| 版本号从目录名解析（现有实现） | 源码 `AccountWorkspace.cs:220-224`：正则 `Helper-(\d+\.\d+(?:\.\d+)?)` |
| 官方更新器对「包完整」的判定（更严） | 源码 `updater.py:532-540`：存在 `build\build.json`、`runtime\pythonw.exe`、`core\app\deskrawl_helper.py`，且 `build.json.package_version` == 清单版本 |
| package 模式（决定是否启用自带数据/更新检查） | 源码 `deskrawl_helper.py:113-128`（`package_mode()`）：仅当 `build\build.json` 存在时为 True，并把 `DESKRAWL_POINTER` / `DESKRAWL_LOC_CSV` / `DESKRAWL_SCORING_DATA` / `DESKRAWL_HOST_BUNDLE` 指到包内 |

### 1.3 「最少需要哪些文件/目录」——实测剥离实验

实验方法（本次真实执行，已清理）：把 1.54.2 整包复制到工作区临时目录，逐轮删除目录后用
`runtime\pythonw.exe core\app\deskrawl_helper.py --open none --port 8941` 启动，再用该副本的 token 请求 `GET /control/status`（端口 8941 不在 8766+10n 阶梯上，避免影响现有实例；实验结束后已 `POST /control/app {"action":"quit"}` 关闭并删除全部文件）。

| 轮次 | 删除内容 | 结果（实测） | 结论 |
|---|---|---|---|
| R1 | 除 `runtime\ core\app\ core\autoloop\ core\helper\ build\build.json` 外全部删除（含 `core\host`） | 启动即崩：`File "core\autoloop\loop.py", line 27 … from hostctl import HostClient` → `ModuleNotFoundError: No module named 'hostctl'` | **`core\host\` 是硬依赖**（至少 hostctl.py），不能省 |
| R2 | R1 + 恢复 `core\host` | 启动成功；`/control/status` → **200**，version=1.54.2、package=true、art=true；但 api.running=false | 控制面可跑；数据 API 缺件 |
| R3 | 同上 + 恢复 `core\scoring core\engine build\out build\loc` | api.running=true；`GET :8940/v1/health` → `200 {"ok":true,"api":"deskrawl-helper-api/1.23","source":"live"}` | 数据 API 才能起来 |

R2 失败的根因（实测日志 `core\runtime\helper-api.log`）：

```
Traceback (most recent call last):
  File "...\core\helper\helper\server.py", line 25, in <module>
    import api_schema, normalize, policy as POL, sources
  File "...\core\helper\helper\normalize.py", line 18, in <module>
    import gear_score2 as gs
ModuleNotFoundError: No module named 'gear_score2'
```

所以「能跑」分三档，实现者应按档位决定校验严格度：

| 档位 | 必需内容（相对包根） | 通过判据 |
|---|---|---|
| **A. 起得来（控制面）** | `runtime\`（pythonw.exe/python311.dll/Lib）、`core\app\`、`core\autoloop\`（含 loop.py）、`core\helper\helper\`、`core\host\`、`build\build.json` | 进程存在 + 带 token 的 `GET /control/status` 返回 200 |
| **B. 面板可用（美术）** | A + `core\helper\ui\`（status.art = `UI\assets\game\manifest.json` 是否存在，源码 `deskrawl_helper.py:3133`） | `status.art == true` |
| **C. 数据 API 可用** | A + `core\scoring\`（gear_score2）+ `core\engine\` + `build\out\` + `build\loc\` | `status.api.running == true` 且 `GET :<api>/v1/health` 200 |
| 建议但非必需 | `build\official.json`（官方文件自检）、README/LICENSES、*.cmd 启动器、`CURRENT_BUILD.json`（自动生成） | `status.official.state`（缺失时为 checking，实测） |

> **实现建议**：一键部署统一按「完整包」落位（不要做精简安装）。精简只用于「本机已有完整副本、只想复制一份到沙箱」的场景；即使如此也要整目录复制，不要挑文件。

### 1.4 版本号的四个来源（互相校验）

| 来源 | 位置 | 实测样例 |
|---|---|---|
| 目录名 | `X1X-Deskrawl-Helper-<ver>-build<buildid>`（正则 `^X1X-Deskrawl-Helper-(\d+\.\d+\.\d+)-build(\d+)$`，源码 `updater.py:26`） | `X1X-Deskrawl-Helper-1.54.2-build26337088` |
| `build\build.json` | `package_version` / `steam_buildid` / `gameassembly_sha256` | package_version=1.54.2，steam_buildid=26337088 |
| 运行中 | `GET /control/status` 的 `version` | `1.54.2` |
| 线上清单 | `manifest.version` / `manifest.folder` | `1.54.2` / 同名文件夹 |

（`build.json` 全字段实测：gameassembly_sha256、steam_buildid、promoted_at、game_dir、package_version、built_at。）

---

## 2. 配对信息（token / 控制端口 / 账号绑定）

### 2.1 token 文件：路径与格式

| 项 | 值 | 证据 |
|---|---|---|
| 逻辑路径 | `<助手根目录>\core\helper\runtime\token` | 源码 `deskrawl_helper.py:63`：`TOKEN_FILE = os.path.join(CORE, 'helper', 'runtime', 'token')` |
| 目录/文件自动创建 | 首次启动自动建目录并写文件 | 源码 `deskrawl_helper.py:1514-1522` |
| 内容 | 64 个小写十六进制字符 + 换行，ASCII，66 字节 | 实测 `wc -c == 66` |
| 合法值校验 | `^[0-9a-f]{64}$`（否则视为「没有配对信息」） | 源码 `AccountWorkspace.cs:229`；助手侧同规则 `deskrawl_helper.py:1518` |
| 生成方式 | `secrets.token_hex(32)`（256 bit，OS CSPRNG） | 源码 `deskrawl_helper.py:1520`；数据 API 侧同规则 `core\helper\helper\server.py:44-54` |
| 复用规则 | 文件存在且合法则**原样沿用**，不轮换 | 源码 `deskrawl_helper.py:1515-1519` |
| 更新时的命运 | 一键更新会把旧目录的 token 复制到新目录（同环境配对不变） | 源码 `updater.py:30`（CARRY 含 core/helper/runtime/token）；实测 `core\runtime\updated_from.seen.json` 的 carried 列表含该项 |

**主机路径 vs 沙箱路径（关键）**：同一逻辑路径在沙箱里物理落在盒子根下：

```
逻辑：  D:\Tool\赛博养蛊\桌破\1\X1X-Deskrawl-Helper-1.54.2-build26337088\core\helper\runtime\token
物理：  D:\SANDBOX\43591\1\drive\D\Tool\赛博养蛊\桌破\1\X1X-Deskrawl-Helper-1.54.2-build26337088\core\helper\runtime\token
        \______ boxroot ______/\__ 固定的 drive\ 段 __/\____________ 去掉盘符冒号后的路径 ____________/
```

映射 API 已经存在，直接用：`SandboxRuntime.Root(box)`（SbieApi_QueryBoxPath，源码 `SandboxRuntime.cs:94-112`）、`FileCandidates(logical, box)`（源码 `SandboxRuntime.cs:126-141`，两条候选：`<root>\user\current\…` 与 `<root>\drive\<X>\…`）、`ResolveFile`（源码 `SandboxRuntime.cs:141`）。

### 2.2 端口记录：core\runtime\ports.json

实测（主机 1.54.2）：

```json
{
 "control": 8766,
 "api": 8765,
 "sandbox": ""
}
```

沙箱 1 的同名文件（实测，字段顺序不同）：

```json
{
  "sandbox": "1",
  "control": 8776,
  "api": 8775
}
```

- 写入时机：仅当「本次取的端口对 ≠ 已保存」时写（源码 `deskrawl_helper.py:5657-5661`）
- 读回校验：必须是 PORT_PAIRS 里的一对，否则当没有（源码 `deskrawl_helper.py:5343-5351`）
- 中控现有用法：监听表拿不到端口时用它兜底（源码 `AccountWorkspace.cs:185-190`）

### 2.3 实例信标：core\runtime\instances\box-<sha1(box)[:12]>.json

实测（主机，沙箱名空 → box-none.json）：

```json
{
 "sandbox": "",
 "control": 8766,
 "api": 8765,
 "pid": 29084,
 "version": "1.54.2",
 "beat": 1793879442.368495,
 "dir": "d:\\tool\\赛博养蛊\\桌破\\0\\x1x-deskrawl-helper-1.54.2-build26337088"
}
```

- 文件名规则：`'box-' + (sha1(box)[:12] if box else 'none') + '.json'`（源码 `deskrawl_helper.py:5354-5357`）
- 每 30 s 重写一次（BEACON_EVERY=30，BEACON_FRESH=120，源码 `deskrawl_helper.py:317`）
- `dir` 是规范化后的 PKG 绝对路径，与自身不一致的信标会被丢弃（防止整目录复制后被误认，源码 `deskrawl_helper.py:5386-5387`）
- **用途**：无需任何 HTTP 即可知道「这个目录里的助手在哪个沙箱、占了哪对端口、PID 是多少、版本多少」，是跨沙箱发现最便宜的真相来源（见 §7.3）。

### 2.4 token 怎么用（协议层）

| 用途 | 请求 |
|---|---|
| 控制 API | `GET http://127.0.0.1:<control>/control/status`，头 `X-Deskrawl-Token: <token>` |
| 数据 API | `GET http://127.0.0.1:<api>/v1/health`，头 `X-Deskrawl-Token: <token>` |
| 鉴权实现 | `hmac.compare_digest` 比对（源码 `deskrawl_helper.py:5085-5087`；`server.py:177-179`） |
| 其它硬性条件 | Host 必须是 `127.0.0.1:<端口>` 或 `localhost:<端口>`（否则 421）；带 Origin 时必须是 `https://x1x.tw`（否则 403）；token 不允许出现在 query（`server.py:162-163`） |

现有 C# 侧对应实现：`AccountWorkspace.ConnectAsync`（源码 `AccountWorkspace.cs:362-394`）。

### 2.5 「账号绑定」绑的是什么

**不是游戏账号**。绑定三件套：`(沙箱名, 助手目录, token)`；游戏角色/账号在助手自己的设置里（`core\runtime\autoloop\autologin.json`、`settings_by_character.json`，实测存在）。

中控侧的持久化在 `%LocalAppData%\DeskrawlHubClient\account-workspace.json`（源码 `AccountWorkspace.cs:64`；README 同述），结构为：

```json
[
  {
    "sandbox": "1",
    "helperDirectory": "D:\\Tool\\赛博养蛊\\桌破\\1\\X1X-Deskrawl-Helper-1.54.2-build26337088",
    "pairingFingerprint": "<SHA256(token) 的大写十六进制>",
    "gameExecutable": "D:\\…\\Deskrawl.exe",
    "gameLaunchMode": "steam",
    "hidden": false
  }
]
```

字段来源：源码 `AccountWorkspace.cs:14-23`（AccountProfile）；指纹算法 `AccountWorkspace.cs:231-235`（SHA256(UTF8(token)) 转大写十六进制）。指纹用于「换目录但配对不变」时自动跟随更新（源码 `AccountWorkspace.cs:149-155`）。

引擎侧还维护一份扫描结果 installs[]（`GET http://127.0.0.1:<engine>/api/config`，源码 `AccountWorkspace.cs:88-98`），实测每项形如：

```json
{"dir":"D:\\SANDBOX\\43591\\1\\drive\\D\\Tool\\…\\X1X-Deskrawl-Helper-1.54.2-build26337088","token":"<64hex>","control":8776,"api":8775,"sandbox":"1","ver":"1.54.2"}
```

> ⚠ 安全：token 等价于本机助手的完全控制权（`/control/app quit`、`/control/game launch` 等）。不要写进日志、诊断包、遥测、异常信息；助手侧日志已刻意不打印 token（实测 `helper-requests.log` 只记 `error=bad_or_missing_token`）。

---
## 3. 助手如何决定监听端口

### 3.1 配置项与默认值

| 项 | 值 | 证据 |
|---|---|---|
| 端口对集合 | `(8766+10i, 8765+10i)`，i=0..11，共 **12 对** | 源码 `deskrawl_helper.py:311`：`PORT_PAIRS = tuple((8766 + 10 * i, 8765 + 10 * i) for i in range(12))` |
| 默认（不带参） | 12 对里**第一对可用**的；若 `ports.json` 记录的对仍空闲则优先复用 | 源码 `deskrawl_helper.py:5447-5462`（pick_ports） |
| 命令行 | `--port <control>`（不给则自动挑） | 源码 `deskrawl_helper.py:5575-5576` |
| 指定时的 api 口 | `control-1`，可被环境变量 `DESKRAWL_API_PORT` 覆盖 | 源码 `deskrawl_helper.py:5601-5602`；`305`：`API_PORT = int(os.environ.get('DESKRAWL_API_PORT') or 8765)` |
| 持久化 | 写入 `core\runtime\ports.json` | 源码 `deskrawl_helper.py:5657-5661` |
| 官方说明（英文原文） | “the control port (default: the first free of 8766, 8776 … 8876; the data API takes the one below it)” | 源码 `deskrawl_helper.py:5575-5576` |

pick_ports 语义（源码 `deskrawl_helper.py:5447-5462`，可直接照抄成 C#）：

1. 候选顺序 = `[saved] + [其它 PORT_PAIRS]`
2. 第一轮：若某 control 口「有人应答」，用 token 探测（running_identity），若 `identity.sandbox == 本进程沙箱` → 判定「就是我自己在跑」（ours=true），直接复用
3. 第二轮：第一个「control 与 api 都没人应答，且两者都能 bind」的对 → 采用
4. 全部占用 → pair=None，打印「端口全被占用」并退出（源码 `deskrawl_helper.py:5637-5645`，文案还提示 `netsh interface ipv4 show excludedportrange protocol=tcp` 查看被 Windows/Hyper-V 保留的段）

> 实现注意：`port_bindable()`（源码 `deskrawl_helper.py:5318-5329`）存在的原因就是「Windows 保留端口不应答但 bind 会失败」。C# 侧选端口时必须做一次真实 TcpListener bind 探测，不能只 connect。

### 3.2 两个端口各自职责

| 端口 | 进程 | 职责 | 鉴权 | 实测 |
|---|---|---|---|---|
| control（8766+10i） | `deskrawl_helper.py` 主进程（Supervisor/Server） | ① `/control/*` 控制 API（settings/loop/api/game/host/update/diagnose/app/lineage/runs/…）② 静态资源 `/art.css` `/i18n.json` `/assets/…`（给 x1x.tw 页面跨域取用）③ `GET /` 302 跳到 x1x.tw 面板 | token + Origin 白名单 | `GET :8766/control/status`（无 token）→ 401 `{"error": "bad_or_missing_token"}`；`GET :8766/` → 302，Location 为 `https://x1x.tw/deskrawl/helper/`（非第一对时带 `#port=8776`） |
| api（8765+10i = control-1） | 子进程 `core\helper\helper\server.py --source live` | 只读游戏数据 `/v1/health /v1/state /v1/status /v1/schema /v1/run /v1/estimate`（仅 GET，禁止 body） | token（同样必需）+ Host 校验 | `GET :8765/v1/health`（无 token）→ 401 `{"error":"bad_or_missing_token"}`（分隔符无空格，见 §4.2） |

- 端口对的可用性由主进程管理：`/control/api` 的 action 取 start 或 stop 可起停数据 API（源码 `deskrawl_helper.py:29, 5131-5132`）
- `status.api` 实测为 `{"running": true, "port": 8765}`
- x1x.tw 页面的 CSP 只允许 12 个 control 口（实测响应头）：`style-src 'self' 'unsafe-inline' http://127.0.0.1:8766 http://127.0.0.1:8776 … http://127.0.0.1:8876` → 超过 8876 的控制口，官方网页面板连不上（浏览器直接拦），只能中控自己用。
- 中控自身支持 24 个窗口（`config.json` 的 `ports.count: 24`、`AccountWorkspace.cs:201` 允许到 8996），与助手的 12 对存在上限差；**实现者应把「可用上限 12」写进 UI 校验**（推断，依据：`PORT_PAIRS` 12 对 + x1x.tw CSP 12 个端口）。

### 3.3 端口冲突的三种真实来源

1. 另一份助手已经占了该对（`port_in_use`）
2. Windows/Hyper-V/WSL/Docker 保留段（`port_bindable` 失败）
3. 上一次的 TIME_WAIT 连接（实测 netstat 有大量 TIME_WAIT 到 8766/8776/8786，但**占用判据只看 LISTENING**）

---

## 4. 无 token 的身份探测端点：正式结论是「没有」

### 4.1 结论

**助手不提供任何免 token 的身份端点。** 所有 `/control/*` 都先过 token（源码 `deskrawl_helper.py:5081-5087`，token 校验在路由分派之前），所有 `/v1/*` 也一样（源码 `server.py:177-181`，token 校验在 `u.path not in ROUTES` 之前）。唯一免 token 的是静态资源与 `GET /` 的 302。

因此「识别某个监听端口属于哪个助手实例」必须由中控自己组合证据，见 §7。

### 4.2 免 token 能拿到的全部信号（全部实测）

| 探测 | 实测响应 | 能证明什么 | 不能证明什么 |
|---|---|---|---|
| `GET /`（control 口） | 302 Found，Location 为 `https://x1x.tw/deskrawl/helper/`（若 control 口非 8766 则带 `#port=8776`） | 这是 Deskrawl 助手；**直接给出 control 端口号** | 哪个沙箱、哪个版本 |
| `GET /control/status`（无 token） | 401，头 `Content-Type: application/json; charset=utf-8`、`Cache-Control: no-store`、`X-Content-Type-Options: nosniff`、`Referrer-Policy: no-referrer`；体 `{"error": "bad_or_missing_token"}` | 这是助手 **control** 口（与数据 API 的 401 文本有细微差别，见下） | 身份 |
| `GET /i18n.json`（无 token） | 200，Content-Type application/json，体约 190 KB；**响应头 CSP 的 connect-src 里写着 `http://127.0.0.1:8765`，即本实例的 api 口** | 助手存在；**可推出 control→api 配对** | 身份 |
| `GET /v1/health`（api 口，无 token） | 401，体 `{"error":"bad_or_missing_token"}`（**冒号后无空格**：助手侧 json.dumps 默认带空格，数据 API 侧手工紧凑输出） | 这是数据 API 口 | 身份 |
| `GET /control/status` 且 Host 不符 | 421 Misdirected Request，体 `{"error": "bad_host"}` | 是助手 | 身份 |
| `OPTIONS /control/status` + `Origin: https://x1x.tw` + `Access-Control-Request-Headers: x-deskrawl-token` | 204，`Access-Control-Allow-Origin: https://x1x.tw`；换别的 Origin → 403 `{"error":"origin_not_allowed"}` | 是助手，且 Origin 白名单生效 | 身份 |

**官方面板自己也是这么做的**（线上 JS 实测）：`panel.zh.mjs` 里先 `fetch(base + "/control/status", {cache:"no-store"})`，若 `status === 401` 就置 `conn = "no-token"`，文案「找到助手了，但還沒配對：請從「啟動助手.cmd」重新開啟這個頁面，或在下方手動配對」。即：**401 = 助手在，但没配对**，这是官方认可的探测语义。

### 4.3 带 token 的身份来源（唯一权威）

`GET /control/status`（200）里的 `instance` 对象，实测两例：

```json
// 主机 8766
{"sandbox":"","control":8766,"api":8765,"games":1,"elsewhere":["1","2"],"shared":[],"box_root":null,
 "dir":"d:\\tool\\赛博养蛊\\桌破\\0\\x1x-deskrawl-helper-1.54.2-build26337088"}
// 沙箱 1 8776
{"sandbox":"1","control":8776,"api":8775,"games":1,"elsewhere":[],"shared":[],
 "box_root":{"path":"D:\\SANDBOX\\%USERNAME%\\1","how":"api","drive":"D:","pkg_drive":"D:","system":false,"open":false},
 "dir":"d:\\tool\\赛博养蛊\\桌破\\1\\x1x-deskrawl-helper-1.54.2-build26337088"}
```

字段含义（源码 `deskrawl_helper.py:3127-3131`）：sandbox = `SbieApi_QueryProcess(自身)` 的结果（不在沙箱为 `""`，见 `deskrawl_helper.py:809-829`）；dir = `os.path.normcase(os.path.abspath(PKG))`（**逻辑路径**）；box_root = 沙箱物理根（`sandbox_root()`，how 取值 api / ini / default）；elsewhere = 别的沙箱里跑着的游戏数；shared = 同一目录被别的沙箱使用（信标）。

另有 version（1.54.2）、package（是否包模式）、art（面板美术是否齐全）、api、game、official 等。

> **`box_root.path` 里的 `%USERNAME%` 是显示用脱敏**（源码 `deskrawl_helper.py:501-505` hide_user()），真实路径是 `D:\SANDBOX\43591\1`。做路径计算前必须换回真实用户名（Environment.ExpandEnvironmentVariables 或手工替换）。
>
> **`instance.dir` 是逻辑路径**，在宿主机上可能不存在（沙箱 1/2 就是这种情况，实测）。要落到物理文件必须走 `SandboxRuntime.FileCandidates/ResolveFile`。

### 4.4 能否区分「版本/机器/账号」

- **版本**：能，但只在带 token 后（version）。无 token 时无法区分（根路径的 302 与 401 对所有版本一致）。
- **机器**：能间接（instance.dir 的盘符/路径 + 端口），无 token 不行。
- **账号**：完全不能。账号/角色不在 HTTP 层（在 autologin.json、settings_by_character.json、settings_character 等文件里）。`status.settings_character`（实测 `sakura#140c850d35e2`）与 `autologin-state.json` 的 slots 可对应角色，但那属于「数据」不是「身份」。

---

## 5. 官方下载入口与稳定性

### 5.1 页面侧：没有可自动化的官方下载

实测 `https://x1x.tw/deskrawl/helper/`：

- HTTP 200，text/html，但只有 2.2 KB 的 SPA 外壳（`<script type="module" src="/deskrawl/helper/helper.mjs">`），实际面板在 `helper.mjs` → `panel.zh.mjs`（409 KB）。
- `helper.mjs` 顶部注释（实测原文）：“x1x.tw hosts neither the art nor the helper download. This page is the helper's only control panel.”
- 面板里的官方安装指引（`panel.zh.mjs` 实测原文）：
  1.「到 X1X Discord 取得「桌面破壞神助手」程式包（目前只提供給社群成員；官方下載只有資源帖）」
  2.「解壓縮到一般資料夾（例如桌面，不要放在 Program Files），執行「啟動助手.cmd」。瀏覽器會自動開啟這個頁面並完成配對。」
  3.「瀏覽器詢問是否允許「存取這部裝置上的其他應用程式和服務」時，請選「允許」。」
  4.「助手會在背景執行，不會開視窗；…要完全關閉時，在控制台按「關閉助手」。」

**结论**：官网**没有**公共直链下载（无 zip/exe 直链、无 Release 页、无 /deskrawl/download 路由），分发靠 Discord 資源帖（可能需登录/门槛）。对自动化只有两条路：①（推荐）用**签名更新清单**；②让用户手工放入本地 zip，中控只负责校验与落位。

### 5.2 唯一可自动化的通道：签名更新清单（实测 200）

```
GET https://pub-a7fb98aa695440ce83c6954022a90bc6.r2.dev/deskrawl/update/latest.json?t=<epoch>
（源码常量 updater.py:19 MANIFEST_URL；?t= 防缓存见 updater.py:193-196；UA: X1X-Deskrawl-Helper-Updater）
```

响应（实测摘要，签名省略中间部分）：

```json
{
 "manifest": {
  "product": "x1x-deskrawl-helper",
  "version": "1.54.2",
  "folder": "X1X-Deskrawl-Helper-1.54.2-build26337088",
  "zip_url": "https://pub-a7fb98aa695440ce83c6954022a90bc6.r2.dev/deskrawl/e0c0a7c50e5235cdc9fb7f93/X1X-Deskrawl-Helper-1.54.2-build26337088.zip",
  "size": 20571590,
  "sha256": "16532fc4c05aa2f08d7e6d9ef96a5046f589edc9fbf4a615bf8f98c0a51ce032",
  "game_builds": { "14a46e0a1bcc": 26337088 },
  "steam_buildid": "26337088",
  "host_sha256": "e8cd64f4aa3febd6025498ce5ecf807d2e32e3b11b25f77dab1d8027c841936c",
  "notes": ["…"], "notes_en": ["…"],
  "released_at": "2026-11-03T20:37:54"
 },
 "sig": "<128 hex = Ed25519(canonical(manifest))>"
}
```

校验规则（源码 `updater.py:69-93`，逐条照做即可）：

| 规则 | 细节 |
|---|---|
| 签名 | `ed25519.verify(PUBLIC_KEY, canonical(manifest), sig)`；canonical 为 sort_keys + 紧凑分隔符 + ensure_ascii=False 的 JSON 字节；公钥 hex `0e8e4a607d3d0dd1d368b161b3a0a6e3e8415adba213b7b6b807a44f22365439`（updater.py:22） |
| 产品 | `manifest.product == "x1x-deskrawl-helper"` |
| 版本 | `^(\d+)\.(\d+)\.(\d+)$` |
| 文件夹 | `^X1X-Deskrawl-Helper-(\d+\.\d+\.\d+)-build(\d+)$` 且捕获版本 == manifest.version |
| 下载地址 | 必须以 `https://pub-a7fb98aa695440ce83c6954022a90bc6.r2.dev/deskrawl/` 开头、以 `<folder>.zip` 结尾 |
| 体积/哈希 | size 为 int，`0 < size <= 300 MiB`；sha256 为 64 hex |
| 游戏版本门禁 | game_builds 非空；key 为 GameAssembly.dll 的 sha256 前 12 位，value 为 steam buildid |

> C# 侧要点：Ed25519 在 .NET 可用 BouncyCastle / NSec 等库，或参考 `core\app\ed25519.py` 的实现。**务必**做签名校验，否则等于允许任意 URL 落地。

### 5.3 下载与包格式（实测 HTTP 层）

| 探测 | 结果（实测） |
|---|---|
| 对 zip_url 发 Range: bytes=0-15 的 GET | 206，`content-type: application/zip`，`accept-ranges: bytes`，`content-range: bytes 0-15/20571590`，`server: cloudflare`，etag / last-modified 齐全 |
| `GET latest.json` | 200，`content-type: application/json`，`cache-control: no-cache, max-age=0` |
| 不存在的 key | 404 + `content-type: text/html`（R2 默认 404 页，**不要把 HTML 当 JSON 解析**） |
| 需要登录/防盗链 | **无**（无鉴权、无 Referer 校验、无 Cookie；公开 R2） |

包格式：**zip，ZipCrypto 加密，deflate**。实测第一个真实文件条目的 local header：`general purpose flag = 0x0001`（encrypted）、`method = 8`（deflate）。
密码是**内置常量** `x1x.tw`（源码 `updater.py:23`），刻意不写进清单（`updater.py:8-11` 注释说明）。→ 不提供密码无法解包；解包必须用支持 ZipCrypto 的工具（Windows 自带 tar.exe / bsdtar 可以，源码 `updater.py:326-341`）。

解包命令（源码 `updater.py:340`）：

```
bat
%SystemRoot%\System32\tar.exe -x -f "<下载的>.zip" -C "<临时目录>" --passphrase x1x.tw
```

校验与落位（源码 `updater.py:336-364, 532-540`）：

1. 解到 `<父目录>\<folder>.unpack-<pid>\`
2. 比对「文件清单+大小」与 zip 条目完全一致（防半包 / 被杀软删文件）
3. `Directory.Move` 到 `<父目录>\<folder>`（**永远不要覆盖旧目录**：Windows 会锁住运行中的 dll/py）
4. 校验 `build\build.json` 的 package_version、`runtime\pythonw.exe`、`core\app\deskrawl_helper.py`
5. 复制 CARRY 清单（`updater.py:30-34`）：core/runtime/autoloop、core/runtime/runs.jsonl、core/runtime/drops.json、core/helper/runtime/token、game_dir.txt、core/runtime/update_dir.json、core/runtime/lineage、core/runtime/ports.json、core/helper/runtime/remote
6. 写 `core/runtime/updated_from.json`（新目录）/ `core/runtime/moved_to.json`（旧目录），启动器据此转发到新目录（源码 `deskrawl_helper.py:5589-5598`）

### 5.4 失败面清单

| 现象 | 依据 | 建议处理 |
|---|---|---|
| DNS 解析失败（r2.dev 被过滤） | 源码 `updater.py:173-175` | 提示允许 r2.dev，可重试 |
| 代理 / PAC 配置错误 | 源码 `updater.py:169-172` | 提示检查 Windows 代理 |
| TLS 证书失败（时钟 / 杀软中间人） | 源码 `updater.py:156-165` | 提示校时 / 加白 |
| 连接被防火墙拦（10013/10053/10054/10061） | 源码 `updater.py:178-186` | 提示把 runtime\pythonw.exe 加白 |
| HTTP 4xx/5xx | 源码 `updater.py:150-151` | 稍后重试 |
| 体积 / 哈希不符 | 源码 `updater.py:267-270`（下载即校验，不符删文件） | 重新下载（限次） |
| 清单签名不符 | 源码 `updater.py:78-80` | **不可重试**，报「不是官方发布的包」 |
| 无写权限 | 源码 `updater.py:274-278` | 换目录（不要 Program Files） |
| 磁盘不足 | 源码 `updater.py:262-265` | 提示换盘 |
| 解包 stall（>60 s 无进展） | 源码 `updater.py:288` | 提示杀软白名单 |
| 解包超时（900 s） | 源码 `updater.py:289` | 报失败并清临时目录 |

---
## 6. 在 Sandboxie 沙箱内「落位」

### 6.1 沙箱物理布局与路径换算（实测）

本机 Sandboxie 为便携安装：`D:\SANDBOX\`（注册表 `HKLM\SYSTEM\CurrentControlSet\Services\SbieSvc` 的 `ImagePath = "D:\SANDBOX\SbieSvc.exe"`，实测；`Start.exe` 同目录）。`C:\Windows\Sandboxie.ini`（实测，UTF-16LE）：

```
ini
[GlobalSettings]
FileRootPath=D:\Sandbox\%USER%\%SANDBOX%

[1]
Enabled=y
ConfigLevel=10
AutoRecover=y
BlockNetworkFiles=y
…
```

于是盒子 1 的物理根 = `D:\SANDBOX\43591\1`（实测有 drive\、user\ 等标准子目录）。盒子内看到 `D:\Tool\赛博养蛊\桌破\1\X1X-…`，物理落在：

```
D:\SANDBOX\43591\1\drive\D\Tool\赛博养蛊\桌破\1\X1X-Deskrawl-Helper-1.54.2-build26337088
```

两个方向的换算（已有实现，直接复用）：

| 方向 | API | 实现要点（源码） |
|---|---|---|
| 盒子名 → 物理根 | `SbieApi_QueryBoxPath(box, file, key, ipc, …)` | `SandboxRuntime.cs:17-18`（委托）、`94-112`（Root：`\??\` 前缀裁剪 + QueryDosDevice 把 `\Device\HarddiskVolumeN` 换回盘符） |
| 物理 → 逻辑 | `FileCandidates(logical, box)` / `LogicalPath(path, box)` | `SandboxRuntime.cs:114-141`：`<root>\drive\<盘符>\<去掉冒号后的剩余>`；以及 `<root>\user\current\<用户目录后的剩余>` |
| 逻辑 → 物理 | `ResolveFile(logical, box)` | `SandboxRuntime.cs:141`：两条候选里第一个存在的 |

三个坑（实测）：

1. `SbieApi_QueryBoxPath` 返回真实路径，但 `status.instance.box_root.path` 里的 `%USERNAME%` 是助手自己脱敏的（`deskrawl_helper.py:501-505`）；显示可直接用，做路径计算必须替换回真实用户名。
2. `QueryFullProcessImageName` 拿到的**沙箱进程镜像路径是逻辑路径**（实测：`D:\Tool\赛博养蛊\桌破\1\X1X-Deskrawl-Helper-1.54.2-build26337088\runtime\pythonw.exe`，而宿主上根本没有这个目录）。不要对它做 Directory.Exists，要先经 FileCandidates 映射。
3. 同一目录在「主机」和「沙箱」是两份不同的物理副本，token 也可能不同（实测主机与沙箱 1 的 token 不同）。**配对信息不可跨环境复用**。

### 6.2 落位方案 A：主机侧直接写盒子目录（推荐用于「本机已有副本」）

前置：该盒子里的助手与游戏**必须已退出**（否则文件被锁）。

```
bat
:: 1) 建目录（物理路径）
mkdir "D:\SANDBOX\43591\1\drive\D\Tool\赛博养蛊\桌破\1\X1X-Deskrawl-Helper-1.54.2-build26337088"

:: 2) 镜像复制（源：本机已验证的完整副本）
robocopy "D:\Tool\赛博养蛊\桌破\0\X1X-Deskrawl-Helper-1.54.2-build26337088" ^
         "D:\SANDBOX\43591\1\drive\D\Tool\赛博养蛊\桌破\1\X1X-Deskrawl-Helper-1.54.2-build26337088" ^
         /MIR /R:1 /W:1 /NFL /NDL /NJH /NJS

:: 3) 清理状态，让沙箱内首次启动自己生成 token / ports.json
rmdir /s /q "…\X1X-Deskrawl-Helper-1.54.2-build26337088\core\runtime"
del "…\X1X-Deskrawl-Helper-1.54.2-build26337088\core\helper\runtime\token"
```

- 优点：不需要 Start.exe，速度最快（同盘约秒级）。
- 注意：`AutoRecover=y` 只是「RecoverFolder 内的盒内写入可回收」；**不要**用 Sandboxie 的「删除内容 / 立即清除」，否则助手与状态一起消失。
- 注意：若该盒子配了 `OpenFilePath`（直接访问列表）指向同目录，盒内进程读到的是宿主真实目录，落位到盒子副本会「看不到」。落位前读 `Sandboxie.ini` 对应盒子段的 OpenFilePath（助手侧已实现解析，`deskrawl_helper.py:466-467`）并提示。

### 6.3 落位方案 B：盒内用 Start.exe 执行（推荐用于「从 zip 全新安装」）

`Start.exe` 参数形态（现有封装，源码 `SandboxRuntime.cs:186-201`）：

```
csharp
FileName          = <Sandboxie 安装目录>\Start.exe
ArgumentList[0]   = "/box:" + box        // 主机（无沙箱）时用 "/dfp"
ArgumentList[1]   = "/hide_window"       // 仅 hidden 时追加
ArgumentList[2..] = program 及其参数
WorkingDirectory  = <program 所在目录>
```

实测 `Start.exe` 在 `D:\SANDBOX\Start.exe`；`SandboxRuntime` 依次从注册表 `SbieSvc.ImagePath`、`%ProgramFiles%\Sandboxie-Plus`、`%ProgramFiles%\Sandboxie` 找它（源码 `SandboxRuntime.cs:39-52`）。

盒内执行（推荐写法，`/silent` 不弹窗、`/wait` 等命令结束再返回）：

```
bat
:: 1) 建目录（盒内建 = 写进盒子层）
"D:\SANDBOX\Start.exe" /box:1 /silent /wait cmd.exe /c mkdir "D:\Tool\赛博养蛊\桌破\1\X1X-Deskrawl-Helper-1.54.2-build26337088"

:: 2) 解包（tar 支持 ZipCrypto；密码为内置常量）
"D:\SANDBOX\Start.exe" /box:1 /silent /wait C:\Windows\System32\tar.exe -x -f "D:\Users\43591\Downloads\X1X-Deskrawl-Helper-1.54.2-build26337088.zip" -C "D:\Tool\赛博养蛊\桌破\1" --passphrase x1x.tw

:: 3) 复核
"D:\SANDBOX\Start.exe" /box:1 /silent /wait cmd.exe /c dir "D:\Tool\赛博养蛊\桌破\1\X1X-Deskrawl-Helper-1.54.2-build26337088\build\build.json"

:: 4) 启动（与中控现有启动一致：--open none、隐藏窗口）
"D:\SANDBOX\Start.exe" /box:1 /hide_window "D:\…\X1X-Deskrawl-Helper-1.54.2-build26337088\runtime\pythonw.exe" "D:\…\X1X-Deskrawl-Helper-1.54.2-build26337088\core\app\deskrawl_helper.py" --open none

:: 5) 停止（也可让中控走 HTTP：POST 到 /control/app，body 的 action 为 quit）
"D:\SANDBOX\Start.exe" /box:1 /silent /wait "…\runtime\pythonw.exe" "…\core\app\deskrawl_helper.py" --quit
```

- 优点：写操作全部经 Sandboxie 过滤驱动，盒子有 OpenFilePath 也行为正确；能顺手做盒内自检。
- 缺点：每次 Start.exe 冷启动有额外开销；含中文 / 空格参数**必须**用 ArgumentList 逐参数传，不要拼字符串。
- 权限：Start.exe 以当前用户身份运行；写到 Program Files 会失败（面板文案也建议不要放 Program Files）。

### 6.4 落位后必须做的复核（顺序固定）

1. 三个硬文件存在：`build\build.json`、`runtime\pythonw.exe`、`core\app\deskrawl_helper.py`（源码 `updater.py:536-538`）
2. `package_version` == 期望版本（源码 `updater.py:539-540`）
3. 文件数量 / 大小与 zip 清单一致（若走 zip 路线；源码 `updater.py:293-299` 的 _differ）
4. 官方文件清单（可选）：`build\official.json` 的 files_sha256 与磁盘比对（建议只抽查关键文件；缺失时实测 `status.official.state` 只有 checking）
5. 启动 → 等端口 → 带 token 探活 → 校验 instance.sandbox 与预期盒子一致（源码 `AccountWorkspace.cs:384-390`）

---

## 7. 端口自动识别算法（可 C# 直接实现）

### 7.1 总流程

```
text
for box in ["(主机)"] + Sandboxie.Boxes():
    A. 监听表      GetExtendedTcpTable(AF_INET, TCP_TABLE_OWNER_PID_LISTENER) -> {port: pid}
    B. 端口过滤    port 属于 {8766+10i | i=0..11}        // 控制口集合
    C. PID -> 进程 OpenProcess / QueryFullProcessImageName -> 逻辑镜像路径
    D. 路径 -> 根  两次 Path.GetDirectoryName -> 候选助手根
    E. 归属        SbieApi_QueryProcess(pid) -> boxName（"" = 主机；null = 问不到）
    F. 目录校验    IsHelper(root)（pythonw.exe + core\app\deskrawl_helper.py，走 FileCandidates 映射）
    G. 配对        token = 读 <root>\core\helper\runtime\token；GET /control/status 带 token
    H. 确认        status.instance.sandbox == 期望 box 且 LogicalPath(instance.dir) == root
    I. 落候选      记录 evidence 与置信度
```

### 7.2 每个候选应携带的字段

```
csharp
// 字段名可换项目风格，语义照抄
internal sealed record HelperCandidate
{
    string Box { get; init; }            // "" = 主机，否则 Sandboxie 盒名（来自 SbieApi_QueryProcess）
    int ControlPort { get; init; }       // 8766+10i
    int ApiPort { get; init; }           // control-1（可由 CSP / status 证实）
    int Pid { get; init; }               // 监听方 PID（不是助手全部子进程）
    string LogicalRoot { get; init; }    // 逻辑助手根（镜像路径两次 DirName，或 status.instance.dir）
    string PhysicalRoot { get; init; }   // 物理根（FileCandidates 命中的；主机 = 同 LogicalRoot）
    string Token { get; init; }          // 64 hex；读不到 = ""
    string Version { get; init; }        // status.version（拿不到 = ""）
    bool Running { get; init; }          // 进程 / 端口层面在跑
    bool Paired { get; init; }           // token 探测 200 且 sandbox 匹配
    int Score { get; init; }             // 见 7.4
    string[] Evidence { get; init; }     // 见 7.4
    string[] Conflicts { get; init; }    // 冲突说明（多实例 / 目录不一致 / 端口不符）
}
```

### 7.3 证据来源（按「便宜且可靠」排序）

| # | evidence 标识 | 取法 | 能定什么 | 失效条件 |
|---|---|---|---|---|
| 1 | `pairing-file` | 读 `<root>\core\helper\runtime\token`（SbieApi 映射后） | 有配对信息、得到 token | 目录选错 / 沙箱未映射 |
| 2 | `beacon` | 读 `<root>\core\runtime\instances\box-*.json`（beat 在 120 s 内） | 盒名、control/api、pid、version、规范化 dir | 版本低于 1.33.0 无信标；dir 不匹配会被丢弃 |
| 3 | `control-probe` | `GET /control/status` + token → 200，比对 instance.sandbox / instance.dir | **权威身份** | token 不匹配 → 401 |
| 4 | `listener` | GetExtendedTcpTable 的 LISTENING 记录 | 端口确实在听 | 只看 LISTENING（TIME_WAIT 会误导，实测大量 TIME_WAIT） |
| 5 | `process-path` | QueryFullProcessImageName → 逻辑根；SbieApi_QueryProcess → 盒名 | 端口属于哪个盒子 / 目录 | 沙箱进程路径是逻辑路径，宿主可能不存在 |
| 6 | `ports-json` | 读 `core\runtime\ports.json` | 上次使用的端口对 | 首次运行没有；可能过期 |
| 7 | `no-token-probe` | `GET /` 的 302（#port=）；`GET /i18n.json` 的 CSP（connect-src） | 确认是助手；确认 api 口 | 只能证明「是助手」，不能定身份 |
| 8 | `status-api` | `GET /control/status` 的 api.running / api.port | 证实 api 口已起 | 需 token |

实测的「多进程」细节（**必须按 PID 归并**）：沙箱 1 里同时有 3 个 pythonw.exe（PID 17888 = loop、19008 = 控制、24296 = 数据 API），只有 19008 监听 8776、24296 监听 8775；沙箱 2 同理（6280 控制 8786 / 26752 数据 API 8785 / 29396 = loop）；主机 29084 控制 8766、7368 数据 API 8765。→ 进程遍历只用来补路径，**端口归属以监听表为准**。

### 7.4 打分与判定依据

```
text
Score =
    +40  若 control-probe 通过（200 且 instance.sandbox 与期望盒子一致）
    +20  若 LogicalPath(instance.dir) 与候选 LogicalRoot 一致
    +15  若 beacon 存在且 dir 匹配、beat < 120s
    +10  若 process-path 与 listener 同 PID
    + 5  若 ports.json 的 control 与实测控制口一致
    + 5  若 ApiPort == ControlPort - 1 且 api 端口确实在听
    -50  若 token 读不到
    -30  若同一盒子出现多个满足 1..5 的候选（同盒多实例冲突）

判定：
    Score >= 60 且唯一      -> 自动采用，Evidence 至少含 listener / process-path / pairing-file / control-probe
    30 <= Score < 60 且唯一 -> 采用但标记「需确认」
    多候选 / 同盒冲突        -> 不自动采用，UI 列候选让用户选（附 Evidence 与冲突原因）
    零候选                  -> 走「手动指定目录」，SaveProfile 用同一 IsHelper 校验（源码 AccountWorkspace.cs:246-254）
```

### 7.5 降级与人工覆盖

| 情形 | 降级动作 | 用户可见文案（建议） |
|---|---|---|
| 端口在听但 token 读不到（目录未知） | 标为「未配对」，允许用户「指定助手目录」后重算 | 「端口 8776 上有一个助手在跑，但还没找到它的配对信息。请选择这个环境使用的助手目录。」 |
| 一个盒子多个候选 | 全部列出，默认选版本最高 + 端口最小 | 「这个环境同时有 N 份助手在运行，请选择要使用哪一份。」 |
| 零候选但目录有助手 | 视为未运行，只允许「启动」 | 「已找到助手目录，但它现在没有运行。」 |
| 零候选且无目录 | 引导部署 | 「还没有为这个环境部署助手，点「部署助手」开始。」 |
| 端口在 12 对之外 | 提示不受官方面板支持 | 「助手使用的端口超出官网面板支持范围（8766–8876），网页控制台可能连不上。」 |

### 7.6 实现细节（容易踩的）

1. **GetExtendedTcpTable 行布局**：`MIB_TCPROW_OWNER_PID` 24 字节，localPort 在 +8（网络序，取低 16 位），owningPid 在 +20；表头 4 字节为 count。源码 `SandboxRuntime.cs:143-163`。
2. **只查 IPv4（family=2）**：源码 `SandboxRuntime.cs:147,152` 固定 AF_INET；若出现 ::1 监听会漏检（推断，置信度高：助手固定绑定 127.0.0.1，见 `deskrawl_helper.py:5308`）。
3. **BoxOf 的 null 语义**：null = 问不到 Sandboxie（不算主机）；"" = 确实在主机（源码 `SandboxRuntime.cs:85-92`）。别混用。
4. **IsHelper 必须走 ResolveFile**，否则沙箱目录永远判 False。
5. **探测超时要短**：`/control/status` 会被 `_STATUS_LOCK` 串行化（源码 `deskrawl_helper.py:5210-5212`），实测单次 12 s 超时够用（`AccountWorkspace.cs:47`）；批量扫描建议 2–3 s 并并行。
6. **Host 头必须写对**：用 `http://127.0.0.1:<port>`（或 localhost），别用机器名（否则 421）。

---

## 8. 一键部署状态机

### 8.1 状态与字段（与前端 deploy 占位对齐）

前端已有占位（源码 `WebAssets\client.js:23`），包含 target / packagePath / port / busy / candidates / steps / message / result 八个字段。

建议的 C# 侧：

```
csharp
enum DeployPhase { Idle, Running, Failed, Done, Cancelled }

sealed record DeployStep(string Id, string Title,
                         string State,     // pending | running | ok | failed | skipped
                         string Detail, string ErrorCode,
                         DateTime? StartedAt, DateTime? EndedAt);

sealed record DeployState(string Target,   // "host" | "box:1"
                          string PackagePath, int Port, DeployPhase Phase,
                          List<DeployStep> Steps, string Message, DeployResult? Result);
```

### 8.2 步骤定义

| # | step id | 标题（中文） | 动作 | 成功判据 | 失败码 |
|---|---|---|---|---|---|
| 0 | `preflight` | 检查目标环境 | 枚举盒子；校验目标盒子启用；读 Sandboxie.ini 的 OpenFilePath；确认 Start.exe / SbieDll 可用 | 目标存在且 SandboxRuntime.Available | E_ENV_UNKNOWN / E_BOX_MISSING / E_SANDBOXIE_MISSING / E_SANDBOXIE_UNAVAILABLE |
| 1 | `detect-existing` | 检查是否已有实例 | 走第 7 节扫描；目标盒子是否已有 control-probe 通过的实例 | 无在跑；有则转「更新 / 跳过」分支 | E_ALREADY_RUNNING |
| 2 | `resolve-source` | 确定安装包来源 | ①用户给的本地 zip / 目录 ②本机同版本副本 ③线上清单 | 得到 PackagePath + version | E_NO_SOURCE |
| 3 | `fetch-manifest` | 获取更新信息 | GET latest.json（带 ?t= 与 UA） | 200 + 验签通过 | E_NETWORK / E_MANIFEST_BAD / E_SIGNATURE |
| 4 | `download` | 下载安装包 | 流式下载 + 边下边算 sha256 + 进度 | size / sha256 匹配 | E_DOWNLOAD / E_HASH_MISMATCH / E_DISK_FULL |
| 5 | `unpack` | 解压安装包 | tar --passphrase 到 .unpack-<pid>，比对清单，Move 到 <folder> | 清单一致 + 三硬文件存在 | E_UNPACK / E_UNPACK_INCOMPLETE / E_NO_TAR / E_LOCKED |
| 6 | `place` | 落位到目标环境 | 主机 robocopy /MIR；沙箱写盒子目录或 Start.exe 盒内解包 | 目标物理根下三硬文件可读 | E_ACCESS_DENIED / E_LOCKED / E_DISK_FULL |
| 7 | `verify-install` | 校验安装 | package_version 匹配；IsHelper 通过；可选官方清单抽查 | 全部通过 | E_VERSION_MISMATCH / E_UNPACK_INCOMPLETE |
| 8 | `pair` | 读取 / 生成配对信息 | 读 token；不存在则标记「待首次启动生成」 | token 合法或已标记 | E_PAIRING |
| 9 | `start` | 启动助手 | SandboxRuntime.Start(box, pythonw.exe, helper.py 与 --open none, hidden:true) | 进程出现（按镜像路径 + 盒名匹配） | E_START_FAILED / E_SANDBOXIE_UNAVAILABLE |
| 10 | `wait-port` | 等待端口就绪 | 轮询监听表里的 8766+10i（500 ms 间隔，最长 60 s）；同步读 ports.json | 出现控制端口 | E_PORT_NOT_READY |
| 11 | `confirm` | 确认身份 | 带 token 请求 /control/status，instance.sandbox 与目标盒一致、LogicalPath(instance.dir) 与目录一致 | 一致 | E_PAIRING / E_PORT_CONFLICT / E_IDENTITY_MISMATCH |
| 12 | `done` | 完成 | 保存 AccountProfile（目录 + 指纹）+ 刷新账号列表 | — | — |

设计约束：

- **可中断**：每步之间检查取消；中断后保留已下载 zip（可复用），但清理 `.unpack-*` 临时目录（源码 `updater.py:529` 的做法）。
- **幂等**：place 用 /MIR；unpack 目标已存在且版本一致 → skipped。
- **串行**：同一目标环境同时只允许一个部署任务（SemaphoreSlim 或 per-target 队列；助手自身用命名互斥量保证「一个目录一个沙箱一个实例」，源码 `deskrawl_helper.py:5425-5445`）。
- **不覆盖运行中的目录**：先停助手（POST 到 /control/app，body 的 action 为 quit；或 --quit），确认进程消失后再落位。

### 8.3 失败分类与中文文案（用户可读）

| 失败码 | 触发条件 | 用户文案（建议） | 技术细节字段 | 可重试 |
|---|---|---|---|---|
| E_ENV_UNKNOWN | 目标环境无法识别 | **未识别到目标环境**：请先选择要部署到「主机」还是某个沙箱。 | target | 否 |
| E_BOX_MISSING | Boxes() 没有该盒名 | **沙箱不存在**：Sandboxie 里没有名为「{box}」的沙盘，请先在 Sandboxie 中创建。 | box, boxes[] | 否 |
| E_SANDBOXIE_MISSING | 找不到 Start.exe / SbieDll.dll | **没有检测到 Sandboxie**：请在运行设置里指定 Sandboxie 安装目录，或先安装 Sandboxie-Plus。 | 候选路径 | 否 |
| E_SANDBOXIE_UNAVAILABLE | 找得到安装但 SbieApi_QueryProcess 不可用 | **无法读取沙箱信息**：Sandboxie 安装不完整（缺少 SbieDll.dll 或驱动未启动），请修复安装后重试。 | startPath, dllError | 是 |
| E_NO_SOURCE | 无本地包且线上不可用 | **找不到可安装的助手包**：请选择本机助手压缩包，或确认网络后重试。 | — | 是 |
| E_NETWORK | DNS / 连接 / 超时 / 代理 / 证书 | **下载失败（网络原因）**：请检查网络与代理设置；若开了 DNS 过滤，请允许 r2.dev；稍后再试。 | winhttpCode, detail | 是 |
| E_MANIFEST_BAD | 清单 200 但非法 JSON / 字段缺失 | **更新信息无法解析**：服务器返回的内容不是有效的更新信息，请稍后重试。 | httpStatus, bodyHead | 是 |
| E_SIGNATURE | Ed25519 校验失败 | **安装包来源不可信**：更新信息签名不符，已拒绝安装（不是 X1X 官方发布的文件）。 | version, releasedAt | 否 |
| E_DOWNLOAD | HTTP 4xx/5xx、连接中断 | **下载安装包失败**：服务器返回 HTTP {code}，请稍后重试。 | httpStatus | 是 |
| E_HASH_MISMATCH | size / sha256 不符 | **安装包校验失败**：下载的文件与官方信息不一致（可能网络损坏），已删除，请重新下载。 | expect, actual | 是 |
| E_DISK_FULL | 写盘空间不足 | **磁盘空间不足**：目标盘剩余空间不够，请清理或换一个盘。 | drive, needBytes | 否 |
| E_UNPACK | tar 退出码非 0 / 内容与清单不符 | **解压失败**：安装包可能损坏，或被杀毒软件拦截。请把助手目录加入杀软白名单后重试。 | tarExit, missing / size / extra | 是 |
| E_NO_TAR | 系统无 tar.exe | **系统缺少解压组件**：需要 Windows 10 1803 及以上（自带 tar.exe），或改用已解压的助手目录。 | tarPath | 否 |
| E_UNPACK_INCOMPLETE | 三硬文件缺一 | **安装包不完整**：缺少 {file}，请重新下载。 | missing[] | 是 |
| E_ACCESS_DENIED | 无写权限（UnauthorizedAccess / 10013） | **权限不足**：无法写入 {path}。请选择普通目录（不要放在 Program Files）。 | path, hresult | 是 |
| E_LOCKED | 文件被占用 | **目标文件正在被使用**：请先在该环境关闭助手（和游戏）后再部署。 | path, holders[] | 是 |
| E_VERSION_MISMATCH | package_version 与期望不同 | **版本不一致**：解出的助手版本是 {actual}，与预期的 {expect} 不同。 | expect, actual | 是 |
| E_ALREADY_RUNNING | 该环境已有 control-probe 通过的实例 | **该环境已经有助手在运行**：如需更新请先关闭它；或直接打开「助手设置」使用现有实例。 | port, pid, version | 否 |
| E_PAIRING | token 读不到 / 401 | **配对失败**：读不到或读错了该助手的配对信息。请在「环境设置」里重新选择实际使用的助手目录。 | tokenFile, httpStatus | 是 |
| E_PORT_CONFLICT | 12 对端口全被占用 | **端口被占用**：8766–8876 全被其他程序或 Windows 保留。请关闭不用的助手；被 Hyper-V / WSL / Docker 保留时可用 netsh interface ipv4 show excludedportrange protocol=tcp 查看。 | pairs[], excluded | 是 |
| E_START_FAILED | 启动抛错或进程立刻退出 | **助手启动失败**：进程启动后立刻退出，请查看 core\runtime\helper-app.log 与 helper-api.log。 | exitCode, logTail | 是 |
| E_PORT_NOT_READY | 60 s 内没有控制端口出现 | **启动后端口不通**：助手没有在 60 秒内开始监听。常见原因是杀软拦截、缺少文件或已有实例占用端口。 | waitedMs, logTail, portsJson | 是 |
| E_IDENTITY_MISMATCH | 200 但 instance.sandbox / dir 不符 | **连到了别的助手**：{port} 上的助手属于「{actualBox}」，不是本次目标环境。已停止，请刷新后重试。 | port, actualBox, actualDir | 是 |

### 8.4 失败信息展示原则

- 每条失败 = 一句人话（what）+ 一句怎么办（how）+ 可折叠技术细节（detail），与助手自身 Bi / failure() 风格一致（源码 `updater.py:139-189`）。
- 失败**停在出错的那一步**：之前的步骤保持 ok，后续保持 pending，不整体回滚 UI（README 已承诺：「失败会停在出错的那一步并说明原因」）。
- 「不可重试」的失败（E_SIGNATURE、E_ENV_UNKNOWN、E_BOX_MISSING、E_ALREADY_RUNNING）不显示「重试」，只显示「去处理」。

---

## 9. 证据索引

### 9.1 中控源码

| 主题 | 位置 |
|---|---|
| 助手根判定 | `中控源\AccountWorkspace.cs:202-203` |
| 版本解析 | `AccountWorkspace.cs:220-224` |
| token 读取 / 校验 | `AccountWorkspace.cs:225-230` |
| 配对指纹 | `AccountWorkspace.cs:231-235` |
| 端口扫描与归属 | `AccountWorkspace.cs:182-190` |
| 控制口集合 | `AccountWorkspace.cs:201` |
| 助手启动（--open none） | `AccountWorkspace.cs:341-342` |
| 连接与 401 / 沙箱 / 目录校验 | `AccountWorkspace.cs:362-394` |
| 配置保存校验 | `AccountWorkspace.cs:246-263` |
| 环境 id / 显示名 | `AccountWorkspace.cs:72-73` |
| 已安装副本来源（引擎 api/config） | `AccountWorkspace.cs:88-98, 156-180` |
| SbieApi 委托与探测顺序 | `SandboxRuntime.cs:12-68` |
| 盒子枚举 / 进程归属 | `SandboxRuntime.cs:70-92` |
| 盒子路径 / 逻辑-物理映射 | `SandboxRuntime.cs:94-141` |
| 监听表 | `SandboxRuntime.cs:143-163` |
| 进程镜像路径 | `SandboxRuntime.cs:165-176` |
| Start.exe 参数封装 | `SandboxRuntime.cs:178-202` |
| 内嵌页面与转发（两端口 + token + Origin） | `HelperPane.cs:48, 63-64, 96-98, 116-117` |
| 部署状态占位 | `WebAssets\client.js:23` |

### 9.2 助手源码（本机副本 1.54.2-build26337088）

| 主题 | 位置 |
|---|---|
| 版本号 | `core\app\deskrawl_helper.py:304` |
| token 路径 / 生成 / 校验 | `core\app\deskrawl_helper.py:63, 1514-1522` |
| 端口对 / ports.json / 信标 | `core\app\deskrawl_helper.py:305-317` |
| 沙箱自识别（SbieApi_QueryProcess） | `core\app\deskrawl_helper.py:809-829, 863-899` |
| package_mode（build.json 决定） | `core\app\deskrawl_helper.py:113-128` |
| /control/status 的 instance | `core\app\deskrawl_helper.py:3127-3133` |
| 鉴权顺序（Host / Origin / token） | `core\app\deskrawl_helper.py:5072-5087` |
| 静态资源 + CSP（泄露 api 口） | `core\app\deskrawl_helper.py:5263-5278` |
| 信标读写 | `core\app\deskrawl_helper.py:5354-5390` |
| 单实例互斥量 | `core\app\deskrawl_helper.py:5425-5445` |
| 端口挑选 | `core\app\deskrawl_helper.py:5447-5462` |
| 启动流程 / --port / --quit | `core\app\deskrawl_helper.py:5573-5668` |
| 数据 API 鉴权与路由 | `core\helper\helper\server.py:9-40, 150-181` |
| 更新清单 / 签名 / 密码 / 下载 / 解包 / 落位 | `core\app\updater.py:18-36, 69-93, 240-271, 326-364, 532-568` |

### 9.3 本次实测记录（2026-11-05）

1. 端口探测：`127.0.0.1` 上 8765 / 8766 / 8775 / 8776 / 8785 / 8786 在 LISTENING（3 个实例）。
2. 无 token：`GET /control/status` → 401 `{"error": "bad_or_missing_token"}`（带 nosniff / no-referrer / no-store）；`GET /` → 302（Location 带 `#port=8776`）；`GET /v1/health` → 401 紧凑 JSON；Host 错 → 421 bad_host；OPTIONS + x1x Origin → 204。
3. 带 token：三实例 instance 见 §4.3；version=1.54.2；api.running=true。
4. CSP 泄露：`GET /i18n.json`（无 token）响应头 connect-src 含 `http://127.0.0.1:8765`。
5. x1x.tw 页面 CSP 只允许 12 个 control 端口（8766–8876）。
6. 清单与包：latest.json 200（字段见 §5.2）；zip 206 / application/zip / 20571590 字节；条目 flag=0x1（ZipCrypto）+ method=8。
7. 剥离实验：见 §1.3（R1 崩于 hostctl，R2 控制面 200 但 api.running=false，R3 v1/health 200）。
8. 路径实验：沙箱进程镜像路径是逻辑路径；盒子 1 物理根 `D:\SANDBOX\43591\1`；沙箱 1 / 2 的 token 与主机不同。

### 9.4 明确的推断与不确定项

| 项 | 结论 | 等级 / 依据 |
|---|---|---|
| 12 对端口是硬上限 | 是（超出后 x1x.tw 面板被 CSP 拦） | 实测 CSP + 源码 PORT_PAIRS；未来是否扩容未知 |
| 1.24.0 以下版本无 instance.sandbox | 是 | 源码注释：a helper before 1.24.0 says none（空字符串） |
| 1.33.0 以下版本无信标 | 是 | 源码 `deskrawl_helper.py:313` 注释 |
| 官方无公共直链下载 | 是 | 实测页面 + 面板文案；Discord 帖的长期稳定性属外部因素，不建议依赖 |
| 沙箱 token 与主机 token 独立 | 实测观察到不同，但**不是必然** | 取决于副本来源；实现上不要假设「同目录同 token」 |
| AutoRecover=y 是否会在盒子关闭时回收助手文件 | 未知 | 本次未做关闭 / 重开盒子的实验；实现时应显式校验「重开盒子后助手目录仍在」 |
| 方案 B（盒内 tar 解包）在本机成功过 | 未实测（避免影响运行中的 3 个实例） | 推断：tar.exe 是系统组件、只读可用，写在盒内层；置信度高但未验证 |
| 方案 A（直接写盒子目录）在本机成功过 | 间接实测（盒子 1 / 2 里确有 1.54.2 的盒内副本） | 中 |

### 9.5 给实现者的 8 条硬性提醒

1. 判断「是不是助手」用两个文件（`runtime\pythonw.exe` + `core\app\deskrawl_helper.py`），别用目录名。
2. 判断「哪个助手」只能靠 token + `/control/status`；无 token 探测只能证明「是助手」。
3. 沙箱路径一律走 `SbieApi_QueryBoxPath` 映射；`instance.dir` 是逻辑路径。
4. 端口归属必须按监听表（LISTENING）+ PID 归并（一个盒子 3 个 pythonw）。
5. 安装 = 整包落位，别做精简；精简会以 `ModuleNotFoundError` 形式在启动时才暴露。
6. 下载必须验签 + 验 sha256；包是 ZipCrypto，密码 `x1x.tw`，用 `tar --passphrase` 解。
7. 永远不要覆盖运行中的目录（Windows 锁文件）：先 quit 再落位到新目录。
8. token 是完全控制权：不进日志、不进诊断包、不进任何上报。
