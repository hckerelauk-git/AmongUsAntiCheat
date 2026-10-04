# Apex Cheat Ender（ACE）· 项目分析与故障定位报告

- **分析对象**：`E:\干活\projects\among-us-ace`（Among Us 客户端反作弊 BepInEx 插件）
- **分析日期**：2026-10-02
- **分析深度**：标准
- **触发诉求**：「游戏主界面不见了」，要求检查 mod 文件与相关报错，定位并修复根因

---

## 0.1 【最终结论 · 2026-10-02 16:20 更新】两个根因，都不是插件

> ### 根因 A —— 「主界面不见了」= 游戏分辨率被设成 **144 × 1 像素**
>
> 位置：**注册表 `HKCU\Software\Innersloth\Among Us`**（Unity PlayerPrefs，**不在** `settings.amogus` 里）
>
> ```
> Screenmanager Resolution Width_h182942802    = 144
> Screenmanager Resolution Height_h2627697771  = 1      ← 窗口高度只有 1 像素
> Screenmanager Fullscreen mode_h3630240806    = 3      ← 窗口模式
> Screenmanager Resolution Window Width = 144 / Height = 1
> ```
>
> **主菜单一直在正常渲染，只是窗口高度 1 像素，肉眼什么都看不到。**
> 用户截图里那条「压扁的标题栏」就是这个。
> **已修复**：`1920×1080` / `Fullscreen mode=1`（无边框全屏）/ `Use Native=1` / 窗口位置 0,0。
> 原值备份：`E:\干活\.workbuddy-ai\tmp\amongus_playerprefs_backup.json`
>
> ### 根因 B —— `EOSSDK` 加载失败 = **System32 里 VC++ 运行库的 ACL 被改坏**
>
> `Player.log:59` 报 `WinError 126`，**重装游戏无效**（因为问题在 System32，不在游戏目录）。
>
> **证据（依赖关系完全对应，可复核）**：
>
> | `Plugins/x86_64` 下的 dll | VC 运行库依赖 | 修复前 | 修复后 |
> |---|---|---|---|
> | `EOSSDK-Win64-Shipping.dll` | msvcp140 + vcruntime140 + vcruntime140_1 | ❌ | ✅ |
> | `sentry.dll` | msvcp140 + vcruntime140 + vcruntime140_1 | ❌ | ✅ |
> | `tess2.dll` | vcruntime140 | ❌ | ✅ |
> | `XGamingRuntimeThunks.dll` | vcruntime140 | ❌ | ✅ |
> | `Microsoft_Xbox_Services_141_GDK_C_Thunks.dll` | msvcp140 + vcruntime140 | ❌ | ✅ |
> | `discord_game_sdk.dll` / `steam_api64.dll` / `Rewired_*.dll` | **无** | ✅ | ✅ |
>
> **坏在哪**：`C:\Windows\System32` 下 4 个文件被切断了 ACL 继承、删掉了 `BUILTIN\Users:(RX)`
> 与 `ALL APPLICATION PACKAGES:(RX)`，只剩 SYSTEM / Administrators：
>
> `msvcp140.dll`、`vcruntime140.dll`、`concrt140.dll`、`msvcp140_atomic_wait.dll`
>
> 非提权进程读不了 → `LoadLibraryW` 返回 **`WinError 5 (ACCESS_DENIED)`** → 依赖它们的 dll 全部加载失败。
>
> **对照（正常文件）**：`msvcp140_1.dll`、`msvcp140_2.dll`、`vcruntime140_1.dll`、`ucrtbase.dll`
> 都有 `BUILTIN\Users:(I)(RX)` 继承。**文件内容没被改**——8 个运行库 mtime 全是
> `2026-05-27 04:59`（VC++ Redist 安装时间），**改 ACL 不更新 mtime**。
>
> **临时修复（无需管理员，已实测生效）**：把 x64 的 `msvcp140.dll` + `vcruntime140.dll`
> 放进**游戏根目录**——加载器「应用目录优先」，直接绕开 System32 的坏副本。
> 原 32 位文件备份为 `*.x86.bak`。
>
> **永久修复（需管理员）**：`icacls "C:\Windows\System32\<文件名>" /reset`（恢复继承）。
>
> ✅ **已于 16:22 执行完毕并验证**：4 个文件 `/reset` 全部 `exitcode=0`，
> `BUILTIN\Users:(I)(RX)` 与 `ALL APPLICATION PACKAGES:(I)(RX)` 均已恢复；
> 非提权进程复验 6 个运行库全部可读，`EOSSDK / sentry / concrt140` 等全部加载 **OK**。
>
> **⚠️ 最可能的肇事者：本机同时装了 360 安全卫士与火绒两套安全软件**
> （`360tray.exe`/`360TptMon.exe` + `HipsDaemon.exe`/`HipsTray.exe`/`wsctrlsvc.exe`）。
> 两套安全软件同时驻留是系统文件权限被反复篡改的高危因素；被改的 4 个 dll
> 又正好是「游戏报缺 DLL」时各类「DLL 修复工具」的标准作业对象。
> **建议二选一卸载，只保留一套。**
>
> > ⚠️ **Steam 验证文件完整性会把 32 位运行库放回游戏根目录，会再次踩坑——别再验证。**

---

## 0. 结论先行（初版，保留）

> **ACE 插件本身没有缺陷，也不是「主界面不见了」的原因。**
> 插件 17 个 Harmony 补丁全部挂载成功、UI 正常创建、构建 0 警告 0 错误、9 项单元测试全通过。
>
> **真正的卡点在游戏侧，且与插件无关：**
> `EOSSDK-Win64-Shipping.dll` 加载失败 → `EOSManager` 初始化异常 → 游戏退回离线 →
> 主菜单被 **`DisconnectPopup`（断线弹窗）** 盖住，同时后台协程 `CoWaitForDateConfirmation`
> 无限打印 `Waiting for time stamp`。
>
> **决定性反证：** 这份行为在**上一场游戏会话**（`Player-prev.log`，12:41）中**完全一致**——
> 同样第 59 行 EOSSDK 报错、同样 18 处 `DisconnectPopup`、同样 1683 次 `Waiting for time stamp`，
> 而那场会话是**正常退出**的（日志尾部是完整的内存 Profiler 转储，无崩溃）。
> 也就是说：**EOSSDK 失败是这台机器的既有基线，不是插件引入的回归。**

---

## 1. 项目概览

| 项 | 值 |
|---|---|
| 项目名 | Apex Cheat Ender（ACE） |
| 定位 | Among Us 客户端反作弊模组，单 DLL 丢进 `BepInEx/plugins` 即用 |
| 技术栈 | C# / .NET（BepInEx 6 · IL2CPP）+ HarmonyX 补丁 |
| 规模 | 48 个文件 / 761.8 KB；代码 36 文件 / **7151 行**（全 C#） |
| Git | `main` @ `d7a01b0`，21 次提交，工作区干净，远端 GitHub |
| 最近提交 | `feat: 侧边栏加图标改版 + 同装 ACE 玩家互认与名字标记`（10-02 11:57） |
| 构建 | `dotnet build -c Release` → **0 警告 / 0 错误**，并自动部署到游戏插件目录 |
| 测试 | 零依赖控制台 runner，**9/9 通过** |

---

## 2. 证据链（可复核）

### 2.1 插件侧：完全健康

`BepInEx/LogOutput.log`（12:58 那场带插件的运行）：

```
[Info   :ApexCheatEnder] [补丁] 帧驱动(Canvas) 已挂载
[Info   :ApexCheatEnder] [补丁] RPC 洪水防护 已挂载 -> PlayerControl.HandleRpc(2 参数, Prefix)
[Info   :ApexCheatEnder] [补丁] ACE 互认(握手) 已挂载 -> PlayerControl.HandleRpc(2 参数, Prefix)
[Info   :ApexCheatEnder] [补丁] 主菜单背景(Start) 已挂载 -> MainMenuManager.Start(0 参数, Postfix)
[Info   :ApexCheatEnder] Apex Cheat Ender 加载完成。按 F8 可切换监控面板。
[Info   :ApexCheatEnder] [UI] 画布已创建。
[Info   :ApexCheatEnder] [UI] 首帧渲染完成。
[Info   :ApexCheatEnder] [桌面动画] 播放结束。
```

只有 **2 条 Warning**，且都是**设计内的优雅降级**（`AntiCheatPlugin.TryPatch` 第 140-144 行
显式处理 `TargetMethod()` 返回 null 的情况，不影响其余补丁）：

| 补丁 | 原因 | 处理 |
|---|---|---|
| `MainMenuArtUpdatePatch` | 当前版本 `MainMenuManager` 无 `Update` 方法 | 跳过，Start 补丁仍在 |
| `AcePresenceNamePatch` | 当前版本 `PlayerControl` 无 `Update` 方法 | 跳过，握手补丁仍在 |

日志中大量 `[Warning: HarmonyX] AccessTools.GetTypesFromAssembly` 与
`System.TypeLoadException` 是 **IL2CPP 程序集反射的固有噪声**（`Il2Cppmscorlib`、
`UnityEngine.CoreModule`、`VirtualTexturingModule`），与 ACE 代码无关。

### 2.2 游戏侧：EOSSDK 加载失败（真正的卡点）

`%LOCALAPPDATA%Low\Innersloth\Among Us\Player.log`：

```
59: Network > EOSManager > DLL Not Found: Unable to load DLL 'EOSSDK-Win64-Shipping'.
    ... Failed to open the requested dynamic library (0x06000000) - 找不到指定的模块。(WinError:0000007e)
    at EOSManager:InitializePlatformInterface()
    at EOSManager:Awake()

Exception: Failed to initialize platform: NotFound
302: Begin async loading MainMenu
621: MainMenuManager.RunStartUp beginning
646: DisconnectPopup:DoShow()
```

以及贯穿全篇的死循环协程：

```
Waiting for time stamp
  at <CoWaitForDateConfirmation>d__14:MoveNext()
```

### 2.3 决定性对照实验

| 指标 | 上一场会话 `Player-prev.log`（12:41） | 当前会话 `Player.log`（13:04） |
|---|---|---|
| EOSSDK 报错行号 | **59** | **59**（完全一致） |
| `DisconnectPopup` 出现次数 | 18 | 更多 |
| `Waiting for time stamp` | 1683 | 14918 |
| `MainMenuManager.RunStartUp` | 621 行，正常 | 621 行，正常 |
| 日志结尾 | **完整内存 Profiler 转储 = 干净退出** | 仍在等待循环中 |

**两场会话的结构完全同构。** 前一场是干净退出（说明当时游戏是可玩/可关闭的），
后一场只是等待时间更长。→ **EOSSDK 失败不是插件引入的回归，是本机既有基线。**

### 2.4 一个确凿的异常（但不是本次故障根因）

游戏根目录架构实测：

```
x64(64位)  Among Us.exe
x64(64位)  GameAssembly.dll
x64(64位)  UnityCrashHandler64.exe      ← 游戏是纯 64 位
x64(64位)  UnityPlayer.dll / baselib.dll / winhttp.dll
x86(32位)  msvcp140.dll       436624 字节   ← 架构错误
x86(32位)  vcruntime140.dll    76152 字节   ← 架构错误
```

`Among Us_Data/Plugins/x86_64/` 下 10 个 dll **全部 x64**（含 `EOSSDK-Win64-Shipping.dll`）。

这两个 32 位运行库躺在 64 位游戏的根目录，Windows 加载依赖时**应用目录优先**，
会遮蔽 System32 的 64 位版本。此前曾改名处理过，但**被恢复过两次**（09:47、12:56），
应是「Steam 验证游戏文件完整性」所致。

> ⚠️ **诚实说明**：这一项**此前被我判定为根因，现已推翻**。因为 12:41 那场「正常退出」的
> 会话里，这两个 32 位文件同样在位，EOSSDK 同样报 126。所以它是**应当清理的错误状态**，
> 但**不是**「主界面不见了」的充要原因。

---

## 3. 代码质量体检

### 3.1 六维评分

| 维度 | 分 | 依据 |
|---|---|---|
| **健壮性** | 9 | 每个补丁独立 `try/catch`；`TryPatch` 单点失败不影响其余；P/Invoke 回调全部包 try-catch 并静态保活委托（`DesktopSplash._wndProcKeepAlive`） |
| **可维护性** | 9 | 中文注释密度高，且**解释「为什么」而非「是什么」**（如 `PAINTSTRUCT` 不写死 `Size` 的原因、GDI 函数在 gdi32 而非 user32） |
| **性能** | 8 | 帧刷新 5Hz 节流、`StringBuilder` 复用、GDI 句柄全局复用、按需重绘、渐变画刷缓存 |
| **测试覆盖** | 5 | 仅 9 项测试，且只覆盖 `src/Core` 下 4 个纯逻辑文件（`GameVec2`/`PlayerTrack`/`Violation`/`PlayerVerdict`）；UI、补丁、AI 客户端**零覆盖** |
| **工程化** | 8 | 有 GitHub Actions CI、有独立测试工程、构建后自动部署到游戏目录；但无版本号自动 bump、无发布产物归档 |
| **安全性** | 7 | 扫描未发现硬编码密钥；`Core/Ai` 走用户自备 API Key 配置。风险点见 §4 |

**综合：7.7 / 10** —— 工程质量明显高于同类模组项目，短板集中在测试覆盖。

### 3.2 值得肯定的设计

1. **补丁优雅降级**（`AntiCheatPlugin.cs:140-144`）：游戏更新导致方法改名时，只丢一个检测，
   不会整套瘫痪。这是本次排查中最有价值的特性——它让「插件坏了」和「游戏版本不匹配」
   在日志里一眼可辨。
2. **帧驱动三入口冗余**（`FrameDriverPatch.cs`）：`Canvas.SendWillRenderCanvases` +
   `HudManager.Update` + `SplashManager.Update`，并做了帧去重。注释里明确记录了
   「动态注册进 il2cpp 的托管组件收不到 Update 消息」这一踩坑结论。
3. **桌面动画走独立 STA 线程**（`DesktopSplash.cs:322-328`）：不阻塞 Unity 主线程，
   这条如果做错会直接导致游戏卡死——已确认实现正确。
4. **P/Invoke 三条铁律**写进了文件头注释，且 `PAINTSTRUCT` 按架构推导尺寸（第 131-148 行），
   避免了 x64 下越界写栈的经典崩溃。

### 3.3 风险清单

| 级别 | 现象 | 证据 | 影响 | 建议 |
|---|---|---|---|---|
| **中** | 测试只覆盖 4 个纯逻辑文件，UI/补丁/AI 全裸奔 | `tests/ApexCheatEnder.Tests.csproj:31-35` 只 `Compile Include` 了 4 个 `src/Core` 文件 | 重构 UI 或改补丁签名时无回归网 | 优先补 `AcePresence.TryAccept`、`RpcFloodGuard` 的纯逻辑测试 |
| **中** | `MainMenuArt.FindBackgroundRenderer` 兜底策略会命中任意 `SpriteRenderer` | `src/UI/MainMenuArt.cs:172,180` 名字没匹配上就返回**层级里第一个** SpriteRenderer | 极端情况下可能把非背景对象（图标/按钮）换成整张背景图 | 增加尺寸/比例校验，或失败时直接放弃而非兜底 |
| **中** | 游戏根目录有 32 位 `msvcp140.dll` / `vcruntime140.dll` | 实测架构（§2.4） | 污染 64 位进程的依赖解析；且**Steam 验证会反复恢复**，形成死循环 | 用 64 位版本**覆盖**（不要删除，删除会被 Steam 判定缺失而恢复） |
| **低** | `DesktopSplash` 的 `_hwnd`、`_startTick` 等状态是进程级 static，线程内读写无同步 | `src/UI/DesktopSplash.cs:110-115` | 当前只会 `Show()` 一次，无实际竞争；若将来支持重复播放需加锁 | 加 `_started` 守卫已足够，备注即可 |
| **低** | `MonitorPanel.Toggle()` 只改 `_visible`，未同步回配置 | `src/UI/MonitorPanel.cs:224-229` | F8 隐藏后重启游戏又会显示 | 可接受（属即时开关语义），或写入 `ShowOverlay` 配置 |

### 3.4 未发现的预期问题（已排除）

- ❌ 无任何 Prefix 会误吞游戏 RPC：`AcePresenceRpcPatch`（`src/Patches/AcePresencePatch.cs:39`）
  仅对 `callId == 0xF0` 返回 `false`，其余一律放行；`RpcFloodGuard` 默认关闭。
- ❌ 桌面动画不会遮挡游戏：位置在**右下角** `work.Right - 384 - 16`（`DesktopSplash.cs:359-360`），
  尺寸 384×104，且 `WS_EX_TOPMOST | WS_EX_TOOLWINDOW`，播完即销毁。
- ❌ ACE 画布不会吞点击：`GraphicRaycaster.enabled = false`（`AceUiRoot.cs:63`）。
- ❌ 监控面板不会默认盖住主菜单：右上角 348×232，且延迟 4.6s、可 F8 关闭。

---

## 4. 修改内容

**本次未改动任何源码。** 理由：

1. 构建与测试均通过，不存在需要修复的编译/逻辑错误；
2. 故障的因果链已经指向游戏侧（`EOSManager`），改插件代码**不可能**修复它；
3. 在没有可复现环境的前提下改代码，属于猜测式修改，会把「环境故障」变成「环境故障 + 插件回归」的双重问题。

执行过的**验证性操作**（不改动工程，仅产出中间文件）：

| 操作 | 结果 |
|---|---|
| `dotnet build -c Release` | 0 警告 / 0 错误，自动部署到 `BepInEx/plugins/ApexCheatEnder.dll` |
| `dotnet run` 测试工程 | **通过 9 / 失败 0** |
| 项目扫描脚本 | 48 文件 / 7151 行 C# / 21 提交 |
| 二进制架构实测 | 游戏纯 x64；根目录 2 个 32 位运行库为异常 |
| 双会话日志对照 | EOSSDK 失败为既有基线 |

---

## 5. 验证结果

```
Apex Cheat Ender · 核心逻辑测试
--------------------------------------------------------
  ✓ GameVec2：距离与向量运算
  ✓ PlayerTrack：快照位移/时间差计算
  ✓ PlayerTrack：合法传送豁免窗口
  ✓ PlayerTrack：回合重置清空回合级状态
  ✓ PlayerTracker：按 Id 取或建轨迹
  ✓ Violation：严重度到基础权重的映射
  ✓ PlayerVerdict：风险等级四态判定
  ✓ PlayerVerdict：证据累加与 Critical 计数
  ✓ PlayerVerdict：Reset 清空本局命中
--------------------------------------------------------
通过 9 ／ 失败 0
全部通过 ✓
```

```
ApexCheatEnder -> E:\干活\projects\among-us-ace\bin\Release\ApexCheatEnder.dll
  [AntiCheat] 已部署 -> E:\STEAM\steamapps\common\Among Us\BepInEx\plugins\ApexCheatEnder.dll
已成功生成。 0 个警告 0 个错误
```

---

## 6. 尚未确认的风险 / 缺失信息

1. **EOSSDK 加载失败的真正缺失依赖未最终确认。**
   报错码是 `WinError 0x7e (126 = MOD_NOT_FOUND)`，但 `EOSSDK-Win64-Shipping.dll`
   （18 MB，x64）**实体存在**于 `Plugins/x86_64/`，说明缺的是它的某个**间接依赖**。
   - 本机安全策略限制导致**无法在本环境实测 DLL 加载**（自建测试进程与 Python ctypes
     一律返回 `WinError 5 拒绝访问`，`reg.exe` 被策略拉黑），因此只能靠静态分析 + 日志推断。
   - **需要你做的**：安装 **Visual C++ 2015-2022 Redistributable (x64)** 后再启动一次，
     看 `Player.log:59` 是否消失。

2. **「左上角」的具体所指仍未确认。**
   你指出问题在左上角，但两次沟通中我没能拿到可辨识的截图区域。
   当前实测：屏幕上存在一个标题为 `Among Us` 的 `SDL_app` 窗口，位置 `(517,221)`、
   尺寸 `842×601`——**不在左上角**。需要你明确：是游戏窗口残留在左上角、
   还是某个弹窗、还是 WorkBuddy 侧栏误认。

3. **是否做过「禁用插件」的隔离验证未知。**
   `_diagnose/切换BepInEx.sh` 可以一键禁用。若禁用后主菜单依旧异常，则 100% 排除插件。
   这一步是本次排查中**唯一缺失的闭环证据**。

4. **Steam 验证文件完整性的影响未量化。**
   32 位运行库被恢复过两次，推测与验证有关。若确实如此，单纯改名解决不了，
   必须用 64 位版本**覆盖**（保持文件存在，避免 Steam 判定缺失）。

---

## 7. 建议（按优先级）

### 立即做
1. **点掉 `DisconnectPopup` 的确定按钮**——日志显示主菜单场景已加载
   （`MainMenuManager.RunStartUp beginning`，第 621 行），菜单**很可能就在弹窗下面**。
2. **跑一次隔离验证**：`sh "E:/STEAM/steamapps/common/Among Us/_diagnose/切换BepInEx.sh"`
   → 启动游戏 → 若仍异常，则确认与插件无关（再跑一次恢复）。
3. **安装 VC++ 2015-2022 Redist (x64)**，重启后再看 `Player.log:59`。

### 近期做
4. **把游戏根目录的 32 位 `msvcp140.dll` / `vcruntime140.dll` 替换为 64 位版本**
   （覆盖而非删除，避免 Steam 反复恢复）。
5. **补测试**：优先 `AcePresence.TryAccept`（含 callId 误判场景）与 `RpcFloodGuard` 窗口计数，
   这两个是纯逻辑、易写、且直接影响「会不会误伤正常玩家」。
6. **`MainMenuArt` 兜底策略收紧**（§3.3 中风险）：宁可放弃替换，也不要换错对象。

### 长期做
7. 给 UI 层引入可测抽象（把 `UnityEngine.UI` 依赖隔离出去），让设置窗口/监控面板可单测。
8. 版本号自动 bump + Release 产物归档，配合已有的 GitHub Actions。

---

## 附录 A · 本次执行的命令

```bash
# 项目扫描
python ~/.workbuddy-ai/skills/project-analysis-report/scripts/scan_project.py \
       "E:/干活/projects/among-us-ace" --json-out ".workbuddy-ai/tmp/ace-scan.json" --summary

# 构建（含自动部署）
cd E:/干活/projects/among-us-ace && dotnet build -c Release

# 测试
cd E:/干活/projects/among-us-ace/tests && dotnet run -c Release

# 日志取证
grep -n "EOSSDK" "%LOCALAPPDATA%Low/Innersloth/Among Us/Player.log"
grep -c "Waiting for time stamp" ".../Player.log"        # 14918
grep -n "MainMenuManager.RunStartUp" ".../Player.log"    # 621
grep -c "DisconnectPopup" ".../Player-prev.log"          # 18
```

## 附录 B · 关键文件索引

| 文件 | 行数 | 职责 |
|---|---|---|
| `src/AntiCheatPlugin.cs` | 189 | 插件入口，逐个挂载补丁（含优雅降级） |
| `src/UI/SettingsWindow.cs` | 1229 | 设置界面（最大文件） |
| `src/Core/BehaviorAnalyzer.cs` | 904 | 行为分析 |
| `src/UI/DesktopSplash.cs` | 721 | 桌面右下角启动动画（纯 Win32 P/Invoke） |
| `src/GameBridge.cs` | 570 | 游戏数据桥接 |
| `src/Patches/GameplayPatches.cs` | 552 | 玩法类补丁 |
| `src/UI/MainMenuArt.cs` | 183 | 主菜单背景替换（含一个中风险兜底策略） |
| `src/Patches/MenuPatches.cs` | 63 | 主菜单补丁挂载点 |
| `tests/Program.cs` | — | 零依赖测试 runner（9 项） |
