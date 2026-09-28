# Co-op Bot 离线多 Actor 联合搜索开发计划

> 状态：本批计划完成（P0-P12）
> 日期：2026-09-28  
> 目标：把 CombatSolver 的战斗根与搜索节点推广为 Actor 数量可变的离线模型，同时保持单人模式等价。  
> 当前边界：本计划不启用生产多人模式，不控制其他客户端，不发送网络动作，不启动可见 Steam，不提升版本、不发包或上传创意工坊。

## 当前进度

- P0：静态盘点完成，确认模拟器已按 `Player` 保存多玩家分支状态，单人假设集中于根快照、搜索和计划层。
- P1：已实现根内 `CombatActorId`、不可变 `CombatActorRoot` 列表和 `LocalActorId`；现有单人代理字段、搜索入口和部署边界未改变。显式游戏/RitsuLib 路径 Release 构建 0/0，根捕获无人检查通过。
- P2：已实现多人 `ContinuationStamp` 的 Actor 目录，live/predicted 均覆盖阶段、五个牌堆、HP/格挡/资源、奥斯提、球、药水、回合历史、状态遗物和跨回合遗物状态；单人 stamp 保持逐字不变。Release 构建、Windows 结构门禁和 `COOP-P2-SINGLE-ROOT` 根捕获无人检查通过，实例已清理。
- P3：`PlanAction` 与 `PlanCardChoice` 已带 `CombatActorId`，旧单人构造默认 `Actor0`；`JointPlan` 在联合计划边界校验 Actor 范围及选择归属。Release 构建、Windows 结构门禁和 `COOP-ACTOR-PLAN-CONTRACT` 通过，实例已清理。
- P4：新增 `JointTurnState`，支持任意 Actor 先后结束回合、死亡 Actor 通过屏障以及完整计划的跨回合屏障校验。Release 构建、Windows 结构门禁和 `COOP-JOINT-TURN-BARRIER` 通过，实例已清理。
- P5：新增确定性的 `JointActionExpander`，按 Actor、手牌顺序、目标 CombatId 和结束动作生成候选；`COOP-ACTOR-CANDIDATES` 在现有单人根上通过，`COOP-MULTI-ACTOR-ROOT` 进一步在真实 2/4 Actor 离线根上通过，覆盖 Actor-aware 归属字段、目标和重复展开稳定性。
- P6：新增 `JointCombatSnapshot`，按 Actor 顺序冻结资源/牌堆投影、联合回合状态、预测 `ContinuationStamp` 和状态指纹；`COOP-JOINT-SNAPSHOT-KEY` 与 `COOP-MULTI-ACTOR-ROOT` 通过 Fork 隔离、屏障差异和真实 2/4 Actor 状态键/回放。
- P7：新增 `JointObjectiveScore` 及确定性字典序比较器；`COOP-JOINT-OBJECTIVE` 通过胜负、总战损、逐 Actor 战损、药水、回合和动作排序。当前“总战损”只是跨角色比较的临时 workaround；角色 HP 的可比价值不同，后续需单独研究，不在本批改变。
- P8：新增 `JointPlanReplayer`，从同一根 Fork 后按 Actor 逐步定位卡牌/目标并检查选择、资源、死亡和回合屏障；`COOP-JOINT-REPLAY` 通过。当前回放覆盖同一预测战斗中的严格动作边界，尚未把完整敌方回合和网络部署纳入验收。
- P9：新增真实 2 Actor 根上的 BFS 联合搜索和不去重 DFS oracle；`COOP-MULTI-ACTOR-ROOT` 通过最优值、动作序和终局状态键一致，另有 `COOP-JOINT-ORACLE` 的有限 toy 穷举证据。两者共享单步权威模拟，不共享枚举策略。
- P10：真实 4 Actor 根在同一场景通过根目录、候选、联合快照和非本地 Actor 回放；`COOP-JOINT-FOUR-ACTOR` 继续提供有限 toy fixture 的穷举全局最优证据。正常规模多人搜索仍未宣称全局最优。
- P11：生产 `CombatBeamSolver` 保持显式 ActorCount=1 边界；`COOP-PRODUCTION-SINGLE-BOUNDARY` 通过 ActorCount=1 接受、ActorCount=2 稳定拒绝。离线联合类型不接入 Runtime、Overlay、部署或网络。
- P12：已完成架构/测试/开发记录同步、Release 构建、Windows 结构门禁、两次职责批提交和同源码本地 Mod 部署；未提升版本、打包、推送、启动可见 Steam 或接入网络。

真实多 Actor 根首个差异已定位：`MadScienceGrowth.CaptureRemainingCapacity` 原先对 `CombatState.Players` 使用 `Single()`，2 Actor 根在预测状态构造时立即失败。现改为按玩家分别计算并求和；单人结果保持等价。该项属于迁移中发现的单人假设，不改变本阶段目标函数。修复后 `COOP-MULTI-ACTOR-ROOT` 已在 2/4 Actor 通过。

[返回重构路线与核验](README.md) · [返回文档导航](../README.md)

## 1. 背景与目标

CombatSolver 当前的核心链路是：

```text
主线程捕获稳定战斗根
  -> 后台 Fork 影子状态
  -> 跨回合搜索
  -> 生成计划
  -> 通过原版入口部署本地玩家动作
```

未来 Co-op Bot 需要在一个共享战斗中联合规划多名角色的动作。第一阶段不处理真实网络部署，而是建立一个可验证的离线联合求解器：

```text
ActorCount = 1
  -> 与当前单人 CombatSolver 行为等价

ActorCount = 2..4
  -> 捕获联合战斗根
  -> 从任意仍可行动 Actor 枚举动作
  -> 模拟跨 Actor 的动作顺序
  -> 处理全员结束回合屏障
  -> 生成带 Actor 身份的联合计划
  -> 严格回放并逐步核对完整状态
```

这里的“最优”分为两种口径：

- 小型固定 fixture：通过完整穷举提供 oracle，证明得到全局最优解。
- 正常规模战斗：Beam/BFWS 只能表述为既定预算内找到的最佳路线，不能声称数学全局最优。

## 2. 当前源码事实

现有模拟底层并非完全单人化：

- `CombatPredictionState` 已经用 `Dictionary<Player, SimPlayerCombatState>` 保存多个玩家。
- 根物化和 Fork 已遍历 `CombatState.Players`。
- `SimPlayerCombatState` 已按玩家拥有独立能量、星能、牌堆和球。
- 目标枚举已经能表达 `AnyPlayer` 和其他友方目标。
- 原版 `CombatState` 本身提供 `Players`、`PlayerCreatures` 和多人缩放模型。

当前主要单人假设集中在：

- `CombatRootSnapshot` 只有一个 `PlayerIdentity` 和一组玩家标量。
- `CombatBeamSolver` 围绕 `_player` 枚举动作、投影威胁和计算评分。
- `PlanAction` 没有 Actor 身份。
- `SimulationSnapshot` 只保存一个玩家的 HP、格挡、资源和牌堆特征。
- `BuildStateKey` 只追加一个玩家的战斗状态。
- `ContinuationStamp` 从 `LocalContext.GetMe` 开始捕获单人续用状态。
- Runtime、部署、UI、录像结果和多数无人测试协议默认只有本地玩家。

因此本计划不重写整个模拟引擎，也不机械地把所有 `Player` 改名为 `Actor`。原版 `Player` 继续作为稳定模型身份；Actor 是根快照、搜索计划和联合回合协议上的纯值视图。

## 3. 范围与非目标

### 3.1 本计划包含

- 根内固定 Actor 目录。
- 多 Actor 严格状态和状态指纹。
- 带 Actor 身份的计划动作和选择。
- 多 Actor 联合回合状态与结束屏障。
- Actor-aware 候选展开、回放、快照和终局排序。
- 两 Actor 小型穷举 oracle。
- 四 Actor 离线联合搜索 fixture。
- 单人模式等价门禁。
- OfflineSearchHarness 或专用 Testing 入口。

### 3.2 本计划不包含

- Steam 联机或大厅流程。
- Host/Client 网络服务修改。
- 控制其他客户端的 Mod。
- `NetPlayCardAction`、`NetUsePotionAction` 或网络选择提交。
- 断线、重连、延迟、乱序和承诺协议。
- 生产 Overlay 的多人路线展示。
- 自动部署联合计划。
- 用更大 Beam 或预算掩盖模拟偏差。
- 在正常规模战斗中承诺数学全局最优。

生产 `SolverController` 在本阶段继续保持单人；多人战斗进入现有生产求解入口时必须明确拒绝，不能悄悄只求解本地 Actor。

## 4. 目标所有权模型

| 对象 | 所有内容 | 身份与 Fork 规则 |
|---|---|---|
| `CombatActorRoot` | Actor 编号、原版 `Player` 身份、角色、初始 HP、阶段及根元数据 | 主线程一次捕获；根内不可变 |
| `SimPlayerCombatState` | 能量、星能、五个牌堆、球、玩家阶段 | 已存在；继续按 `Player` 分支 Fork |
| `SimCreatureState` | 玩家、宠物与敌人的 HP、格挡等 | 已存在；使用同一 `PredictionForkContext` |
| `JointTurnState` | 可行动、已结束 Actor 集合，共享阶段与回合 | 新增分支状态；Fork 独立；进入状态键 |
| `PlanAction.Actor` | 每个动作的所有者 | 纯值 `CombatActorId`；worker 不查询 live 玩家 |
| `ActorSimulationSnapshot` | 每个 Actor 的生存、资源、战损和搜索特征 | 搜索快照按根 Actor 顺序冻结 |
| `TeamSimulationSnapshot` | 敌人、RNG、共享终局和团队评分 | 单个搜索节点拥有 |
| `ContinuationStamp` | 所有 Actor 与共享战斗的严格状态 | 按根 Actor 顺序确定性序列化 |

建议 Actor 身份为根内连续编号：

```csharp
internal readonly record struct CombatActorId(int Index);

internal sealed record CombatActorRoot(
    CombatActorId Id,
    Player PlayerIdentity,
    int InitialHp,
    int InitialMaxHp,
    int StartTurnNumber,
    PlayerTurnPhase Phase);
```

第一版不使用网络 ID 作为搜索主键。网络 ID 属于后续部署适配层；离线搜索只依赖根内稳定顺序和原版 `Player` 稳定身份。

## 5. 全局不变量

整个迁移必须保持以下不变量：

1. `ActorCount = 1` 时，合法动作、动作顺序、结算、终局排序和部署协议保持现有语义。
2. worker 只消费根快照，不读取会随实机推进而变化的 live 状态。
3. 一次 Fork 的所有分支结构继续共享同一个 `PredictionForkContext`。
4. Actor 顺序在一个根内固定；第一版不做 Actor 置换等价。
5. 动作、选择、药水槽和卡牌 occurrence 都必须在所属 Actor 内解释。
6. 全员结束屏障属于战斗语义，必须进入状态键和严格状态。
7. 共享 RNG 按真实联合动作顺序消费，不能把四份单人路线事后拼接。
8. 未支持的多人语义显式失败，不回退到本地玩家或默认 Actor。
9. Search 不引用 Runtime、UI、Testing 或网络服务。
10. 本批不修改生产部署入口；离线联合搜索和真实网络部署分开验证。

## 6. 分阶段实施

### P0：冻结单人等价基线

目标：建立迁移前直接证据，并盘点全部单人假设。

工作：

1. 选择固定单人 fixture：
   - 普通出牌；
   - 药水；
   - 手动选牌；
   - 结束回合；
   - 跨回合；
   - 一个包含球、宠物或角色专属资源的场景。
2. 保存每个 fixture 的：
   - 选中动作序列；
   - 每步完整 `ContinuationStamp`；
   - 状态指纹；
   - 最终排序结果；
   - expanded、transition、choice branch 等搜索指标。
3. 静态盘点：
   - `_player`；
   - `PlayerIdentity`；
   - `LocalContext.GetMe`；
   - `Players.Single()`；
   - 单个玩家 HP、能量、牌堆和药水字段；
   - 无所有者的 `EndTurn`；
   - 默认本地玩家目标；
   - 单玩家状态键和结果协议。

验收：建立一次迁移前基线，不重复运行相同证据；后续阶段只重跑受影响的最小代表。

### P1：引入根 Actor 目录，不改变搜索

目标：让根快照能够描述多个 Actor，但现有求解器仍只接受一个 Actor。

修改范围：

- `src/Runtime/CombatRootSnapshot.cs`
- 必要的根捕获 helper
- Testing 根捕获断言

工作：

1. 新增 `CombatActorId` 与 `CombatActorRoot`。
2. `CombatRootSnapshot` 新增 `Actors`。
3. Actor 顺序固定为根捕获时的 `CombatState.Players` 顺序。
4. 为每个 Actor 捕获卡牌、药水、遗物和基础标量。
5. 现有单人属性暂时代理到 `Actors[0]`，避免一次迁移所有调用方。
6. 生产 Runtime 捕获到多个 Actor 时继续明确拒绝进入单人搜索。

验收：

- 单人根捕获与迁移前等价；
- 双 Actor 测试根能列出两个稳定 Actor；
- 把双 Actor 根交给现有单人求解器时明确失败。

### P2：扩展严格状态和状态所有权

目标：让 live/predicted 严格差分覆盖所有 Actor。

修改范围：

- `src/Runtime/ContinuationStamp.cs`
- `SimulatedCombatState` 的指纹和续用辅助
- actual/simulated 差分描述

每个 Actor 至少捕获：

- Creature HP、最大 HP、格挡、生死；
- Energy、Stars、`PlayerTurnPhase`；
- 五个有序牌堆及逐卡实例状态；
- Power 和球；
- 药水槽及可变状态；
- 遗物状态和计数；
- 玩家专属历史计数；
- 宠物及附属状态。

共享部分继续捕获：

- 敌方 roster 与已知敌人状态；
- 敌人 AI；
- 九条 RNG；
- 回合号与 `CurrentSide`；
- 死亡、召唤和挂起选择；
- 共享 Hook、Modifier 和预测状态库。

验收：

- 单 Actor stamp 保持语义等价；
- 双 Actor 根的 live/predicted 严格相等；
- 修改任一 Actor 的手牌、能量、HP 或阶段都会改变严格状态；
- 两名 Actor 交换资源不能被误判为相同状态；
- 父分支和兄弟 Fork 修改互不泄漏。

### P3：计划动作显式携带 Actor

目标：所有计划动作和选择都有明确所有者。

修改范围：

- `src/Search/CombatPlan.cs`
- 动作构造、固定前缀、回放与显示投影边界

工作：

1. `PlanAction` 增加必填 `CombatActorId Actor`。
2. `PlayCard`、`UsePotion` 和 `EndTurn` 全部显式记录 Actor。
3. 联合模型中的 `EndTurn` 语义定义为 `EndActorTurn`；现有枚举名是否迁移由实现批次决定，不在纯数据迁移时同时重命名。
4. 动作内选择、嵌套选择和回合开始/结束选择记录发起 Actor。
5. 卡牌 occurrence 在 Actor 自己的牌堆内解释。
6. 药水槽属于动作 Actor。
7. 目标优先保存 `CombatId`，不依赖易漂移的列表索引。
8. 错误 Actor 必须明确拒绝，不能回退到 `_player`。

验收：

- 单人所有动作统一使用 `ActorId(0)`，显示和部署不变；
- 相同卡牌位于不同 Actor 手牌时能够精确区分和回放；
- 更换错误 Actor 后稳定失败。

### P4：新增联合回合状态

目标：准确表达多名玩家并行行动与全员结束屏障。

拟新增状态：

```csharp
internal sealed class JointTurnState
{
    public ActorMask ActiveActors { get; }
    public ActorMask EndedActors { get; }
    public CombatSide CurrentSide { get; }
    public int Round { get; }
}
```

实现前必须定向核对原版：

- `EndPlayerTurnAction`；
- `ReadyToBeginEnemyTurnAction`；
- `UndoEndPlayerTurnAction`；
- `PlayerCombatState.Phase`；
- 玩家回合结束 Hook 的顺序；
- 全员 ready 后敌方回合的准确入口。

联合状态机：

```text
玩家侧开始
  -> 所有存活且有资格的 Actor 可行动
  -> 任一未结束 Actor 可出牌、用药或结束
  -> 已结束 Actor 不再产生普通动作
  -> 全部有效 Actor 结束
  -> 执行共享玩家侧结束与敌方回合
  -> 分别准备下一回合各 Actor
```

第一版不支持网络层的撤销结束回合。

验收：

- Actor 0 结束后 Actor 1 仍能行动；
- 只有全部有效 Actor 结束才推进敌方；
- 死亡或失去行动资格的 Actor 不阻塞屏障；
- 结束掩码在 Fork 后父子和兄弟分支隔离；
- 不同结束掩码不能被状态键合并。

### P5：候选展开 Actor-aware

目标：搜索可从任意仍可行动 Actor 产生合法候选。

联合展开顺序固定为：

```text
ActorIndex
  -> 动作类型
  -> 原牌堆/药水槽顺序
  -> 目标 CombatId
  -> 选择原序
```

工作：

1. 遍历未结束 Actor。
2. 为当前 Actor 枚举卡牌、药水和结束动作。
3. 串行与固定 lane 并行路径共用相同 Actor-aware `ExpansionPlan`。
4. 选择续执行、药水续执行、固定前缀和回放全部按动作 Actor 定位状态。
5. opening、cycle、cross-turn、transposition 和工作预算中的动作身份加入 Actor。
6. 第一版不尝试证明不同 Actor 的动作可交换；所有顺序都按真实语义保留。

验收顺序：

1. 两 Actor，每人一张牌；
2. 两 Actor，多目标；
3. 两 Actor，独立药水；
4. 两 Actor，选牌；
5. 两 Actor，提前结束和跨回合。

### P6：多 Actor 搜索快照和状态键

目标：让转置、剪枝、威胁投影和评分看见完整联合状态。

拟拆分：

```csharp
internal sealed record ActorSimulationSnapshot(...);
internal sealed record TeamSimulationSnapshot(...);
```

每个 Actor 快照至少包括：

- 当前与预计 HP；
- 最大 HP、格挡、生死；
- 能量、Stars；
- 累计战损和恢复；
- 手牌数、可出牌数和牌堆价值；
- 药水使用和战略成本；
- 玩家专属长期资源；
- 玩家阶段和结束状态。

状态键按根 Actor 顺序追加：

```text
ActorId
  -> Creature state
  -> Player combat state
  -> ordered piles
  -> orbs
  -> potions
  -> relic state
  -> actor-specific history
  -> turn readiness
```

之后再追加共享敌人、RNG、死亡状态与 `SimulatedCombatState` 指纹。

约束：

- 第一版不做 Actor 置换等价；
- 两个相同角色也不能互换身份；
- Actor 身份进入动作键、状态键和回放协议；
- 纯显示名称不进入状态键。

验收：

- 相同卡牌属于不同 Actor 时不发生跨 Actor 转置合并；
- 相同共享状态但结束掩码不同，不合并；
- 单 Actor 状态键保持语义等价；
- Fork、转置与增量回放结果一致。

### P7：定义团队目标和“最优”

目标：给穷举 oracle 和生产搜索一个单一、明确的终局比较器。

第一版建议使用字典序：

1. 胜利优于失败；
2. 全队存活优于有人死亡；
3. 总永久生命损失更少；
4. 最低剩余生命比例更高；
5. 永久资源和药水消耗更少；
6. 回合数更少；
7. 动作数更少；
8. 最后用确定性联合动作序决胜。

不能只把所有 Actor 的 HP 相加，否则可能用一名角色死亡换取其他角色过量剩余 HP。Beam 中间评分可以聚合团队特征，但终局字典序必须由单一 `FinalPlanOrdering` 权威实现。

验收：

- 总损失相同但分布不同；
- 一人死亡但总 HP 更高；
- 药水换回合；
- 多条同值路线的确定性决胜；
- 穷举 oracle 与生产最终比较器一致。

### P8：建立联合计划严格回放器

目标：把“搜索找到路线”和“路线能够按相同语义执行”分开验证。

回放流程：

```text
从相同根 Fork
  -> 读取 PlanAction.Actor
  -> 在该 Actor 的状态中定位卡牌或药水
  -> 执行动作和选择
  -> 每步捕获完整严格状态
  -> 与搜索节点状态比较
```

每一步至少检查：

- 动作 Actor；
- 卡牌逐实例状态；
- 目标；
- 选择上下文；
- RNG 状态与计数；
- 所有 Actor 的牌堆、资源和阶段；
- 敌方完整状态；
- 结束掩码；
- 终局。

该回放器不接网络，也不控制客户端。

### P9：两 Actor 穷举 oracle

目标：在可穷举状态空间内直接证明联合搜索的最优性。

fixture 约束：

- 两个 Actor；
- 每人一至三张固定牌；
- 单个敌人；
- 无随机或冻结 RNG；
- 单回合；
- 无无限循环；
- 最多一个简单目标或选择。

oracle 使用朴素 DFS：

- 不使用 Beam；
- 不使用生产剪枝；
- 枚举所有合法联合动作序列；
- 战斗结算仍调用同一个权威模拟器；
- 使用同一个团队终局比较器选优。

生产联合搜索与 oracle 比较：

- 最优值；
- 最终结果；
- 联合动作序列；
- 完整终局状态。

oracle 只独立实现枚举策略，不能复制第二套卡牌或战斗结算语义。

### P10：扩展至四 Actor

目标：证明同一 Actor 模型能从两名玩家自然扩到四名玩家。

fixture 顺序：

1. 四 Actor，每人一张牌，单回合；
2. 四 Actor，多目标；
3. 队友目标卡；
4. 独立药水；
5. 有 Actor 提前结束；
6. 跨回合；
7. 一个 Actor 死亡；
8. 多人倍率；
9. 玩家间 Hook、Power、遗物影响；
10. 角色专属资源和球。

小 fixture 继续穷举；组合爆炸后的场景只证明：

- 严格回放一致；
- 单线程/多线程确定性；
- 不可退化哨兵；
- 在相同预算下的相对路线质量。

### P11：保持生产 Runtime 单人边界

目标：离线联合搜索落地时，不提前引入不完整的多人部署。

- `SolverController` 继续只接受一个本地 Actor。
- Overlay 继续渲染单人计划。
- 自动执行继续拒绝多人战斗。
- 联合搜索仅从 Testing 或 OfflineSearchHarness 入口启动。
- 捕获到多名玩家时生产入口输出稳定“不支持”结果。
- 不发送任何 `Net*Action`。
- 不控制其他客户端。

### P12：文档、门禁与滚动提交

建议按以下提交边界推进：

1. Actor 根目录与单人兼容层；
2. 多 Actor 严格状态；
3. Actor-aware `PlanAction`；
4. 联合回合状态；
5. Actor-aware 候选展开；
6. 多 Actor 快照、状态键和团队排序；
7. 联合严格回放；
8. 两 Actor oracle；
9. 四 Actor fixture 和离线入口。

每个批次：

1. 记录迁移前后所有权和不变量；
2. 只迁移一个可解释职责块；
3. 删除旧所有权或限制兼容代理的生命周期；
4. 同步 Windows/Linux 结构门禁；
5. 运行 Release 构建和受影响的最小代表 fixture；
6. 更新 `docs/ARCHITECTURE.md`、`docs/DEVELOPMENT_NOTES.md` 和 `docs/TEST_MATRIX.md`；
7. 直接提交当前批次；
8. 不提升版本、不打包、不上传、不启动可见 Steam。

## 7. 单人假设审计清单

实现期间逐项搜索并沿调用链处理：

- `LocalContext.GetMe(...)`
- `state.Players.Single()`
- `_player`
- `PlayerIdentity`
- `InitialPlayerHp` / `InitialPlayerMaxHp`
- `PlayerHp` / `PlayerBlock` / `Energy` / `Stars`
- `playerState.Hand` 及其他单一牌堆引用
- `player.PotionSlots`
- 无所有者的 `EndTurn`
- 无 Actor 的 `PlanCardChoice`
- 默认本地玩家的卡牌、Power、遗物和药水所有权
- 按全局 occurrence 找牌，而非 Actor 内 occurrence
- 状态键只追加一名玩家牌堆
- 终局只判断本地玩家死亡
- 敌方威胁只投影到一名玩家
- 回合推进只运行一次玩家准备或结束
- 历史计数只传 `_player`
- UI、日志、缓存和路线协议默认只属于本地玩家

这些项目不能一次性批量替换。每一类都要确认当前所有者、目标所有者、状态生命周期、Fork 方式和对应最小验证。

## 8. 验证矩阵

| 阶段 | 最低验证 | 不足以证明 |
|---|---|---|
| P0 | 固定单人动作、严格状态和搜索指标基线 | 多人正确性 |
| P1 | 单人根等价；双 Actor 根目录；生产入口拒绝 | 联合搜索 |
| P2 | 双 Actor live/predicted 严格差分；Fork 隔离 | 动作可执行性 |
| P3 | 带 Actor 动作精确定位和错误 Actor 拒绝 | 回合屏障 |
| P4 | 两 Actor 结束屏障和跨回合状态 | 最优性 |
| P5 | Actor-aware 候选与串并行确定性 | 状态键完整性 |
| P6 | 多 Actor 指纹、转置和增量等价 | 终局政策正确 |
| P7 | 构造反例和比较器合同 | 搜索没有丢解 |
| P8 | 联合计划逐步严格回放 | 全局最优 |
| P9 | 小型 DFS oracle 与生产搜索一致 | 正常规模全局最优 |
| P10 | 四 Actor 严格回放和确定性 | 网络部署 |
| P11 | 生产 Runtime 稳定拒绝多人 | 联机 Bot 可用 |

无人测试遵循仓库现有层级：

- 纯结构迁移：Release 构建、结构门禁、一个穿过边界的代表场景。
- 新增 Actor 分支状态：最小 actual/simulated 严格差分、Fork、指纹和两回合生命周期。
- 搜索动作/排序：目标短搜、穷举 oracle、不可退化哨兵，最终候选才做必要完整场景。
- 不启动可见 Steam；headless 测试必须使用 `-CleanupInstanceOnExit`。

## 9. 关键风险与停损条件

### 9.1 组合爆炸

四名 Actor 的动作交错会显著扩大排列空间。第一阶段先保证正确，不用 Beam 扩容掩盖问题。只有 P9 oracle 证明语义正确后，才单独进入搜索性能与保路优化。

### 9.2 动作顺序误判为可交换

Hook、历史、目标和共享 RNG 都可能让跨 Actor 动作顺序有语义。第一版全部保留顺序，不做交换归一化。

### 9.3 Actor 身份从状态键遗漏

两个相同角色或相同卡牌仍属于不同所有者。发现跨 Actor 转置、卡牌定位或药水槽混用时立即停止扩大场景，先修复身份和状态键。

### 9.4 单人回归

任何单人合法动作、状态、路线政策或协议变化都视为回归，除非用户另行授权改变单人行为。不能以“为多人准备”为由接受无关单人变化。

### 9.5 过早接入网络

离线联合计划尚不能严格回放、或小型 oracle 尚未通过时，不开始客户端控制、承诺性或网络同步工作。

## 10. 实时进度汇报约定

实施本计划时持续向用户汇报：

- 阶段开始：目标、准备修改的文件、保持不变的合同。
- 发现关键原版语义：立即报告事实和对计划的影响。
- 每个职责块完成：已修改文件、编译状态、仍存在的单人假设。
- 每次验证完成：实际命令、fixture、结果和证明范围。
- 验证失败：首个差异状态和当前定位，不扩大预算或超时掩盖。
- 每次提交完成：提交内容、下一阶段和剩余风险。
- 持续工作超过约一分钟没有阶段结果时：发送简短进度更新。

用户在实施期间可以随时暂停、缩小或调整阶段；已通过且输入未变化的证据不重复执行。

## 11. 开工条件与完成定义

### 11.1 开工条件

当前仓库规则仍写明“只支持单人战斗；不要增加多人兼容分支”。正式实施前，需要把本批授权限定并写入活动批次：

> 允许新增离线、多 Actor 的联合搜索模型和测试入口；生产 Runtime、UI、自动部署及网络入口仍保持单人并显式拒绝多人。

这不表示现有 CombatSolver 已支持多人，也不授权 Steam 联机、客户端控制、版本提升或发布。

### 11.2 第一里程碑

- `ActorCount = 1` 的固定基线保持等价。
- 两 Actor、单回合、固定小牌组 fixture 可完整穷举。
- 生产联合搜索和 DFS oracle 得到相同最优值与严格终局状态。
- 联合计划可以逐动作严格回放。

### 11.3 离线四 Actor 里程碑

- 同一模型支持四 Actor，无专门四角色分支。
- 四 Actor 动作全部带稳定 Actor 身份。
- 全员结束屏障和跨回合状态正确。
- 小型四 Actor fixture 可穷举或由可证明分解的 oracle 验证。
- 较大 fixture 的联合计划可严格回放且串并行确定。
- 生产 Runtime 仍显式保持单人边界。

完成这些条件后，下一份独立计划才讨论“承诺性修正”和“一个真实客户端只控制一个玩家槽位”。
