# Apex Cheat Ender (ACE)

Among Us 客户端反作弊插件（BepInEx 6 / IL2CPP）。

**编译状态：`dotnet build -c Release` 通过，0 错误 0 警告。**

---

## 界面

三块界面，分别在不同位置：

| 位置 | 内容 | 快捷键 |
|---|---|---|
| **Windows 桌面右下角** | 启动动画（ACE 预启动风格），游戏启动时弹出，约 4.4 秒后自动消失 | — |
| **游戏内 · 居中** | 设置界面：980×620 大窗口，左侧 7 个标签页 + 右侧配置区 | `Insert` |
| **游戏内 · 右上角** | 监控面板：防护状态 + 可疑度排行 | `F8` |

### 桌面右下角启动动画

这是**操作系统桌面**上的独立窗口，不是游戏内 UI。

实现方式：**纯 P/Invoke 调 Win32 API 自建窗口**，不依赖 WinForms / WPF。
原因是游戏进程跑在 CoreCLR 上，机器上通常没有安装 .NET Desktop Runtime，
一旦引用 WinForms 就会在运行时抛 `FileNotFoundException`。

窗口特性：
- `WS_POPUP` 无边框 · `WS_EX_TOPMOST` 置顶 · `WS_EX_TOOLWINDOW` 不进任务栏
- `WS_EX_LAYERED` 做整体透明度淡入淡出
- 位置由 `SystemParametersInfo(SPI_GETWORKAREA)` 计算，**自动避开任务栏**
- 独立 STA 线程跑消息循环，不阻塞游戏主线程
- 绘制全部走 GDI：`FillRect` 画底板与进度条，`DrawText` 画文字，盾牌图标用三块矩形拼出

### 游戏内设置界面

左侧 7 个标签页，分组参考 Amethyst 反作弊的菜单组织方式：

```
反作弊检测  |  通风 / 滑索保护  |  玩家管理  |  网络保护
界面与通知  |  检测记录  |  关于
```

交互实现上刻意**不用 uGUI 的 Button / Toggle 组件**——
它们的 `onClick` / `onValueChanged` 需要往 il2cpp 事件上挂托管委托，
在动态注册的类型上容易出问题。改为每帧读鼠标位置，
用 `RectTransformUtility.RectangleContainsScreenPoint` 做命中测试后自己分发点击。
数值行按点击落在行的左半区 / 右半区决定减或加。

### 启动动画时间轴

```
0.00 ~ 0.30s  淡入
0.30 ~ 2.90s  进度条推进，状态文字分阶段切换：
                正在初始化安全组件 → 正在加载作弊特征库
                → 正在校验游戏完整性 → 正在扫描已加载模块
2.90 ~ 3.70s  定格在「防护已启用」，盾牌转绿
3.70 ~ 4.40s  淡出并销毁窗口
```

---

## 1. 它做什么

四层检测，逐层降低置信度：

| 层 | 检测内容 | 误报率 | 开关 |
|---|---|---|---|
| **静态层** | 扫描 BepInEx 已加载插件，比对作弊软件特征库（GUID / 插件名 / 类型名 / 内嵌字符串） | 极低 | `启用静态模组扫描` |
| **事件层** | Harmony 拦截击杀、任务完成、通风管、位置同步，校验参数合法性 | 低 | `启用事件检测` |
| **行为层** | 按固定间隔采样位置，做运动学分析（瞬移 / 持续超速 / 穿墙） | 中 | `启用行为检测` |
| **判定层** | 汇总证据 → 加权计分 → 时间衰减 → 分级响应 | — | — |

### 具体检测项

- **瞬移** — 单次采样位移超阈值，且不在合法传送豁免窗口内
- **持续超速** — 连续 N 次采样速度超上限才升级为证据（压制网络抖动）
- **穿墙** — 位置落在墙体碰撞体内（`Physics2D.OverlapPoint`，默认关闭）
- **超距击杀** — 击杀瞬间双方距离超过「设置距离 + 容差」
- **击杀冷却绕过** — 两次击杀间隔短于角色冷却
- **非内鬼击杀** — 非内鬼阵营执行击杀
- **远程做任务** — 提交任务时人不在任务点附近
- **任务速度异常** — 两次任务点之间的移动速度超过物理上限
- **非法通风管** — 非内鬼使用，或距离通风管过远
- **会议期间移动** — 会议中位置发生位移
- **高频位置同步** — 10 秒内触发 6 次以上 `RpcSnapTo`（作弊者瞬移的典型特征）

### 降误报的三道闸门

1. **合法传送豁免窗口** — 回合开始、会议开始/结束、进出通风管，都会开启 0.35 秒豁免
2. **抖动容差** — 小于阈值的位移直接忽略
3. **连续命中计数** — 单次异常不算，连续多次才升级为证据

另外，计分带**时间衰减**：最后一次证据产生 20 秒后开始每秒衰减，让偶发误报自然消退。

---

## 2. 编译

### 环境要求

- .NET SDK 6.0 或更高（本项目用 8.0.413 验证通过）
- Among Us（IL2CPP 版）+ BepInEx 6.x

### 命令

```bash
cd AmongUsAntiCheat
dotnet build -c Release
```

游戏不在默认路径时覆盖 `GameDir`：

```bash
dotnet build -c Release -p:GameDir="你的Among Us目录"
```

编译成功后会自动把 DLL 拷进 `BepInEx/plugins/`。不想自动部署加 `-p:AutoDeploy=false`。

### 为什么必须引用游戏目录的 DLL

BepInEx 插件要在游戏进程内运行，编译时必须链接游戏程序集才能拿到正确的类型定义。
本项目**只读取**游戏目录下的 DLL，不修改任何游戏文件。

`csproj` 里引用的是：
- `BepInEx/core/` — BepInEx 运行时、Harmony、Il2CppInterop
- `BepInEx/interop/` — 游戏程序集与 UnityEngine 程序集

> ⚠️ **注意**：UnityEngine 必须引用 `interop/` 里的那一套，**不要用 `unity-libs/`**。
> `interop/` 里的是 Il2CppInterop 生成的，其 `MonoBehaviour` 继承链顶端是 `Il2CppObjectBase`；
> `unity-libs/` 是另一套参考程序集，类型不兼容，会导致 `AddComponent<T>` 等泛型约束编译失败。

---

## 3. 安装

1. 编译产物 `AmongUsAntiCheat.dll` 放进 `BepInEx/plugins/`
2. 启动游戏，配置文件生成在 `BepInEx/config/bluewhale.amongus.anticheat.cfg`
3. 游戏内按 **F8** 切换监控面板

---

## 4. 配置

配置文件分六组，全部可调。关键项：

```ini
[1-总开关]
启用静态模组扫描 = true
启用行为检测 = true
启用穿墙检测 = false      # 有额外性能开销且延迟时可能误报，默认关
启用事件检测 = true

[2-采样参数]
采样间隔秒 = 0.10         # 越小越灵敏，开销越大
回合开始宽限秒 = 4.0      # 避免出生传送误报

[3-运动学阈值]
最大速度容差倍率 = 1.6
瞬移判定最小位移 = 4.5
超速累计次数 = 3

[4-事件阈值]
击杀距离容差 = 0.75
击杀冷却容差秒 = 0.35
任务速度容差倍率 = 1.5
远程任务容差 = 2.0

[5-判定与响应]
警告阈值 = 1.0
踢出阈值 = 3.0
可疑度衰减速率 = 0.05
允许自动踢人 = false      # 默认关！建议先只记录，观察无误报后再开
显示游戏内面板 = true

[6-白名单]
受信任插件GUID =
受信任插件名 =
```

### 调参建议

**先跑一周只记录模式**（`允许自动踢人 = false`），看日志里有没有误报。
如果某项检测频繁误报，按下面调：

| 现象 | 调整 |
|---|---|
| 网络差的玩家被判瞬移 | 提高 `瞬移判定最小位移` 到 6~8 |
| 正常玩家被判超速 | 提高 `最大速度容差倍率` 到 2.0 |
| 超距击杀误报 | 提高 `击杀距离容差` 到 1.2~1.5 |
| 想做客制化玩法（自定义角色加速） | 把模组加进白名单，或提高速度容差 |

---

## 5. 自动踢人

只有**自己是房主**时才生效。达到 `踢出阈值` 会调用 `InnerNetClient.KickPlayer(clientId, ban)`。

默认**关闭**。原因是反作弊的误判代价很高——冤枉一个正常玩家比放过一个作弊者更伤。
建议流程：

1. 先只记录，跑一段时间
2. 翻 `BepInEx/LogOutput.log`，搜 `[证据]` 和 `[警告]`
3. 确认没有误报后，再开 `允许自动踢人`

---

## 6. 项目结构

```
AmongUsAntiCheat/
├── AmongUsAntiCheat.csproj
├── src/
│   ├── AntiCheatPlugin.cs          插件入口，装配各模块、挂载补丁
│   ├── AntiCheatRuntime.cs         纯托管运行时：全部检测逻辑 + 帧状态机
│   ├── GameBridge.cs               游戏 API 桥接，所有访问都带降级保护
│   ├── Config/
│   │   └── AntiCheatConfig.cs      全部可调参数
│   ├── Core/
│   │   ├── GameVec2.cs             纯逻辑向量（不依赖 Unity）
│   │   ├── Violation.cs            违规类型 / 严重度 / 证据模型
│   │   ├── CheatSignatureDb.cs     作弊软件特征库 + 默认信任名单
│   │   ├── ModScanner.cs           静态扫描
│   │   ├── PlayerTrack.cs          玩家轨迹与状态
│   │   ├── BehaviorAnalyzer.cs     运动学与动作合法性算法
│   │   └── VerdictEngine.cs        加权计分、时间衰减、分级响应
│   ├── Patches/
│   │   ├── FrameDriverPatch.cs     帧驱动（三个入口互为冗余）
│   │   └── PlayerActionPatches.cs  击杀 / 任务 / 通风管 / 位置同步
│   └── UI/
│       ├── AceTheme.cs             配色 + 程序生成纹理与音效
│       ├── UiBuilder.cs            uGUI 元素构建工具
│       ├── AceUiRoot.cs            Canvas 根节点 + 帧调度
│       ├── SplashAnimation.cs      右下角启动动画
│       └── MonitorPanel.cs         右上角监控面板
└── README.md
```

### 设计取舍

**为什么帧驱动要 patch 三个地方？**
动态注册进 il2cpp 的托管 MonoBehaviour **收不到 Unity 每帧分发的消息**——
`Awake` 会在 `AddComponent` 时同步触发（所以日志看起来一切正常），
但 `Update` / `OnGUI` 不会。这是本项目踩过最大的坑，表现为插件加载成功、零报错、界面永不出现。

现在改为 patch 原生方法，并挂三个入口互为冗余：

| 入口 | 覆盖阶段 |
|---|---|
| `Canvas.SendWillRenderCanvases` | 游戏启动到退出（主入口，每帧静态方法） |
| `HudManager.Update` | 大厅 / 对局 |
| `SplashManager.Update` | 启动阶段 |

三者都调 `AceUiRoot.DriveFrame()`，内部用 `Time.frameCount` 做帧去重，同帧重复触发不会重复执行。

**为什么界面用 uGUI 而不是 IMGUI？**
IMGUI 依赖 `OnGUI` 消息，而它正是上面那个不可靠的通道。uGUI 全部由 il2cpp 原生类型
（Canvas / Image / Text）构成，创建后主动刷新文本，不依赖任何托管组件的消息回调。

**为什么判定是确定性规则而不是机器学习？**
反作弊判定必须可解释、可复现。被误判的玩家需要能看到具体是哪条规则、哪个数值触发的。
每条证据都带结构化 `Metrics`（距离、速度、阈值），直接写进日志。

**为什么补丁用「按方法名查找 + `__args` 通用取参」？**
Among Us 每个大版本都会给核心方法加参数（`MurderPlayer` 后来就加了 `MurderResultFlags`）。
硬编码签名会在更新后直接失效；按名字找 + 通用取参能让补丁在签名变化后依然工作。
单个补丁挂载失败只影响那一项检测，不会让整套瘫痪。

**为什么 `GameBridge` 里到处是 try-catch？**
IL2CPP 下访问不存在的成员会抛异常甚至崩进程。
把游戏 API 访问全部收敛到一处并做多路径降级，能让插件在游戏更新后最多失去某项检测能力，
而不是整个挂掉。

---

## 7. 已知限制

- **客户端反作弊有天花板**。作弊者完全可以在自己机器上禁用本插件。
  要真正防住，需要**服务端权威校验**（自建服务器，在服务端验证移动与动作）。
- **静态扫描只能识别已知作弊软件**。改过 GUID、混淆过字符串的新作弊器需要更新特征库。
- **击杀距离表是固化的**（`{1.0, 1.8, 2.5}`）。当前游戏版本没有把该表暴露成可读的静态字段。
  如果实际判定距离不同，调 `击杀距离容差` 补偿。
- **穿墙检测默认关闭**。有额外开销，且在玩家高延迟时可能误报。
- **任务点查询走 `PlayerControl.myTasks`**。拿不到任务列表时该项检测自动跳过。

---

## 8. 配套工具

`../tools/TypeProbe/` 是一个 API 探针：从游戏程序集里读出真实的类型名、字段名、方法签名。

```bash
cd ../tools/TypeProbe
dotnet run -c Release                                  # 查默认清单
dotnet run -c Release -- PlayerControl ShipStatus      # 查指定类型
dotnet run -c Release -- -asm BepInEx.Unity.IL2CPP.dll IL2CPPChainloader
```

写插件时如果拿不准某个 API，先问它，别靠记忆猜。
