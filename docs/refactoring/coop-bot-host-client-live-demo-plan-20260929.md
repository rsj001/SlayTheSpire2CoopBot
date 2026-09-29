# Co-op Bot Host/Client 四人实机演示开发计划

> 日期：2026-09-29  
> 状态：实施中（C0–C9 代码与无人门禁已通过；C3/C4/C6–C9 的真实联机门禁留待四客户端预检；C10 待实施）
> 前置成果：离线多人语义 M0–M11 已完成  
> 核心假设：**Host 可以读取四名玩家完成联合搜索所需的全部真实战斗状态**  
> 项目边界：本计划实现于独立 `CoopBot` Mod；CombatSolver 的生产 Runtime 继续只支持单人，`src/Search/Coop` 不读取网络、live UI 或客户端状态。

## 1. 目标与完成定义

目标是在四客户端真实联机战斗中完成以下闭环：

1. 四名玩家安装同一个 `CoopBot` Mod；Host 自动成为唯一规划协调者，所有实例都运行本地 Actor 执行代理。
2. Host 在稳定边界捕获完整四人战斗根，转换为已有 `CombatRootSnapshot`，运行 `JointOfflineSearch`。
3. Host 从同一冻结根严格回放 best-found 路线，再向四端广播只读完整计划供 UI 展示。
4. Host 每次只授权当前一个动作；动作 owner 所在客户端校验后通过原版公开入口执行并返回 ACK。
5. Host 自己的动作也走相同命令、校验和 ACK 状态机，仅把网络传输替换为进程内传输。
6. Host 等待原版同步重新稳定，逐字段比较 actual 与该动作的 predicted checkpoint；一致才提交下一动作。
7. 手动插入动作、拒绝、超时、掉线、目标或选择变化、actual/simulated 差异都会取消旧计划并从新根重规划。
8. 最终完成一场四人代表战斗，至少覆盖全队牌、队友目标、异 Actor 选择、药水、球或宠物、敌方 AOE、单员死亡或复活边界中的适用代表。

本阶段只宣称“真实四人状态上的固定预算 best-found 联合规划与协调执行通过”，不宣称任意战斗全局最优。

## 2. 非目标与硬约束

- 不把联机控制加入 CombatSolver 的 `SolverController`、单人部署器或生产 Beam。
- 不在 Runtime 重新实现卡牌、Power、遗物、药水、怪物或回合结算；这些仍由原版入口和离线权威语义分别负责。
- 不由 Host 直接写远端 HP、能量、牌堆、Power、readiness 或网络 owner。
- 不让 Client 运行搜索、修改搜索政策或自行选择替代动作。
- 不把广播的完整计划视为执行授权；Client 只执行当前 `ActionCommit`。
- 第一版不做公网服务、无人值守匹配、跨主机迁移、恶意客户端对抗或断线后自动接管。
- 总战损暂时仍为各 Actor HP 损失和，并保留逐 Actor HP 向量；角色 HP 权重另立研究。

## 3. 目标架构与所有权

所有玩家安装同一个 Mod，由会话身份启用不同组件：

```text
CoopBot
├─ Protocol                 版本化 DTO、序号、幂等和拒绝码
├─ NativeAdapter            live 观察与原版动作入口
├─ Capture                  稳定根、Actor 映射和状态指纹
├─ Host
│  ├─ SearchCoordinator     捕获、转换、搜索、严格回放
│  └─ DeploymentCoordinator 计划、单动作授权、ACK、重规划
├─ Client
│  └─ LocalActorAgent       本地校验、执行和回执
├─ UI                       只读 snapshot renderer
└─ Diagnostics              事件日志、actual/sim 首差异与证据导出

CombatSolver
└─ src/Search/Coop          冻结根上的纯离线联合模型与搜索
```

### 3.1 Host 独占

- `CombatSessionId`、`RootRevision`、当前稳定根和完整状态指纹；
- 搜索请求、取消令牌、当前 `PlanId` 和 predicted checkpoints；
- 动作调度、Prepare/Commit、ACK、超时和重规划；
- 完整路线及全队 UI snapshot 的权威版本。

### 3.2 每端 LocalActorAgent 独占

- 本地网络玩家到 `CombatActorId` 的绑定；
- 当前命令、已处理 `ActionId` 幂等表和本地执行状态机；
- 对本地卡牌/药水实例、目标、费用、选择页面和行动权的最终校验；
- 通过原版公开入口执行属于本地 Actor 的动作；
- ACK、拒绝原因和本地动作后摘要。

### 3.3 Search 边界

- Search 只接收冻结的 `CombatRootSnapshot`、政策、预算、诊断 sink 和取消令牌。
- Search 不引用 Host、Client、网络、UI、live `CombatState` 或全局设置。
- Host 将 live 根显式转换为离线根；转换失败是稳定失败边界，不能用默认值补齐。
- 联合动作仍由 `JointActionTransition` 权威模拟，严格回放仍由 `JointStrictReplayVerifier` 完成。

## 4. 会话、身份和稳定根

### 4.1 稳定身份

每场战斗建立并广播不可变映射：

```text
CombatActorId ↔ NetworkPlayerId ↔ CharacterId ↔ IsLocal ↔ IsHost
```

Actor 顺序来自原版稳定 roster，而不是消息到达顺序。映射在战斗内变化时立即停止自动模式并重建会话，不静默重排。

### 4.2 稳定根条件

Host 只有在以下条件全部满足时才增加 `RootRevision`：

- 原版动作和 Command 队列为空；
- 卡牌、伤害、抽牌、Power、死亡、召唤和选择事务均已结束；
- 没有未确认的本 Mod 动作；
- 玩家侧/敌方侧、Round、TurnNumber 和 readiness 已稳定；
- roster 与 CombatId 集合稳定；
- 连续观察得到相同的完整状态指纹。

超时仍不稳定时暂停自动模式并报告具体活动事务，不捕获半结算根。

### 4.3 情况 A 假设门禁

在开始控制前必须证明 Host 捕获内容覆盖：

- 四名玩家的 HP、格挡、能量、星能、金币和最大生命；
- 五牌堆有序实例、费用、升级、附魔和临时标志；
- Power 的数量、内部计数、Applier/Target；
- 药水槽、遗物状态、球、宠物和角色资源；
- 怪物 roster、行动、AI 私有状态和死亡/复活/逃跑；
- 九条战斗 RNG；
- Round、每 Actor 阶段、选择上下文和终局。

任一搜索必需字段只有 owner Client 可见，则情况 A 被证伪：停止本计划的控制阶段，转入“Client 私有片段合成根”扩展，不允许 Host 猜测。

## 5. 版本化协议

所有消息至少携带：

```text
ProtocolVersion
CombatSessionId
MessageSequence
SenderNetworkPlayerId
RootRevision
PlanId（适用时）
ActionId（适用时）
```

最小消息集合：

| 消息 | 方向 | 用途 |
|---|---|---|
| `Hello` / `SessionAccepted` | 双向 | 版本、Host 身份和能力协商 |
| `ActorAssignment` | Host→All | 冻结 Actor 映射 |
| `RootPublished` | Host→All | 发布根版本和指纹，不传 live 对象 |
| `PlanPublished` | Host→All | 广播完整只读路线和预测摘要 |
| `ActionPrepare` | Host→Owner | 请求 owner 校验当前动作 |
| `ActionPrepared` / `ActionRejected` | Owner→Host | 返回可执行或稳定拒绝码 |
| `ActionCommit` | Host→Owner | 当前唯一执行授权 |
| `ActionAck` / `ActionFailed` | Owner→Host | 原版接受、完成或失败结果 |
| `PlanCancelled` | Host→All | 使全部未执行授权失效 |
| `AutomationModeChanged` | Host→All | Observe/Suggest/ConfirmEach/Auto |
| `Heartbeat` | 双向 | 会话活性与最后序号 |

### 5.1 ActionCommand

动作必须包含：

- `ActorId`、动作种类和预期根指纹；
- 卡牌实例身份或药水槽/ID；
- 目标 CombatId；
- 主选择、嵌套选择和选择 Actor；
- 预期费用、回合和行动阶段；
- 不可逆等级与超时；
- 唯一幂等 `ActionId`。

拒绝码至少区分：根过期、非本地 Actor、实例缺失、费用变化、目标失效、选择变化、非行动阶段、已有在途动作、重复命令和协议不兼容。

## 6. 计划发布与单动作承诺

### 6.1 完整计划只用于展示

`PlanPublished` 可以包含完整联合路线、逐动作 predicted checkpoint、逐 Actor 战损、总战损、药水成本和停止原因。各端 UI 据此显示未来路线，但不能自行连续执行。

### 6.2 执行只按当前动作授权

固定流程：

```text
Host ActionPrepare
→ Owner 本地校验
→ ActionPrepared
→ Host ActionCommit
→ Owner 原生执行
→ ActionAck
→ Host 等待 live 稳定
→ actual/predicted 严格差分
→ 下一动作或重规划
```

Host 自己的 Actor 使用同一个流程，只通过进程内 transport 调用本地 Agent。

### 6.3 不可逆边界

- 建议和未 Commit 的动作不构成承诺。
- 未过全员屏障的 readiness/EndTurn 可按已验证规则撤销。
- 药水、消耗牌、失血、随机结算和强制 EndTurn 属于不可逆动作，第一版必须 Prepare/Commit。
- 第一版禁止整条计划或多 Actor 前缀自动预提交；后续短前缀优化须另立证据和撤销协议。

## 7. 本地原生执行状态机

```text
Idle
→ Validating
→ Prepared
→ Executing
→ WaitingNativeCompletion
→ Reporting
→ Idle
```

任何非 `Idle` 状态拒绝第二条命令。完成条件不是“入口方法返回”，而是原版动作、选择、伤害、死亡、召唤、牌堆和网络同步全部重新稳定。

第一版动作支持顺序：

1. Host 本地普通出牌、目标和 EndTurn；
2. 远端 Client 普通出牌、目标和 EndTurn；
3. 无选择药水；
4. 跨玩家目标；
5. 主选择、跳过和取消；
6. 异 Actor 选择（以 `Tutor`／“指导”为门禁）；
7. 嵌套选择和自动/重复子出牌；
8. 跨回合动作及死亡后的 Actor 子集。

未知动作或选择显式拒绝，不通过 UI 模拟点击或直接字段写入兜底。

## 8. UI 合同

UI 继续使用“结果 → 主线程只读 snapshot → renderer”边界，renderer 不读取搜索结果、网络 DTO、`PlanAction`、`ModelDb` 或 live 战斗对象。

### 8.1 四端共有

- 当前模式、Host、ActorId、连接和协议状态；
- 搜索中/已完成/失效/重规划状态；
- 完整联合路线，每步显示 Actor、动作、目标和选择；
- 当前授权动作与 owner；
- 逐 Actor HP 预测、总战损 workaround、药水和停止原因；
- ACK、拒绝、超时和 actual/sim 差异摘要。

### 8.2 Host 额外控件

- 搜索、取消、重规划；
- 执行下一步、暂停、全自动；
- Observe/Suggest/ConfirmEach/Auto 模式；
- Actor 映射、客户端状态和详细差异诊断。

### 8.3 Client 额外控件

- 本地待执行动作；
- 允许本次、拒绝并转人工、暂停自动执行；
- 最近命令和结果。

玩家可见新增文案必须同时维护中文和英文。第一场演示使用 `ConfirmEach`，通过后才允许 `Auto`。

## 9. actual/simulated 验证与重规划

每个搜索结果必须保存逐动作和屏障 predicted checkpoint。收到 ACK 后，Host 从重新稳定的 live 状态生成 actual checkpoint，并比较：

- 动作/屏障索引、Actor、Turn、Phase；
- 每名 Actor 全状态；
- 敌人、Power、牌堆、药水、球、宠物和角色资源；
- RNG、回合历史、状态键和 continuation；
- 终局。

第一处差异必须包含 `PlanId`、`ActionId`、Actor、字段、actual 和 predicted；不得只比较聚合 HP。

以下任一事件立即广播 `PlanCancelled` 并重规划：

- 玩家手动插入动作；
- Client 拒绝、失败或超时；
- 卡牌、药水、目标或选择变化；
- actual/simulated 不一致；
- roster、Actor 映射、Host 或协议版本变化；
- 掉线/重连；
- 战斗提前终止。

同一根连续出现相同失败、无法建立稳定根或未知原版入口时停止自动模式，不能重试到偶然成功。

## 10. 通信与安全

- 优先调查原版/RitsuLib 的版本化自定义联机消息入口；没有稳定入口才使用独立 LAN transport。
- 独立 transport 默认不监听公网，只接受当前会话显式加入的节点。
- Client 只接受已协商 Host 的命令，校验会话随机令牌、序号和协议版本。
- 重发可以恢复丢包，但相同 `ActionId` 永不重复执行。
- 战斗结束、Host 退出或版本不一致时销毁会话和未完成命令。
- 完整日志不得记录账号令牌或无关个人信息。

## 11. 分阶段实施与门禁

当前进度与直接证据：

| 阶段 | 状态 | 证据 |
|---|---|---|
| C0 原版接口调查 | 已通过（静态） | [0.111.0 原版/RitsuLib 接口审计](coop-bot-c0-native-interface-audit-20260929.md) |
| C1 协议与会话 | 已通过（纯合同） | 四协议实例映射、版本/Host/sender 冲突、序号、幂等和清理共 32 项；独立 Mod 编译通过 |
| C2 只读 Recorder | 已通过（无人合成四 Actor） | `6ef8cb07a6574394b19e9fc95c111efb`：4 Actor、9 RNG、110 指纹字段；真实平台 Host 可见性保留为实机前置门禁 |
| C3 Host 搜索桥 | 已通过（无人合成四 Actor） | `4bdf976afd0c43c597fb9eb980b3b3e0`：DOP1/DOP4 动作、终态、展开数与脱离 DTO 一致，两次 strict replay；真实 Host 根对照保留为实机预检 |
| C4 四端 UI | 已通过（无人投影） | `badd049e2a914f16b7224d3c8d0044c1`：四端同 PlanId/路线、Host/Client 控件分工与 zhs/eng；真实同步显示和布局保留为可见验收 |
| C5 Host 本地执行 | 已通过（无人原生动作） | `737579cf05734538a36d2536ba09f27c`：普通出牌/目标与 EndTurn 均经原版队列完成，分别对齐动作与跨回合 barrier 全 continuation |
| C6 远端执行 | 代码/无人合同通过，实机待验 | 56 项纯合同覆盖三个远端 owner、拒绝/超时/重复；`398291209a19425e95694de4e79678f3` 验证 owner 指纹和原版 handler 生命周期；三个真实 Client 各出牌一步仍是实机门禁 |
| C7 选择与药水 | 代码/无人动态通过，实机待验 | `342e3340a01d459b81a81819db815bfa`：Tutor Actor3 选择、错误 owner 拒绝、Actor0→Actor3 Block Potion 与 LocalActorAgent 原版药水 ACK 均通过严格 continuation；62 项纯合同含全 observer-ready 屏障；真实四端广播与远端执行仍是实机门禁 |
| C8 完整玩家侧 | 代码/无人动态通过，实机待验 | `c1bb062ff57c4ed2b2d9402a7979081e`：四 Actor 2→0→3→1 到达完整 readiness 屏障；人工插入取消/重规划与授权根严格验证分流；66 项纯合同通过 |
| C9 跨敌方侧 | 代码/无人动态通过，实机待验 | `07868b5599614431b5f1877af27d01b7`：四 Actor 完整敌方侧/下一轮与单员死亡子集严格一致；逐 Actor readiness 入根，最后 EndTurn 对齐下一轮 barrier；单人边界 `2bbeaa025a7c4669a9fd7022dc91a5d4` 通过 |
| C10 完整演示 | 待实施 | 完成无人证据导出与故障注入后，生成四客户端实机预检/执行清单；真实整场由用户安排可见环境 |

| 阶段 | 产物 | 必须通过的门禁 |
|---|---|---|
| C0 原版接口调查 | Host/owner 判断、消息、动作和完成观察点清单 | 每个入口绑定游戏/RitsuLib 版本；未知项不得进入实现 |
| C1 协议与会话 | DTO、序号、ActorAssignment、心跳、幂等 | 4 实例 Actor 映射一致；版本/Host 冲突稳定拒绝 |
| C2 只读 Recorder | Host 稳定根、四端摘要、事件日志 | 情况 A 字段清单逐项成立；不成立即停止控制阶段 |
| C3 Host 搜索桥 | live→root、取消、严格回放、PlanPublished | 同一真实根 DOP1/DOP4 路线/终态确定；无 live worker 读取 |
| C4 四端 UI | Host/Client 只读 snapshot 与模式控件 | 四端同 PlanId/路线；renderer 无 mutable/search/network 依赖 |
| C5 Host 本地执行 | 进程内 Agent、Prepare/Commit/ACK | 出牌、目标、EndTurn 各一次 actual/sim 严格一致 |
| C6 远端执行 | Client Agent、transport、拒绝/超时/幂等 | 三个远端 Actor 各执行一步；重复命令不重复出牌 |
| C7 选择与药水 | 药水、跨玩家目标、选择链 | `Tutor` 异 Actor 选择及跨玩家药水严格一致 |
| C8 完整玩家侧 | 多 Actor 调度、readiness、手动插入 | 四 Actor 任意原序完成一侧；插入动作取消并重规划 |
| C9 跨敌方侧 | 敌方阶段、死亡、下一轮根 | 至少跨一轮，逐 checkpoint 与离线预测一致 |
| C10 完整演示 | 四人代表整场、证据包 | 胜负终局、0 重复执行、所有实例退出后无残留会话 |

每阶段只在前一阶段门禁通过后开始。协议、动作入口、UI 和自动化不得在一个巨大提交中同时落地。

## 12. 验证矩阵

### 12.1 纯合同

- DTO 往返、未知字段和版本拒绝；
- MessageSequence 去重、乱序和重发；
- ActionId 幂等；
- Actor 映射稳定性；
- 状态机非法跃迁；
- 超时、取消和战斗结束清理；
- UI snapshot 不含 live/search mutable 对象。

### 12.2 两客户端最小原生闭环

1. Host 控制自己打普通攻击；
2. Host 命令 Client 打普通攻击；
3. 双方动作次序互换；
4. Client 拒绝过期根；
5. 重发 Commit 不重复执行；
6. 手动动作使旧计划失效；
7. `Tutor` 由远端 DecisionActor 选择；
8. 双方 EndTurn 并跨敌方侧。

### 12.3 四客户端演示矩阵

- 四种不同角色和稳定 Actor 顺序；
- Host 与三个 Client 分别至少执行一步；
- 全队效果、队友目标、卡牌转移或异 Actor 选择；
- 药水；
- 球或宠物；
- 敌方 AOE；
- 一名 Actor 死亡后剩余 Actor 继续，或同级死亡边界代表；
- 一次人工插入与成功重规划；
- 至少一个敌方侧和下一玩家侧；
- 完整胜利或明确失败终局。

### 12.4 故障注入

- Client 掉线/重连；
- ACK 丢失、重复和乱序；
- Prepare 后根变化；
- Commit 后原版拒绝；
- 目标死亡、牌被转移、选择候选变化；
- Host 取消搜索或退出；
- 同一失败重复三次后自动模式停止。

## 13. 证据格式

每次实机验证保存：

- 游戏、CombatSolver、CoopBot、RitsuLib 版本及程序集 SHA-256；
- 四端会话身份、Actor 映射和协议能力；
- 根摘要、政策和固定预算；
- `PlanPublished`、Prepare/Commit/ACK 序列；
- predicted 与 actual checkpoints；
- 首差异或通过结论；
- 重规划原因；
- 实例清理结果。

日志成功不等于语义通过；只有原版执行后的完整 actual/simulated checkpoint 对账才是动作证据。

## 14. 提交与文档策略

建议按以下批次提交：

1. `docs: define coop host client runtime boundary`
2. `feat: add coop protocol and actor sessions`
3. `feat: capture stable multiplayer host roots`
4. `feat: connect host roots to joint search`
5. `feat: render synchronized coop plans`
6. `feat: execute host actor commands`
7. `feat: dispatch remote actor commands`
8. `feat: support remote choices and potions`
9. `feat: coordinate complete multiplayer rounds`
10. `test: verify four-player live joint combat`

职责落地时同步维护架构地图、Windows/Linux 结构门禁、测试矩阵和机器证据。普通开发不自动提升版本、发包、上传创意工坊或启动可见 Steam；真实联机阶段由用户明确安排可见四客户端环境。

## 15. 完成判定与停止条件

### 完成

- C0–C10 全部通过；
- 四端使用同一 Mod 和协议版本；
- 完整计划只读广播、动作逐条授权；
- Host 和远端动作共用同一 Agent 合同；
- 每步 actual/simulated 严格一致或明确重规划；
- 四人代表整场结束且无重复执行、悬挂命令或残留会话；
- CombatSolver 生产单人边界与既有离线 M0–M11 门禁保持通过。

### 必须暂停并改计划

- 情况 A 被证伪；
- 原版没有可维护的 owner 本地动作入口；
- 自定义消息无法可靠绑定 Host/Actor；
- 选择或动作完成边界无法稳定观察；
- 为通过实机测试必须改写真实战斗字段或复制第二套战斗语义。

出现这些条件时先记录首个证据，再选择“Client 私有状态片段”“不同 transport”或“收窄自动动作集合”，不得用更宽异常捕获、延迟或重复重试掩盖。
