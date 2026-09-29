# 离线四 Actor 单人功能迁移库存

> 状态：F0-F12 已完成并通过最终同源码门禁  
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
| 每 Actor Power、遗物、药水、球、宠物、角色资源 | `Search/SimulatedCombatState*.cs`、`Prediction/*` | F5a-F5d 已覆盖根元数据、动态机制、五角色代表与真实召唤所有权；F4c2d 补齐逐 Actor 药水奖励展望 | 已迁移 | Power/遗物/药水元数据/Orb/Stars/Shiv/Osty 与 Fork/兄弟隔离均通过 |
| 敌人 roster、AI、行动和隐藏状态 | `Prediction/Monster*`、`SimulatedCombatState*.cs` | 复用模拟器；完整 continuation 保存 roster、行动、AI 与隐藏状态 | 已迁移 | 2/4 Actor 跨轮搜索/根级回放在每动作与屏障 checkpoint 逐点同 continuation/状态键 |
| 九条战斗 RNG、Hook 历史和战斗历史 | `Engine/InCombat/Simulation/*`、`PredictionStateStore` | 九条 RNG 与逐 Actor 历史已进入完整 continuation | 已迁移 | Niche 单次消费、兄弟 Fork 隔离和 2/4 Actor 跨轮逐点 strict replay 通过 |
| 续用戳 | `Runtime/ContinuationStamp.cs` | Actor 分段字段 | 已迁移 | 单人文本兼容；2/4 Actor 代表字段扰动通过，机制字段由 F4/F5继续验证 |
| 完整状态键 | `CombatBeamSolver.StateEvaluation.cs` | 联合屏障 + 完整 continuation 文本 | 已迁移 | Actor 代表字段及屏障扰动改变键；机制专项由 F4-F7补证据 |

## 候选、动作和选择

| 功能面 | 单人权威入口 | 联合入口 | F0 状态 | 最低关闭证据 |
|---|---|---|---|---|
| 普通出牌与 EndTurn | `CombatBeamSolver.Expansion*.cs` | `JointActionExpander`、`JointActionTransition` | 已迁移 | 搜索与回放同键、2/4 Actor 非本地动作 |
| AnyEnemy、AnyAlly 基础目标 | `CombatBeamSolver.Expansion.cs` | `JointActionExpander.ResolveTargets` | 已迁移 | 目标顺序、队友目标与 Actor owner |
| AnyPlayer、Self、全体、无目标及动态目标 | `CombatBeamSolver.Expansion*.cs`、card mirrors | F3a 已覆盖全部当前 TargetType；AnyPlayer/AnyAlly 显式枚举 | 已迁移 | 2/4 Actor 手动目标集合；非手动目标由 mirror 解析 |
| 自动牌、重复牌、复制牌、生成牌、回手与临时牌 | `CombatBeamSolver.CardChoiceContinuation.cs`、`Prediction/*` | F3b 从权威出牌后 pending spec 补全动态选择；F6b 覆盖回合尾自动牌与 Decisions 重复选择链 | 已迁移 | Havoc→Second Wind、Hellraiser→Seeker Strike、Decisions→Prepared 多次选择及既有生成/复制代表通过 |
| 卡牌实例 identity、升级、附魔和状态 occurrence | `PreparedCardAction`、`CombatPlan.cs` | F3a 使用同一 `ChoiceCardKey`/state occurrence 回放 | 已迁移 | Armaments/Strike 候选回放与搜索单步同键 |
| 药水使用、目标、槽位、生成、复制和替换 | `CombatBeamSolver.*Potion*.cs`、`Prediction/Potion*` | F4a/F4b 按 Actor 枚举槽位/目标、九类主选择和 Entropic Brew 生成；F4d 跨回合槽位/消耗完成 | 已迁移 | 2/4 Actor 独立候选、九类选择、生成及第二轮药水严格回放通过 |
| 主动遗物动作 | 原版无战斗内独立提交入口；遗物由 Hook 触发 | 不新增伪造动作；`PlanRelicEffect` 仍是路线注释 | 明确不支持 | F1 反编译检索无 `UseRelic`/`ActivateRelic` 战斗动作；遗物触发归 F5 |
| 主选择、嵌套选择、回合开始/结束选择 | `PrimaryChoiceReplay`、`CardChoiceContinuation`、`PotionChoiceContinuation` | F6a-d 已覆盖 owner/source、主/嵌套 placement、多 Actor 原序、回合边界和重复出牌 | 已迁移 | 动态嵌套、双 Actor 药水帧、Tools of the Trade、Hellraiser/Joss Paper 与 Decisions 重复链通过 |
| opening、fixed-prefix、cycle、cross-turn、plan continuation | `CombatSearchCoordinator.*`、`FrontierContinuationScheduler` | fixed-prefix 与普通 BFS/DFS/Beam/BFWS 已自动跨轮；联合模型复用同一节点转移，不复制单人 opening/cycle 政策 | 已迁移 | 2/4 Actor 跨轮搜索与选择恢复通过；单人候选序仍归最终门禁 |

## 结算机制

| 功能面 | 单人权威入口 | 联合入口 | F0 状态 | 最低关闭证据 |
|---|---|---|---|---|
| 卡牌和伤害/格挡基础结算 | `Engine/InCombat/Simulation/*`、`Mirrors/*` | 复用同一 simulator/mirror | 已迁移 | Actor0-3 同机制 strict diff |
| Power 创建、叠加、减少、移除和生命周期 | `Mirrors/Power*`、`Prediction/*Power*` | F5b 已通过通用远端生命周期、One For All 全队应用与 Shuriken Hook 代表 | 已迁移 | applier/target、叠加/移除、Fork/键、四 Actor 全队来源均通过 |
| 被动遗物、计数、消耗和共享遗物 | `Mirrors/Relic*`、`Prediction/*Relic*` | F5c 已通过远端 Shuriken 计数/触发及同型 Throwing Axe 独立消耗 | 已迁移 | Actor1 触发/消耗、Actor0 同型隔离与父 Fork 均通过 |
| 球槽、球序、触发和角色资源 | `SimulatedCombatState*Orb*`、角色 mirrors | F5d 已验证 Defect Orb、Regent Stars、Silent Shiv、Necrobinder Osty 的远端归属 | 已迁移 | 资源键/续用戳/兄弟隔离及 Silent 动作级生成通过 |
| 宠物和召唤物所有权 | `Prediction/*`、相关 card/power mirrors | F5d 由远端 Necrobinder 实际打出 Afterlife 并按 Player 查询 Osty | 已迁移 | Actor2 召唤存活，Actor0/1 无 Osty，所有权稳定 |
| 第三方 subscriber 与登记表 | `THIRD_PARTY_ADAPTERS.md` 所列 registry | 复用单人登记/惰性判定；未知 gameplay subscriber 稳定拒绝 | 已迁移 | inert 放行；未知战斗 hook 类型/scope 明确失败 |

## 回合、终局和搜索政策

| 功能面 | 单人权威入口 | 联合入口 | F0 状态 | 最低关闭证据 |
|---|---|---|---|---|
| Actor 独立结束与全员屏障 | 单人 `EndTurn` 尾部 | `JointTurnState` | 已迁移 | 任意顺序、死亡 Actor、提前跨回合拒绝 |
| 玩家回合尾 Hook、敌方回合、下一回合开始/抽牌 | `CombatBeamSolver.Terminal.cs`、simulation commands | 联合 `ExpandBarrier` 共用 F7 玩家尾、额外回合/敌方侧和下一玩家侧，挂起选择回到稳定屏障节点展开 | 已迁移 | 固定前缀与普通 BFS/DFS/Beam 自动跨轮、Tools/Power/遗物/重复出牌选择通过 |
| 额外回合、死亡、复活、逃跑和失去资格 | `Prediction/*`、`SimulatedCombatState*.cs` | F7b 完成死亡资格、Parafright 复活、Fat Gremlin 逃跑及远端 Actor Ambergris 额外回合子集 | 已迁移 | 死亡/复活/逃跑及额外回合 Actor 子集、TurnNumber/RoundNumber、敌方跳过和来源消费通过 |
| 敌方死亡、召唤、行动尾部和终局 | `Prediction/Monster*`、`CombatBeamSolver.Terminal.cs` | 全部当前 `MonsterMoveEffects.Supports` 已集中分类 owner/target/pre/post mixed；未知语义继续显式拒绝 | 已迁移 | Inhale、Oil Spray、Rage、Soul Siphon、Gas Bomb、Living Fog、复活/逃跑与 Tough Egg 单次 RNG 通过 |
| 单人终局政策 | `FinalPlanOrdering`、`RouteQualityPolicy` | 联合比较器独立位于 `Search/Coop`，单人入口未改 | 已保留 | 结构门禁 + 最终 F12 单人等价哨兵 |
| 团队目标 | 无单人对应 | `JointObjective` 完整字典序；总战损仍为 workaround | 已迁移 | 终局边界、存活、分布、资源、成长、偷窃和稳定决胜通过 |
| 药水政策、战略成本、成长、偷窃和强制目标 | `PotionUsePolicy`、`FinalPlanOrdering` | F4c 完成硬政策、共享预算 Smart 反事实、精确药量层、逐 Actor 奖励/Ambergris/Boss relief；F8 终局完成 | 已迁移 | 无收益拒绝、用药层、奖励抵扣及专门阈值通过 |
| BFS/DFS 有限 oracle | 无生产对应 | `JointOfflineSearch` | 已迁移 | 2 Actor 同值/同动作/同键 |
| 生产规模 Beam/BFWS、Pareto、保路、多样性 | `CombatBeamSolver.*` | 有界联合 Beam/BFWS、共享预算/转置、Actor/药水/Pareto 保路与固定 lane | 已迁移 | 2 Actor 两动作、4 Actor 一动作 oracle 及串并行等价通过 |
| 串行/固定 lane 并行、预算、取消 | `AdmittedJobScheduler`、`SearchBudgetLedger` | F9 已实现联合固定 lane、共享预算和取消边界 | 已迁移 | DOP1/DOP4 同动作/同键/同展开数，预算与预取消通过 |
| 内存压力与快照所有权 | `AdmittedJobScheduler`、`SearchMemoryPressureSignal` | 联合搜索使用有界 frontier/OPEN 和共享状态预算；测试观察器不持有节点 | 已迁移 | 固定工作量、停止原因，以及完成/取消/注入异常后三类子模拟器弱引用全部释放 |
| 严格回放 | `ReplayAction`、无人测试差分 | 搜索节点与根级回放分别产生逐动作/屏障 checkpoint | 已迁移 | 2/4 Actor 先完整跨过敌方轮再终局，全部 checkpoint 与终态严格一致 |

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
| F-ISSUE-003 | 联合 BFS/DFS 曾在全员屏障处停止，未执行敌方生命周期 | 已统一经 `ExpandBarrier` 执行玩家尾、额外回合或敌方侧和下一轮；2/4 Actor 跨轮逐点 strict replay 关闭缺口 | F7/F10 已关闭 |
| F-ISSUE-004 | `CombatRootSnapshot` 的可再生药水、Throwing Axe、战后回血与可搜索药水原先只捕获本地玩家 | F5a 移入 `CombatActorRoot`；旧根字段保留为本地 Actor 兼容视图 | F8 联合终局继续消费逐 Actor 元数据 |
| F-ISSUE-004 | `PlanRelicEffect` 是路线显示证据，不等于主动遗物动作 | 反编译原版未发现战斗内 `UseRelic`/`ActivateRelic` 提交入口；不新增动作类型，遗物 Hook 触发语义归 F5 | F1 已定边界，F5 验证触发 |
| F-ISSUE-005 | 多 Actor 远端玩家获得格挡时，multiplayer scaling mirror 直接按玩家数拒绝 | 按原版精确镜像：玩家目标/非 powered 不缩放；主次敌人按人数及 Act/Boss 系数缩放 | F2 已修复；失败 `e62462a1435d4bbbbe0c48965528985b`，通过 `34221092e12f40f5addc9fb89219ff94` |
| F-ISSUE-006 | 同 ID 卡牌的 `CardOccurrence` 会在前一实例离开手牌后重编号；直接串联首态候选会使后续严格回放找不到实例 | 已明确为前缀相对地址：每步重枚举并记录动作，原根完整回放与增量状态同键/同续用文本 | F6d 已关闭；禁止拼接同一首态的多个候选 |
| F-ISSUE-007 | 联合玩家侧在逐 Actor PhaseOne 循环内统计虚无牌时，较早 Actor 的共享参与者结算会先耗尽后续 Actor 手牌，导致 Joss Paper 等共享 PhaseTwo 来源少计 | 在任何 PhaseOne 前按稳定 Actor 顺序冻结全体虚无牌总数，再交给一次共享 PhaseTwo | F7a 已关闭；失败 `d8805dc2888c4b60988c4422a027a946`，通过 `7d629eea37754cdfb240a1d6580bf041` |
| F-ISSUE-008 | 联合 expander 在来源牌离手前按根手牌静态建立主选择，`Decisions, Decisions` 会错误选择自身，实际 OnPlay spec 已不含来源牌 | 主选择与嵌套选择统一从权威动作探针的 pending spec 展开；仅原版必需空选择保留预填 | F6b 已关闭；失败 `3ee53c2a957b4fdaaba7d64e9db8873a`，通过 `05ebefd640864595ae272ceae4ae8e37` |

