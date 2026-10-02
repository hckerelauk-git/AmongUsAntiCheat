AmongUsAntiCheat（ACE · Apex Cheat Ender）
Among Us 客户端反作弊插件，基于 BepInEx 6（IL2CPP）+ Harmony。
以「规则命中」模型对房间内的 RPC 与玩家行为做语义级校验，在房主侧拦截作弊，并给出可解释的判定依据。

插件 GUID：apex.cheat.ender

目标环境：Among Us（IL2CPP）+ BepInEx 6.0.0-be.735+ / Unity 2022.3.44f1 / .NET 6.0

程序集架构：AnyCPU（同一份 dll 可被 32 位与 64 位游戏进程加载）

⚠️ 能力边界（务必先读）
本插件的实际实现范围小于常见宣传口径。为避免误解，这里如实列出。

已实现并接入检测循环
层	能力	代码位置
静态	扫描已加载的 BepInEx 插件，比对作弊特征库（黑 / 灰 / 白名单）	Core/ModScanner.cs、Core/CheatSignatureDb.cs
行为	瞬移、持续超速、穿墙（默认关闭）	Core/BehaviorAnalyzer.AnalyzeMovement
行为	会议期间位移	Core/BehaviorAnalyzer.AnalyzeMeetingMovement
事件	非内鬼击杀、超距击杀、击杀冷却绕过	Core/BehaviorAnalyzer.AnalyzeKill
事件	远程提交任务、任务速度异常	Core/BehaviorAnalyzer.AnalyzeTask
事件	无通风能力的角色使用通风管、远程使用通风管	Core/BehaviorAnalyzer.AnalyzeVentUse
事件	破坏系统：非内鬼破坏 / 会议中破坏 / 越界目标	Patches/GameplayPatches.UpdateSystemPatch
事件	开局保护期内的会议 / 报告尸体	Patches/GameplayPatches.ReportDeadBodyPatch
事件	非变形者变形 / 非守护天使保护	Patches/GameplayPatches.ShapeshiftPatch / ProtectPatch
事件	伪造通风管编号 / 会议中使用通风管	Patches/GameplayPatches.VentOpPatch
事件	非房主强制把他人踢出通风管	Patches/GameplayPatches.BootFromVentPatch
事件	会议期间使用滑索	Patches/GameplayPatches.ZiplinePatch
事件	幽灵动作（已死亡玩家执行活人动作）	Patches/PlayerActionPatches.MurderPlayerPatch
通讯	聊天刷屏、空消息、超长消息、控制字符	Patches/GameplayPatches.SendChatPatch
通讯	昵称非法（空 / 超长 / 控制字符）	Patches/GameplayPatches.SetNamePatch
网络	RPC 洪水限速（Prefix 拦截，仅房主丢弃）	Core/Rpc/RpcFloodGuard.cs
网络	位置强制同步（RpcSnapTo）高频检测	Patches/PlayerActionPatches.SnapToPatch
网络	超大数据包（房主丢弃）	Patches/GameplayPatches.OversizedPacketPatch
记录	对局玩家名单 / 命中记录落盘	Core/HistoryLog.cs
辅助	大模型二次研判（可选，仅 DeepSeek）	Core/Rpc/RpcAiAnalyzer.cs
未实现
以下能力在本代码库中完全没有代码：

已知作弊客户端 RPC ID 识别（SickoMenu / AUM 等硬编码 RPC 号）

大厅非法 RPC 检测、VotingComplete 过载防护、整数反序列化加固

服务端权威校验（客户端反作弊的固有天花板，需另行设计）

当前版本中所有配置项均已接入实际检测逻辑，不再存在「拨了没反应」的空壳开关。

处置模型
采用「规则命中」模型，没有分数累积、没有时间衰减：

每条命中记一笔（PlayerVerdict.EvidenceCount）；

命中 Critical 严重度的规则 → 直接判定为 HighRisk；

AI 二次研判确认 → 升级为 Confirmed；

其余命中 → Suspicious（只记录，等房主处置）。

自动踢人默认关闭（自动踢人（不用手动点）= false）。开启后，命中 HighRisk 及以上会触发踢出；
踢出动作只在自己是房主时才会真正执行。

快速开始
安装 BepInEx 6（IL2CPP 版）到 Among Us 游戏目录；

将 AmongUsAntiCheat.dll 放入 BepInEx/plugins/；

启动游戏，插件自动生成配置：BepInEx/config/apex.cheat.ender.cfg；

游戏中按 F8 显示 / 隐藏右上角监控面板，按 Insert 打开设置界面。

文档索引
文档	内容
01 检测目标	插件要防什么、判定到什么程度、明确不防什么
02 核心原理	规则命中模型、三层检测架构、降误报设计
03 关键模块与数据流	模块职责、依赖关系、一帧内的完整数据流
04 配置项与参数	全部配置项逐条说明、取值范围、生效情况
05 对外接口与返回值	桥接层 API、Harmony 补丁契约、核心类型
06 常见误判场景与处理	已知误报来源、成因、处置办法
07 部署与调试	构建、部署、日志、故障排查
源码结构
text
src/
├── AntiCheatPlugin.cs          插件入口：读配置 → 建模块 → 挂补丁
├── AntiCheatRuntime.cs         纯托管运行时：持有全部模块，驱动帧循环
├── GameBridge.cs               游戏 API 桥接层（所有 il2cpp 访问集中于此）
├── Config/
│   └── AntiCheatConfig.cs      全部配置项定义
├── Core/
│   ├── BehaviorAnalyzer.cs     行为 / 事件检测算法
│   ├── VerdictEngine.cs        判定层：证据 → 结论 → 处置
│   ├── PlayerTrack.cs          单玩家轨迹与观测快照
│   ├── ModScanner.cs           静态扫描：已加载插件 vs 特征库
│   ├── CheatSignatureDb.cs     作弊特征库（黑名单 / 灰名单 / 默认信任）
│   ├── Violation.cs            证据、严重度、风险等级定义
│   ├── GameVec2.cs             纯逻辑二维向量
│   ├── ConfigHotReloader.cs    配置文件热重载（0.5s 轮询 + Diff）
│   ├── Ai/AiProviders.cs       AI 供应商目录
│   └── Rpc/
│       ├── RpcFloodGuard.cs    RPC 速率保护
│       ├── RpcEvent.cs         单条 RPC 事件
│       ├── RpcEventRecorder.cs 每玩家 RPC 环形缓冲
│       ├── RpcAiAnalyzer.cs    AI 分析编排
│       └── RpcAiClient.cs      AI HTTP 客户端
├── Patches/
│   ├── FrameDriverPatch.cs     帧驱动（三个冗余入口）
│   └── PlayerActionPatches.cs  击杀 / 任务 / 通风管 / RPC 洪水 / 位置同步
└── UI/
    ├── AceUiRoot.cs            画布与帧调度
    ├── MonitorPanel.cs         右上角监控面板（F8）
    ├── NotificationPanel.cs    右下角通知卡片
    ├── SettingsWindow.cs       游戏内设置界面（Insert）
    ├── DesktopSplash.cs        桌面右下角启动动画（Win32）
优化说明
本次整理做了以下改动，保持内容不变、可读性与一致性提升：

标题层级与列表统一：能力表、处置模型、源码结构均使用一致的中英文分隔符与空行节奏。

术语统一：击杀冷却绕过、通风管、滑索、幽灵动作 等术语全篇统一写法。

表头与分隔符规范：Markdown 表格全部对齐，| 前后保留一个空格。

段落合并：处置模型与快速开始的条目合并到单行，避免无意义换行。

源码树注释分隔符统一：全部改为 ： 与 / 交替使用，视觉一致。

能力边界分节：已实现 与 未实现 两块用明确的二级 / 三级标题区分，便于快速跳读。

中文标点：全文统一使用中文顿号、引号与破折号，避免中英混排时的视觉跳跃。

代码路径：所有文件路径统一使用反引号包裹，表格内保持同列对齐。
                                                                                                                                                              ###豆包AI生成
