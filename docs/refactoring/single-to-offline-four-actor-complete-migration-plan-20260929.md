# 从单人 CombatSolver 到离线四 Actor 完整自动规划开发计划

> 状态：执行中；前置联合模型 P0-P12、F0-F2 已完成，F3-F5 进行中，F6-F12 待完成
> 日期：2026-09-29  
> 基线提交：`f14acea6`  
> 目标：在不改变单人 CombatSolver 语义的前提下，建立一个可以控制最多四名 Actor、完整覆盖单人战斗机制的离线联合自动规划器。  
> 当前目标函数：暂时按每名 Actor 战损之和排序；该口径是 workaround，不代表不同角色的 HP 价值相同。

## 0. 执行进度

| 阶段 | 状态 | 提交/证据 |
|---|---|---|
| F0 单人全功能清单与基线 | 已完成 | [功能迁移库存](offline-four-actor-feature-inventory-20260929.md)；Windows 结构门禁通过；复用同源码五项动态基线 |
| F1 统一联合单步转移 | 已完成 | `JointActionTransition` 统一卡牌/选择/药水/EndTurn；`COOP-JOINT-REPLAY` 通过 |
| F2 Actor 状态/Fork/快照/续用 | 已完成 | 2/4 Actor 远端字段扰动与 Fork 隔离通过；修复多人敌人格挡缩放 mirror |
| F3 完整卡牌与目标 | 进行中 | F3a/F3b 已接入目标/实例身份、基础及动态嵌套选择；F3c 已接入同回合 fixed-prefix 请求并通过 BFS/DFS；opening/cycle/cross-turn 仍待完成 |
| F4 完整药水 | 进行中 | F4a/F4b 已完成 2/4 Actor 独立槽位/目标、九类手动选择与 Entropic Brew 生成；Smart/强制/药量/战略成本等政策待完成 |
| F5 Power/遗物/球/宠物/角色资源 | 进行中 | F5a 根元数据、F5b 通用远端 Power、F5c 远端 Shuriken 动作链及 F5d 三类角色资源已通过；全队/消耗/共享/Silent/第三方边界待补 |
| F6 选择与嵌套 continuation | 未开始 | - |
| F7 联合回合与敌方生命周期 | 未开始 | - |
| F8 联合终局目标 | 未开始 | - |
| F9 联合 Beam/BFWS | 未开始 | - |
| F10 strict replay 与差分 | 未开始 | - |
| F11 性能与确定性 | 未开始 | - |
| F12 最终门禁 | 未开始 | - |

## 1. 当前基线

前置计划已完成以下基础设施：

- `CombatRootSnapshot` 已拥有按固定顺序排列的 `CombatActorRoot` 目录。
- `ContinuationStamp`、`PlanAction`、`PlanCardChoice` 已带 Actor 维度。
- `JointTurnState` 已建模可行动、结束、死亡和全员结束屏障。
- `JointActionExpander` 已支持基础手牌、目标和 EndTurn 候选。
- `JointCombatSnapshot` 已保存联合 Actor 投影、续用戳和状态键。
- `JointActionTransition` 已成为 BFS、DFS oracle 和严格回放共享的单步入口。
- `JointPlanReplayer` 已支持基础卡牌动作和非本地 Actor 回放。
- `JointOfflineSearch` 已有 BFS 状态去重成员和不去重 DFS oracle。
- 真实 2/4 Actor 离线根已通过根目录、基础候选、状态键和非本地 Actor 回放测试。
- 生产 `CombatBeamSolver` 仍明确拒绝 `ActorCount > 1`。

当前实现不能声称支持完整四人自动规划，因为药水、主动遗物、完整选择链、跨回合敌方生命周期、全部单人候选政策和生产级联合 Beam 仍未完成。

## 1.1 P 阶段与 F 阶段的关系

两套编号不是两次相同的计划：

| 阶段 | 目的 | 完成后的结论 |
|---|---|---|
| P0-P12 | 建立 Actor-aware 根、计划、屏障、快照、基础回放、toy oracle 和生产单人边界 | 证明联合模型的架构边界和最小搜索合同成立 |
| F0-F12 | 把单人 CombatSolver 的完整语义迁移到联合模型，并完成真实 2/4 Actor 严格验证 | 才能声称离线四 Actor 完整自动规划 |

具体对应关系：

- P1/P2 的根目录和续用戳属于 F2 的前置基础；它们没有覆盖全部 Actor 状态。
- P3/P4 的动作 Actor 和回合屏障属于 F1、F3、F6、F7 的合同基础；它们没有完成全部选择和跨回合生命周期。
- P5/P6 的基础卡牌候选和快照属于 F2/F3；它们没有覆盖药水、遗物、Power 和完整选择链。
- P7 的目标比较器属于 F8 的临时版本；总战损仍是 workaround。
- P8 的基础回放属于 F10 的入口；它还没有完整敌方回合和全机制 strict diff。
- P9/P10 的 toy oracle 和 2/4 Actor 根验证属于 F9/F10 的早期证据；它们不等于正常规模四人搜索完成。
- P11 的生产单人边界是 F12 的非目标约束，不是多人 Runtime 已实现。

因此，P 阶段完成代表“骨架和最小合同可用”，F 阶段完成才代表“单人机制已完整迁移并通过四人离线门禁”。

## 2. 完成定义

只有同时满足以下条件，才可以声称“离线四 Actor 完整自动规划”完成：

1. Actor 数量为 1 时，与当前单人 CombatSolver 的动作、终局、续用和状态键语义等价。
2. Actor 数量为 2、3、4 时，所有动作、选择、资源和回合生命周期均使用同一套联合模型，不存在四人专用复制分支。
3. 单人 CombatSolver 当前支持的卡牌、药水、遗物、Power、球、角色资源、选择、RNG、死亡、召唤、复活、跨回合和计划机制均有联合实现或明确的可验证拒绝边界。
4. 联合搜索、独立 oracle 和严格回放使用同一个权威单步结算入口。
5. 真实 2 Actor 和 4 Actor fixture 均通过 actual/simulated 严格差分；失败必须定位到首个字段差异。
6. 四 Actor 计划可以从冻结根完整回放到终局，且搜索计划与回放计划状态逐步一致。
7. 串行和固定并行展开在相同根、政策和预算下保持确定性。
8. 生产 Runtime 仍明确保持单人边界；本计划不接入网络客户端控制。

## 3. 不变量与所有权

每个新增或迁移状态必须记录：

- 主线程根从哪里、何时捕获；
- 状态属于 `CombatRootSnapshot`、`SimulatedCombatState`、克隆 Model 还是 `PredictionStateStore`；
- Fork 使用深拷贝、COW 还是不可变共享；
- Actor 引用如何通过同一 `PredictionForkContext` 重映射；
- 是否影响合法动作或结算，是否进入状态键；
- 是否跨回合存活，是否进入 `ContinuationStamp`；
- actual/simulated 严格差分如何观察；
- 创建、叠加、减少、移除、清空和死亡时点；
- Fork 前是否要求事务为空。

硬性规则：

- worker 只读取冻结根、影子状态和注入的不可变策略；
- 不读取 live HP、能量、牌堆、Power、遗物、药水或 RNG；
- 不使用 `Players.Single()`、`LocalContext.GetMe()` 或本地玩家字段隐式代表全部 Actor；
- 未知语义显式失败，不返回默认值、不跳过候选、不吞异常；
- 不把 UI、日志、诊断或启发式字段加入战斗状态键；
- 生产单人入口拒绝多人，不把多人悄悄降级为本地 Actor。

## 4. 分阶段实施

### F0：冻结单人全功能清单与基线

目标：把“完全覆盖单人”从口号变成可检查清单。

权威库存：[离线四 Actor 单人功能迁移库存](offline-four-actor-feature-inventory-20260929.md)。后续阶段必须在同一表中关闭缺口，不另建一份漂移清单。

工作：

- 从 `CombatBeamSolver`、`CombatSearchCoordinator`、`SimulatedCombatState`、`Prediction` 和 `Mirrors` 列出所有候选入口。
- 盘点卡牌、药水、遗物、Power、球、宠物、选择、RNG、死亡、召唤、复活、回合和计划机制。
- 为每项标记 `已迁移`、`待迁移`、`明确不支持` 或 `需要真实原版证据`。
- 固定单人根、政策、预算、动作序、终局和续用证据。

验收：

- 清单能映射到源码入口和测试 fixture；
- 单人基线可重复；
- 后续每个阶段只关闭清单中的一组缺口。

### F1：统一联合单步转移入口

目标：搜索、oracle、回放和未来部署全部使用同一个动作结算入口。

工作：

- 扩展 `JointActionTransition` 支持卡牌、药水、遗物、选择和 EndTurn。
- 统一 Actor、实例、目标、资源、选择帧和终局检查。
- 所有失败保留动作、Actor、事务和状态上下文。
- 移除 `JointPlanReplayer` 中重复的卡牌定位和目标定位逻辑。

验收：

- 同一动作从搜索和回放得到相同状态键；
- 未解决选择和不支持效果明确失败；
- 单人卡牌回放与原路径逐步等价。

### F2：完成 Actor 状态、Fork、快照和续用审计

目标：四 Actor 的完整分支状态不遗漏任何影响未来语义的字段。

工作：

- 补齐每个 Actor 的 HP、格挡、资源、牌堆、Power、遗物、药水、球、宠物和角色专属状态。
- 补齐共享敌人、AI、召唤 roster、RNG、Hook 历史和战斗历史。
- 把所有跨回合字段接入 `ContinuationStamp`。
- 为 Actor 身份、回合屏障、死亡集合和共享状态补充状态键字段。
- 审计所有 `Single()`、`First()`、`LocalContext.GetMe()` 和本地玩家缓存。

验收：

- 2/4 Actor 同根 Fork 与实际状态逐字段一致；
- 任意单字段变更都会改变必要的状态键或续用戳；
- 单人 `ContinuationStamp` 保持兼容；
- 迁移中发现的首个差异记录到测试矩阵，不用默认值掩盖。

### F3：完整卡牌和目标候选迁移

目标：四个 Actor 可以枚举单人搜索已经支持的全部卡牌动作。

工作：

- 普通卡牌、自动牌、重复牌、复制牌、生成牌、回手牌、升级和临时卡牌。
- AnyEnemy、AnyAlly、AnyPlayer、Self、全体和无目标卡牌。
- 卡牌实例 occurrence、升级、附魔和来源身份。
- 卡牌产生的选择和嵌套选择。
- opening、cross-turn、cycle、fixed-prefix 和 continuation 入口。

验收：

- 2/4 Actor 目标枚举与原版目标规则一致；
- 同名卡牌逐实例匹配；
- 复杂卡牌的选择链可严格回放；
- 单人候选序和结果不退化。

执行拆分：

- F3a（已完成）：卡牌实例身份、完整 `TargetType` 分类与基础动作选择。
- F3b（已完成）：动作执行时出现的动态／嵌套选择，最多 16 层且普通执行错误继续传播。
- F3c（已完成）：`JointOfflineSearchRequest` 携带同回合固定前缀、动作上限和状态上限；BFS 与独立 DFS oracle 从同一权威 transition 回放前缀后继续搜索。`COOP-MULTI-ACTOR-ROOT` 运行 `5f14e64a366e4ad286cc03099b4bc8c4` 通过 `FixedPrefix:BfsVsDfs`，实例已删除。
- F3d（待完成）：opening/cycle 成员和单人候选序哨兵；跨回合前缀必须等待 F7 的联合回合生命周期，当前入口显式拒绝，不把同回合证据外推。

### F4：完整药水迁移

目标：每名 Actor 的药水槽和所有单人药水政策在联合搜索中独立工作。

工作：

- 药水槽、实例身份、目标、消耗、生成、复制和替换。
- 9 类手动选择药水和自动药水。
- 强制用药、Smart、无药基线和精确药量层。
- 药水战略成本、保命资源、免费生成来源和跨回合药水计划。
- 药水 continuation 与联合终局比较。

验收：

- 四个 Actor 可以同时拥有独立药水而不互相污染；
- 药水候选实际改变对应 Actor 的状态；
- 强制药政策和用药数量与单人规则一致；
- 药水回放、状态键和续用戳逐步一致。

执行拆分：

- F4a（已完成）：`JointActionExpander` 按 Actor 模拟槽位枚举可搜索药水和目标；需要选择的药水先由权威 transition 执行生成阶段，再把主选择写入 `PlanAction.Choice`，后续选择才进入 nested 链。`COOP-MULTI-ACTOR-ROOT` 运行 `cf7241344aa044cbbe3bbdff233257fb` 通过 2/4 Actor 独立 Block Potion/Gambler's Brew 候选、选择 owner 与 BFS/DFS 对照。
- F4b（已完成）：四种生成牌药水和 Ashwater、Droplet of Precognition、Gambler's Brew、Liquid Memories、Touch of Insanity 九类主选择均由 Actor1 严格回放；Entropic Brew 生成改变联合状态键和续用戳。`COOP-MULTI-ACTOR-ROOT` 运行 `35794e9d204e4a11b8b54607dbeca5d7` 通过。
- F4c（进行中）：F4c1 已完成 Actor+槽位指令、Disabled/Force、最少/最多用药数及单人战略成本复用；Smart 相对无药基线和联合反事实待 F4c2/F8。
- F4d（待完成）：跨回合药水 continuation，依赖 F7 生命周期与 F8 终局政策。

### F5：Power、遗物、球、宠物和角色专属资源

目标：迁移所有会改变卡牌合法性、结算或终局的 Actor 私有和共享机制。

工作：

- Power 创建、叠加、减少、移除、生命周期、applier/target。
- 被动遗物、主动遗物、触发计数和遗物消耗。
- 队友目标、全队效果和共享遗物。
- 球槽、球顺序、球触发和宠物状态。
- 角色专属资源、角色专属牌和角色专属遗物。
- 第三方 Mod 登记点、Hook subscriber 和 mirror 覆盖。

验收：

- 同一机制在 Actor0/Actor1/Actor2/Actor3 上结算一致；
- 触发顺序、来源和目标不依赖本地玩家；
- 未登记第三方语义明确拒绝；
- 关键机制至少有一个 actual/simulated strict diff fixture。

执行拆分：

- F5a（已完成）：`CombatActorRoot` 捕获逐 Actor 可搜索药水、Throwing Axe 可用性、Petrified Toad 可再生药水语义和战后遗物回血；旧根级单人字段保持并与本地 Actor 逐值一致。Actor1 独有 Petrified Toad 的 `COOP-MULTI-ACTOR-ROOT` 运行 `f6a9adae0f5b4dc49e472f21747659af` 通过。
- F5b（进行中）：通用 StrengthPower 已通过跨 Actor applier、远端 target、叠加/移除、Fork 隔离和状态键/续用戳；全队效果与具体 Hook 时序代表集待补。
- F5c（进行中）：Actor1 连续三次攻击经权威联合 transition 触发自己的 Shuriken，计数、Strength 归属、状态键/续用戳与父 Fork 隔离通过；`COOP-MULTI-ACTOR-ROOT` 运行 `e0a5cdea18764e00ae656242cf1cfc46` Passed。遗物消耗和共享边界代表仍待补。
- F5d（进行中）：混合 Ironclad/Defect/Regent/Necrobinder 根及 Orb/Stars/Osty 隔离已通过；Silent 专属机制与宠物真实多人所有权仍待补。
- F5e（待完成）：第三方登记点和未知 subscriber 拒绝边界。

### F6：完整选择系统和嵌套 continuation

目标：所有单人挂起选择都可以由指定 Actor 继续执行。

工作：

- 选择牌、弃牌、保留、升级、目标、药水、遗物和生成内容。
- 回合开始选择、EndTurn 选择、自动牌选择和重复出牌选择。
- 嵌套选择、选择前缀、选择 owner、候选实例和原序消费。
- 多 Actor 同时存在挂起选择时的唯一所有权。

验收：

- 选择帧携带 OwnerActor 和 SourceAction；
- 同名候选按实例匹配；
- 选择失败不会污染兄弟分支；
- 单人选择续执行合同逐位保持。

### F7：完整联合回合和敌方生命周期

目标：从玩家动作一直推进到下一轮玩家动作，完整复现原版回合生命周期。

工作：

- Actor 独立结束、额外回合和独立 `TurnNumber`。
- 全员屏障后的玩家回合结束 Hook、Power、遗物、球和牌堆效果。
- 敌方回合、敌方 AI、敌方 Hook、伤害和状态结算。
- 下一轮 Actor 回合开始、抽牌、自动阶段和角色专属开始效果。
- Actor 死亡、复活、召唤、逃跑和失去行动资格。

验收：

- 一个 Actor 结束不阻塞其他 Actor；
- 只有所有有效 Actor 到达屏障才进入敌方回合；
- 死亡 Actor 不阻塞屏障；
- 跨回合实际状态与模拟状态逐字段一致；
- 敌方行动顺序和 RNG 计数保持一致。

### F8：联合终局目标和政策

目标：保留现有单人终局政策，同时提供可替换的团队目标。

第一版字典序：

1. 胜利优于失败；
2. 全队存活优于有人死亡；
3. 每人战损之和更低；
4. 逐 Actor 战损向量更优；
5. 药水、遗物和保命资源消耗更少；
6. 成长、偷窃和强制政策满足度更高；
7. 回合数更少；
8. 动作数更少；
9. 稳定联合动作序决胜。

约束：

- 总战损只是 workaround；
- 角色 HP 价值差异作为后续研究记录；
- 不用总 HP 相加掩盖某个 Actor 死亡；
- 未完成路线使用明确的下界和边界，不伪造完整战损。

验收：

- 总损失相同但分布不同的路线可区分；
- 一人死亡但总 HP 更高时政策正确；
- 药水换回合、遗物换战损和成长目标满足度排序正确；
- 单人 `FinalPlanOrdering` 结果不改变。

### F9：联合 Beam/BFWS 搜索和保路

目标：把基础 BFS/DFS 验证升级为正常规模可运行的离线联合搜索。

工作：

- 联合 `SearchNode`、父链、完整快照和转置表。
- Beam 排名、Pareto、必保候选、多样性通道和药水配额。
- 联合动作顺序的保守枚举；未经证明不做 Actor 动作交换。
- 共享时间、节点、内存和取消预算。
- 固定 worker lane、确定性提交和快照所有权。
- 取消、异常、内存压力和终局排空。

验收：

- 2 Actor 小 fixture 与 DFS oracle 一致；
- 4 Actor 小 fixture 与 oracle 或可证明分解一致；
- 同预算串行/并行动作和状态一致；
- 不以扩大 Beam、时间或节点掩盖语义差异。

### F10：完整严格回放与 actual/simulated 对照

目标：证明“搜索找到的路线”就是“从同一根可以执行的路线”。

每一步比较：

- Actor、卡牌/药水/遗物实例和目标；
- HP、格挡、能量、Stars 和所有牌堆；
- Power、遗物、药水、球、宠物和角色资源；
- 敌人 HP、格挡、行动、AI 和 roster；
- RNG 状态和计数；
- 选择上下文、回合阶段和屏障；
- 死亡、复活、召唤、终局和续用戳。

验收：

- 2 Actor 完整战斗 strict diff 通过；
- 4 Actor 完整战斗 strict diff 通过；
- 第一处差异可定位到字段、动作和 Hook；
- 回放失败时停止，不继续部署部分路线。

### F11：性能、确定性和组合爆炸控制

目标：在语义完成后让四 Actor 搜索可在固定预算内运行。

工作：

- 测量 Actor 数量、动作排列、选择链、药水和状态键增长。
- 只采用有证明或有严格 fixture 证据的动作交换/偏序约简。
- 审计状态键分配、Fork、快照、转置和释放。
- 使用既有 Runtime 内存信号和请求预算，不新增无界队列。
- 记录四 Actor 的工作量、停止原因、峰值和质量，不外推可见 FPS。

验收：

- 固定根 AB 对照没有质量退化；
- 取消和异常无快照泄漏；
- 搜索在状态上限、节点上限和时间上限内稳定结束；
- 性能结论区分 headless 工作量和可见 Steam 性能。

### F12：四 Actor 离线完成门禁

必须一次性通过：

- 单人全功能等价代表集；
- 2 Actor 完整 strict diff；
- 4 Actor 完整 strict diff；
- 药水、遗物、Power、选择、RNG、死亡召唤和跨回合代表集；
- BFS/Beam 与 DFS oracle 对照；
- 串行/并行确定性；
- Windows 结构门禁；
- Release 构建；
- OfflineSearchHarness 或 Testing 离线入口；
- 文档、测试矩阵和迁移问题记录。

完成后才可以说：

> CombatSolver 已具备覆盖单人机制的离线四 Actor 联合自动规划能力。

这仍不表示已经具备真实多人 Runtime、客户端控制、网络动作、承诺性修正或线上部署能力。

## 5. 迁移问题记录规则

迁移中遇到类似以下问题时，先记录再按当前阶段修复：

- `Players.Single()`、本地玩家隐式所有权；
- 共享和私有资源边界不清；
- Actor 身份遗漏在状态键、选择帧或续用戳；
- 触发顺序依赖本地客户端；
- 角色 HP、资源或战损不可直接比较；
- 单人终局政策无法直接聚合到团队；
- 敌方回合或死亡流程只保存了本地 Actor 状态；
- 第三方 Hook 未提供稳定 Actor 所有权。

每条问题至少记录：首个差异、源码职责、影响范围、临时修复、单人回归证据、多人验证状态和后续研究项。

## 6. 提交与验证节奏

建议按职责拆分提交：

1. F1-F2：统一转移入口和完整状态审计；
2. F3：卡牌、目标和基础选择；
3. F4：药水；
4. F5：Power、遗物、球、宠物和角色资源；
5. F6-F7：选择链和完整回合生命周期；
6. F8：团队目标和终局政策；
7. F9：联合 Beam/BFWS；
8. F10：严格回放和 actual/simulated 对照；
9. F11-F12：性能门禁、文档和最终离线完成结论。

每个提交必须：

- 只包含一个职责块；
- 更新 `docs/ARCHITECTURE.md`、`docs/DEVELOPMENT_NOTES.md` 和 `docs/TEST_MATRIX.md`；
- 运行与改动相称的最小验证；
- 明确未覆盖的机制；
- 保留失败的首个差异和实例清理证据；
- 不把 toy oracle、静态阅读或单人根测试写成四人完整通过。

## 7. 非目标

本计划不包含：

- Steam 联机大厅；
- Host/Client 网络协议；
- 控制其他客户端；
- `NetPlayCardAction`、网络药水或网络选择提交；
- 断线、重连、延迟、乱序和承诺协议；
- 生产多人 Overlay；
- 版本提升、发包、创意工坊上传或远端同步。

这些内容需要在 F12 完成后另立 Runtime/网络计划。
