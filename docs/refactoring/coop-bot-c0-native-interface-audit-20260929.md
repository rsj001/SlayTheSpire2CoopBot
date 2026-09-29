# Co-op Bot C0 原版联机接口审计

> 日期：2026-09-29  
> 结论：C0 静态接口调查通过；允许进入 C1 协议与会话实现  
> 游戏：Slay the Spire 2 `0.111.0`  
> RitsuLib：`0.6.2`，使用 `compat/0.111.0`

本文固定 [Host/Client 四人实机演示计划](coop-bot-host-client-live-demo-plan-20260929.md) C0 的原版接口证据。它只证明当前程序集提供了可实现的身份、消息、动作和完成观察边界；“Host 能看见联合搜索所需全部四人状态”的情况 A 仍必须在 C2 用真实多人状态逐字段证明。

## 1. 审计输入

| 输入 | SHA-256 |
|---|---|
| `data_sts2_windows_x86_64/sts2.dll` | `0861BFA1DF347538D932F22D580E75420F08082792EB914E53B4882764ACDBE9` |
| `RitsuLib/compat/0.111.0/STS2-RitsuLib.dll` | `7427DA5493306C8C6A859ED3690257914528A4D2BF29821320B31391BBB36D5D` |
| `RitsuLib/compat/0.111.0/STS2-RitsuLib.Runtime.dll` | `F321AA102F18E939AC48DD2DC60610284FA5B4697B84385775494593D9ED4A7C` |
| `RitsuLib/STS2-RitsuLib.dll` 启动选择器 | `E3959F1746FCB7AA404CB9CD861443DC540E8488B50F7D156EACBE79925156B6` |

原版反编译引用根为 `C:/Users/LEGION/Desktop/Slay the Spire 2/src/Core`。下文路径均相对此根。

## 2. 身份、角色表与生命周期

| 需要 | 原版入口 | 采用方式与约束 |
|---|---|---|
| Host/Client 判断 | `Runs/RunManager.cs` 的 `RunManager.Instance.NetService.Type` | 仅 `NetGameType.Host` 创建规划协调器；`Client` 只创建本地代理；`Singleplayer` 不启用 Co-op 会话。 |
| 本地网络身份 | `Context/LocalContext.cs` 的 `NetId`、`GetMe(...)`、`IsMe(...)` | 本地代理只能接受 `ActorAssignment` 中绑定到此 `NetId` 的 Actor。 |
| 稳定玩家 roster | `Combat/CombatState.cs` 的 `Players`；玩家含 `NetId` | Actor 顺序冻结为根 `Players` 顺序，不按消息到达顺序生成。战斗内顺序或身份变化使会话失败，不静默重排。 |
| 战斗开始/结束 | RitsuLib `CombatStartingEvent`、`CombatEndedEvent`；原版 `CombatManager.CombatBegan/CombatEnded` | Mod 用生命周期事件保存/释放当前 `CombatState`，不在后台临时查询 live 单例。 |
| 回合/阵容变化 | `CombatManager.TurnStarted`、`TurnEnded`、`CreaturesChanged`、`AboutToSwitchToEnemyTurn` | 这些事件只触发重新观察；能否捕获根仍由稳定性门禁决定。 |

`CombatManager.DebugOnlyGetState()` 虽为 public，但原版文档明确标注仅供测试。CoopBot 生产路径不得以它作为状态所有权入口；当前战斗引用必须从生命周期事件参数取得并在结束时清除。

## 3. 自定义消息 transport

原版 transport 可直接承载 Mod 消息，不需要第一版另开 LAN 端口：

- `Multiplayer/Serialization/MessageTypes.cs` 在初始化时合并 `INetMessageSubtypes.All` 与 `ReflectionHelper.GetSubtypesInMods<INetMessage>()`。
- `Multiplayer/Serialization/NetTypeCache.cs` 以类型名确定性排序并生成整数 ID。因此四端必须加载相同 CoopBot 消息类型集合；仅靠协议字段不能修复程序集类型集合不一致。
- `Runs/RunManager.cs` 暴露当前 `INetGameService NetService`。
- `Multiplayer/Game/INetGameService.cs` 提供 Host 定向发送、广播/Client→Host 发送、注册/注销处理器、连接状态与断开事件。
- `Multiplayer/NetMessageBus.cs` 在包头携带消息类型 ID 与真实 sender ID，处理器收到 transport 提供的 `senderId`。

协议实现必须遵守：

1. 消息使用可靠传输并允许原版加载期缓冲；执行授权不得使用广播回显替代 Host 定向发送。
2. 处理器以 transport 的 `senderId` 为权威，不信任 DTO 自报的 sender 字段；两者不一致时稳定拒绝。
3. `NetMessageBus` 会捕获处理器异常、记录后继续。因此 CoopBot 处理器必须把验证失败显式转换为拒绝或会话 `Failed`，不能依赖异常自动终止会话。
4. `MessageTypes` 的类型 ID 是 `byte` 写入。C1 必须保持消息类型集合小而固定，并用单一 envelope 承载版本化 payload，避免每次扩协议都改变大量原版消息类型 ID。
5. 战斗结束、原版 `Disconnected`、Host 变化或版本不兼容时注销所有处理器并清空未提交命令。

## 4. 原生动作入口与 owner 边界

权威入口是 `RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(GameAction)`，而不是直接改 live Model：

| Bot 动作 | 原版 `GameAction` | 构造身份 |
|---|---|---|
| 出牌 | `GameActions/PlayCardAction.cs` | 本地 hand 中的具体 `CardModel` 加目标 `Creature?`；序列化保留 `NetCombatCard` 实例身份、模型 ID 与目标 CombatId。 |
| 用药 | `GameActions/UsePotionAction.cs` | 本地槽中的具体 `PotionModel`、目标和 `isCombatInProgress: true`。 |
| 结束回合 | `GameActions/EndPlayerTurnAction.cs` | 本地 `Player` 与当前 `TurnNumber`。 |
| 撤销 readiness | `GameActions/UndoEndPlayerTurnAction.cs` | 本地 `Player` 与当前 `TurnNumber`；只允许尚未越过原版不可撤销屏障时使用。 |

`ActionQueueSynchronizer` 已实现原版 Host 排序与同步：Client 请求 Host 入队，Host 广播确认后各端按同一 action ID 执行。

关键 owner 结论：Host **不能**直接为远端玩家调用 `RequestEnqueue`。Host 分支的 `EnqueueAction(action, _netService.NetId)` 会把 Host `NetId` 写入 `ActionEnqueuedMessage.playerId`；远端按该 ID 重建 owner。CoopBot 因此必须把 `ActionCommit` 发给 owner Client，由该 Client 的 LocalActorAgent 以本地实例构造动作并调用 `RequestEnqueue`。Host 自身动作也走同一代理合同，只把 transport 换成进程内投递。

## 5. 完成、稳定与手动插入观察点

原版提供的必要观察点：

- `ActionQueueSet.IsEmpty` 与 `ActionQueueChanged`：所有玩家队列是否还有项目；
- `ActionExecutor.CurrentlyRunningAction`、`IsRunning`、`FinishedExecutingActions()`；
- `ActionExecutor.BeforeActionExecuted`、`AfterActionExecuted`：绑定当前 Bot 命令与真实 action，发现非 Bot 的玩家动作；
- `ActionQueueSet.ActionEnqueued`：在执行前发现手动插入；
- `ActionQueueSynchronizer.CombatState`：区分 PlayPhase、回合结束和非玩家阶段；
- `CombatManager` 的 ending/side/turn/roster 事件。

单个 `AfterActionExecuted` 只代表该 `GameAction` 结束，不代表伤害、死亡、召唤、选牌与网络传播均已稳定。NativeAdapter 的统一稳定门禁必须同时满足：队列空、executor 不运行、没有选择/命令事务、无本 Mod 在途授权、战斗身份未变，并在连续主线程观察中得到相同完整指纹。C2 才实现和验证该门禁。

## 6. 选择链

`GameActions/Multiplayer/PlayerChoiceSynchronizer.cs` 证明选择 owner 由原版同步：所有端确定性 `ReserveChoiceId(player)`，owner 端 `SyncLocalChoice(...)`，其他端 `WaitForRemoteChoice(...)`。这支持“异 Actor 选择必须在 DecisionActor 所在客户端执行”的设计。

当前 CombatSolver 已有 `NativeChoiceRuntime` 对原生选择候选、实例身份和完成页面的观测/部署经验，但它是单人 Runtime 内部实现，而且部分路径通过 UI 控件完成选择。CoopBot 不直接复用该内部类，也不把 UI 点击作为兜底。C7 开始前必须先建立独立 NativeAdapter：在 owner Client 捕获原生 choice context，以原版选择结果/同步入口提交，并为 `Tutor`（指导）的异 Actor 选择做专项差分。C1–C6 不声明或提交带选择动作。

## 7. C0 判定与后续限制

C0 静态门禁通过，因为：

- Host/Client、本地 owner、稳定 roster 和生命周期均有明确版本绑定入口；
- 原版 transport 会发现 Mod 的 `INetMessage`，支持 Host 定向消息和真实 sender 身份；
- 普通出牌、用药和 EndTurn 有公开同步入口，且远端 owner 约束已明确；
- 动作入队、执行结束和队列清空有可组合观察点；
- 选择的 owner 同步语义明确，但控制入口严格延后到 C7。

仍未证明的事项不是 C0 接口未知，而是后续动态门禁：

- C2：Host 是否实际拥有情况 A 列出的全部四人 live 字段；
- C2：完整状态指纹连续相同能否准确覆盖所有异步事务；
- C5/C6：Bot 命令与原版 action/ACK 的真实一一对应；
- C7：无 UI 模拟的 owner 选择提交入口；
- C9/C10：跨敌方侧、死亡/复活和整场清理。

任一动态门禁失败时，按总计划停止控制阶段并记录首个差异，不扩大超时或直接写 live 字段。
