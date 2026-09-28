# 离线四 Actor 单人功能迁移库存

> 状态：F0 基线  
> 冻结提交：`00a4193f`  
> 维护规则：后续 F 阶段只能把本表中的 `待迁移` 关闭为 `已迁移`，或用实际原版证据改为 `明确不支持`；不得只凭编译或静态阅读改变状态。

本文是[完整迁移计划](single-to-offline-four-actor-complete-migration-plan-20260929.md)的可检查库存。它记录单人 CombatSolver 已有的功能面、联合模型当前状态、权威源码入口和最低验收证据。生产 Runtime、UI、联网与部署保持单人，不属于本库存的迁移对象。

状态定义：

- `已迁移`：联合模型已有权威实现，并取得对应动态证据。
- `待迁移`：单人已有实现，但联合模型尚未覆盖完整语义。
- `明确不支持`：原版或第三方语义经实际证据证明无法离线模拟，联合入口必须稳定拒绝。
- `需要真实原版证据`：仅靠反编译或静态阅读不能确定 Actor 所有权、时序或网络语义。

## 根、身份和状态所有权

| 功能面 | 单人权威入口 | 联合入口 | F0 状态 | 最低关闭证据 |
|---|---|---|---|---|
| 稳定根与 Actor 目录 | `Runtime/CombatRootSnapshot.cs` | `CombatActorRoot` | 已迁移 | 1/2/4 Actor 根捕获及固定顺序 |
| Actor 身份进入动作与选择 | `Search/CombatPlan.cs` | `JointPlan.cs` | 已迁移 | 越界与跨 Actor owner 稳定拒绝 |
| 每 Actor HP、格挡、能量、Stars、金币 | `Search/SimulatedCombatState.cs` | `JointCombatSnapshot.cs`、Actor continuation | 已迁移 | 2/4 Actor 远端格挡/能量/Stars/金币扰动与 Fork 隔离 |
| 每 Actor 五个牌堆与卡牌实例状态 | `Search/SimulatedCombatState*.cs` | `ContinuationStamp`、`JointCombatSnapshot` | 已迁移 | 五牌堆进入 Actor continuation；远端升级实例扰动通过 |
| 每 Actor Power、遗物、药水、球、宠物、角色资源 | `Search/SimulatedCombatState*.cs`、`Prediction/*` | 尚无完整联合投影 | 待迁移 | 各 Actor 独立变更、Fork 隔离、状态键与续用戳 |
| 敌人 roster、AI、行动和隐藏状态 | `Prediction/Monster*`、`SimulatedCombatState*.cs` | 复用模拟器，联合快照未完整投影 | 待迁移 | 敌方跨回合 strict diff 与 RNG 计数 |
| 九条战斗 RNG、Hook 历史和战斗历史 | `Engine/InCombat/Simulation/*`、`PredictionStateStore` | 复用模拟器，联合审计未完成 | 待迁移 | 兄弟 Fork 隔离与跨回合 strict diff |
| 续用戳 | `Runtime/ContinuationStamp.cs` | Actor 分段字段 | 已迁移 | 单人文本兼容；2/4 Actor 代表字段扰动通过，机制字段由 F4/F5继续验证 |
| 完整状态键 | `CombatBeamSolver.StateEvaluation.cs` | 联合屏障 + 完整 continuation 文本 | 已迁移 | Actor 代表字段及屏障扰动改变键；机制专项由 F4-F7补证据 |

## 候选、动作和选择

| 功能面 | 单人权威入口 | 联合入口 | F0 状态 | 最低关闭证据 |
|---|---|---|---|---|
| 普通出牌与 EndTurn | `CombatBeamSolver.Expansion*.cs` | `JointActionExpander`、`JointActionTransition` | 已迁移 | 搜索与回放同键、2/4 Actor 非本地动作 |
| AnyEnemy、AnyAlly 基础目标 | `CombatBeamSolver.Expansion.cs` | `JointActionExpander.ResolveTargets` | 已迁移 | 目标顺序、队友目标与 Actor owner |
| AnyPlayer、Self、全体、无目标及动态目标 | `CombatBeamSolver.Expansion*.cs`、card mirrors | F3a 已覆盖全部当前 TargetType；AnyPlayer/AnyAlly 显式枚举 | 已迁移 | 2/4 Actor 手动目标集合；非手动目标由 mirror 解析 |
| 自动牌、重复牌、复制牌、生成牌、回手与临时牌 | `CombatBeamSolver.CardChoiceContinuation.cs`、`Prediction/*` | F3b 可探测并补全动作内动态/嵌套选择；完整跨回合 continuation 待 F6 | 待迁移 | Havoc→Second Wind 动态嵌套通过；其余代表集与跨回合仍待验证 |
| 卡牌实例 identity、升级、附魔和状态 occurrence | `PreparedCardAction`、`CombatPlan.cs` | F3a 使用同一 `ChoiceCardKey`/state occurrence 回放 | 已迁移 | Armaments/Strike 候选回放与搜索单步同键 |
| 药水使用、目标、槽位、生成、复制和替换 | `CombatBeamSolver.*Potion*.cs`、`Prediction/Potion*` | F4a 已按 Actor 枚举槽位/目标并区分主选择与 nested 选择；生成/复制/替换代表集待 F4b | 待迁移 | 2/4 Actor 独立 Block Potion/Gambler's Brew 候选已通过；完整代表集及搜索/回放同键待补 |
| 主动遗物动作 | 原版无战斗内独立提交入口；遗物由 Hook 触发 | 不新增伪造动作；`PlanRelicEffect` 仍是路线注释 | 明确不支持 | F1 反编译检索无 `UseRelic`/`ActivateRelic` 战斗动作；遗物触发归 F5 |
| 主选择、嵌套选择、回合开始/结束选择 | `PrimaryChoiceReplay`、`CardChoiceContinuation`、`PotionChoiceContinuation` | F3b 已完成动作内探测/补全；回合边界与独立 continuation 待 F6 | 待迁移 | owner/source/实例/原序及兄弟 Fork 隔离 |
| opening、fixed-prefix、cycle、cross-turn、plan continuation | `CombatSearchCoordinator.*`、`FrontierContinuationScheduler` | F3c 已接入同回合 fixed-prefix 请求并由 BFS/DFS 对照；opening/cycle/cross-turn 尚未接入 | 待迁移 | 单人候选序哨兵与 2/4 Actor 联合成员；跨回合待 F7 |

## 结算机制

| 功能面 | 单人权威入口 | 联合入口 | F0 状态 | 最低关闭证据 |
|---|---|---|---|---|
| 卡牌和伤害/格挡基础结算 | `Engine/InCombat/Simulation/*`、`Mirrors/*` | 复用同一 simulator/mirror | 已迁移 | Actor0-3 同机制 strict diff |
| Power 创建、叠加、减少、移除和生命周期 | `Mirrors/Power*`、`Prediction/*Power*` | 复用模拟器，Actor 审计未完成 | 待迁移 | applier/target/时序逐字段差分 |
| 被动遗物、计数、消耗和共享遗物 | `Mirrors/Relic*`、`Prediction/*Relic*` | 复用模拟器，Actor 审计未完成 | 待迁移 | 每 Actor 与共享边界对照 |
| 球槽、球序、触发和角色资源 | `SimulatedCombatState*Orb*`、角色 mirrors | 复用模拟器，联合投影未完成 | 待迁移 | Defect 与各角色资源代表 fixture |
| 宠物和召唤物所有权 | `Prediction/*`、相关 card/power mirrors | 未建立统一 Actor owner 合同 | 需要真实原版证据 | 原版多玩家所有权/死亡时序证据 |
| 第三方 subscriber 与登记表 | `THIRD_PARTY_ADAPTERS.md` 所列 registry | 未知 gameplay subscriber 仍拒绝 | 待迁移 | 已登记来源 Actor 对照；未知来源稳定拒绝 |

## 回合、终局和搜索政策

| 功能面 | 单人权威入口 | 联合入口 | F0 状态 | 最低关闭证据 |
|---|---|---|---|---|
| Actor 独立结束与全员屏障 | 单人 `EndTurn` 尾部 | `JointTurnState` | 已迁移 | 任意顺序、死亡 Actor、提前跨回合拒绝 |
| 玩家回合尾 Hook、敌方回合、下一回合开始/抽牌 | `CombatBeamSolver.Terminal.cs`、simulation commands | BFS/DFS 在屏障停止 | 待迁移 | 2/4 Actor 两回合 strict diff |
| 额外回合、死亡、复活、逃跑和失去资格 | `Prediction/*`、`SimulatedCombatState*.cs` | 仅基础死亡标记 | 待迁移 | roster/屏障/续用戳与实际状态一致 |
| 敌方死亡、召唤、行动尾部和终局 | `Prediction/Monster*`、`CombatBeamSolver.Terminal.cs` | 基础 win check | 待迁移 | 首个差异定位到动作/Hook/字段 |
| 单人终局政策 | `FinalPlanOrdering`、`RouteQualityPolicy` | 未复用 | 待迁移 | ActorCount=1 结果逐位等价 |
| 团队目标 | 无单人对应 | `JointObjective` 临时总战损 + 向量 | 待迁移 | 存活、分布、资源、成长和稳定决胜 |
| 药水政策、战略成本、成长、偷窃和强制目标 | `PotionUsePolicy`、`FinalPlanOrdering` | 未接入 | 待迁移 | 单人政策等价及联合反事实 |
| BFS/DFS 有限 oracle | 无生产对应 | `JointOfflineSearch` | 已迁移 | 2 Actor 同值/同动作/同键 |
| 生产规模 Beam/BFWS、Pareto、保路、多样性 | `CombatBeamSolver.*` | 尚无联合实现 | 待迁移 | 小根 oracle、固定预算确定性和质量哨兵 |
| 串行/固定 lane 并行、预算、取消、内存压力 | `AdmittedJobScheduler`、`SearchBudgetLedger` | 尚无联合实现 | 待迁移 | DOP1/DOPN 同动作/同键、无泄漏 |
| 严格回放 | `ReplayAction`、无人测试差分 | 仅同一模拟根基础动作 | 待迁移 | 2/4 Actor 完整 actual/simulated 逐步对照 |

## F0 冻结基线

后续阶段必须保留以下单人合同：

1. `PlanAction` 和 `PlanCardChoice` 未显式指定 Actor 时仍为 Actor0。
2. 生产 `CombatBeamSolver` 接受 ActorCount=1，稳定拒绝 ActorCount>1。
3. `ContinuationStamp` 的单人文本格式不因多人字段发生变化。
4. 单人候选顺序仍由既有 `ExpansionPlan`、`AdmittedJobScheduler` 和各 continuation source 决定。
5. 单人终局仍由 `FinalPlanOrdering`/`RouteQualityPolicy` 决定，联合临时目标不得反向接入生产单人路径。
6. `COOP-P2-SINGLE-ROOT`、`COOP-ACTOR-PLAN-CONTRACT`、`COOP-ACTOR-CANDIDATES`、`COOP-JOINT-REPLAY`、`COOP-PRODUCTION-SINGLE-BOUNDARY` 是 F0 最小动态代表集；各后续阶段只重跑受影响的最小项。

## 已知迁移问题

| 编号 | 首个差异/风险 | 当前处理 | 后续阶段 |
|---|---|---|---|
| F-ISSUE-001 | 不同角色 HP 价值不可直接相加 | 保留总战损 workaround，同时保留逐 Actor 向量；不在迁移阶段重设权重 | F8 后续研究 |
| F-ISSUE-002 | 生产 Runtime/UI/部署大量使用 `LocalContext.GetMe()` | 保持生产单人边界，不把它们纳入离线联合模型 | F12 非目标门禁 |
| F-ISSUE-003 | 联合 BFS/DFS 在全员屏障处停止，未执行敌方生命周期 | 明确标为待迁移，不把现有 oracle 证据外推 | F7/F10 |
| F-ISSUE-004 | `PlanRelicEffect` 是路线显示证据，不等于主动遗物动作 | 反编译原版未发现战斗内 `UseRelic`/`ActivateRelic` 提交入口；不新增动作类型，遗物 Hook 触发语义归 F5 | F1 已定边界，F5 验证触发 |
| F-ISSUE-005 | 多 Actor 远端玩家获得格挡时，multiplayer scaling mirror 直接按玩家数拒绝 | 按原版精确镜像：玩家目标/非 powered 不缩放；主次敌人按人数及 Act/Boss 系数缩放 | F2 已修复；失败 `e62462a1435d4bbbbe0c48965528985b`，通过 `34221092e12f40f5addc9fb89219ff94` |

