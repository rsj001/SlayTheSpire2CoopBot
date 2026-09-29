# 从单人 CombatSolver 到离线四 Actor 完整自动规划开发计划

> 状态：执行中；前置联合模型 P0-P12、F0-F9 已完成，F10-F12 待完成
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
| F3 完整卡牌与目标 | 已完成 | F3a/F3b 目标/实例身份与动态嵌套选择；F3c fixed-prefix；F3d 自动跨轮 BFS/DFS/Beam/BFWS 均通过；单人候选序归 F12 哨兵 |
| F4 完整药水 | 已完成 | F4a/F4b 独立槽位/九类选择/生成、F4c 硬政策与完整 Smart 反事实、F4d 跨回合均通过 |
| F5 Power/遗物/球/宠物/角色资源 | 已完成 | F5a-F5e：根元数据、Power/全队/Hook、遗物触发/消耗、五角色资源、真实召唤所有权及第三方拒绝边界均有动态证据 |
| F6 选择与嵌套 continuation | 已完成 | F6a frame、F6b 回合开始/结束 Power/遗物与自动重复出牌、F6c 多 Actor 原序队列、F6d 前缀相对同名实例回放均通过 |
| F7 联合回合与敌方生命周期 | 已完成 | F7a/F7b 生命周期、F7c 全部既有特殊行动作用域、F7d 下一轮与选择恢复均通过 |
| F8 联合终局目标 | 已完成 | 终局边界、存活、战损向量、药水/保命资源、成长、偷窃、回合、动作及稳定动作序通过；单人排序未改 |
| F9 联合 Beam/BFWS | 已完成 | F9a Beam/转置、F9b 保路、F9c 固定 lane、F9d 有界 BFWS 与 2/4 Actor oracle 全部通过 |
| F10 strict replay 与差分 | 已完成 | 2/4 Actor 完整致死搜索路线从同根逐动作回放，完整联合状态 strict diff 与首差异诊断通过 |
| F11 性能与确定性 | 已完成 | 四 Actor 三动作固定预算：Beam/BFWS 有界完成，串行重复/4 lane 完全确定；记录实际工作量、耗时与分配 |
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
- F3d（已完成）：固定前缀在全员已到屏障且下一动作恰为下一轮时复用 F7 流水线，`runId=b85791b45b59410c9d3b1c8242a45ff4` Passed；随后 BFS/DFS/Beam/BFWS 的普通节点也共用 `ExpandBarrier`，只给两名 Actor 的 EndTurn 前缀即可自动完成玩家尾、额外回合或敌方侧、下一玩家侧及挂起选择分支，再枚举下一轮第三动作，最终 `runId=a7a3e70a570a47f682d48af93f195d09` Passed。联合搜索不另建 opening/cycle 语义副本；它们作为成员政策复用同一节点转移。生产单人候选与结果哨兵仍归最终 F12，不能用本阶段联合证据代替。

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
- F4c（已完成）：F4c1 完成 Actor+槽位指令、Disabled/Force、用药上下界和单人战略成本。F4c2 建立保留 Force 的无可选药基线，Beam/BFWS 在共享状态预算内按精确总用药数分层，并按动作 Actor 的可再生遗物、一次性奖励替换额度、Ambergris 自身最大生命阈值与冻结 Boss relief 裁决。基础阈值 `runId=12601915c77d43888b98927f30a5d817`、分层预算 `345dfe4be93c4a71828230133a695f81`、专门政策 `2c75864c21834e68b41cf1ab51dba777` 均 Passed；专门测试开发期失败 `5b8846ec5e2e4928b5d18dc011e3e229` 和 `41701aa9f20f484b86d0896fd85f5e69` 分别用于拆分诊断和修正 ActClear 预期。
- F4d（已完成）：Actor0 的药水动作作为第二轮固定前缀，经 F7 生命周期推进后仍保持 Actor/槽位身份、单次消耗、联合目标计数和完整状态键；BFS/DFS oracle 一致，`runId=6e70211973b3400fb77bea4b41fbd6fb` Passed。

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
- F5b（已完成）：通用 StrengthPower 已通过跨 Actor applier、远端 target、叠加/移除、Fork 隔离和状态键/续用戳；新增 One For All 精确镜像，由 Actor1 对四名 Actor 应用同额 Power 并保留 applier，`COOP-MULTI-ACTOR-ROOT` 运行 `69805442a5ae4e6599e0e84de008d8f2` Passed。Shuriken 的 AfterCardPlayed 时序代表由 F5c 同场动作链覆盖。
- F5c（已完成）：Actor1 连续三次攻击经权威联合 transition 触发自己的 Shuriken，计数、Strength 归属、状态键/续用戳与父 Fork 隔离通过，运行 `e0a5cdea18764e00ae656242cf1cfc46` Passed；Actor0/Actor1 各持同型 Throwing Axe 后，Actor1 出牌只消耗自己的实例，队友实例与父 Fork 保持未使用，运行 `31ea273edf31473a939b7075faceddbe` Passed。
- F5d（已完成）：混合 Ironclad/Defect/Regent/Necrobinder 根及 Orb/Stars/Osty 隔离已通过；独立 Ironclad/Silent/Necrobinder roster 中，Actor1 Blade Dance 生成的 Shiv 只进入自身手牌，Actor2 Afterlife 实际召唤的 Osty 只属于自身，`COOP-MULTI-ACTOR-ROOT` 运行 `e1f94caff84845e281c716f46d1e6499` Passed。
- F5e（已完成）：复用生产 `ValidateSubscriber` 入口，战斗外 inert subscriber 放行；未知来源且覆写战斗 hook 的 subscriber 明确抛 `PredictionUnsupportedException`，消息保留类型和 scope。`COOP-MULTI-ACTOR-ROOT` 运行 `7eb68184342747fb865bf839fbabc583` Passed。

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

执行拆分：

- F6a（已完成）：新增不可变 `JointPendingChoiceFrame`，明确保存 `OwnerActor`、完整 `SourceAction`、source/spec 与主/嵌套 placement；联合 expander 在生成分支前严格核对 frame owner/source action。Havoc → Second Wind → Defend 动态嵌套严格回放 `runId=78cfa14e6d98479fa14d2104166c5b95` Passed。
- F6b（已完成）：回合开始阶段把 owner、EndTurn SourceAction、source/context/timing 与 spec 封装为 `TurnStart` frame，并可从稳定跨回合父状态以选择前缀重新执行。Actor1 的 `ToolsOfTheTradePower` 修复 frame 身份后 `runId=2cf1ac4368ab4ad58ac2c056708b6842` Passed；Dark Embrace/Hellraiser 的自动出牌回合尾选择 `runId=9bc79f5cdc9548db8f287189f993d54f` Passed；Joss Paper 遗物来源及全队虚无计数修复后 `runId=7d629eea37754cdfb240a1d6580bf041` Passed。重复出牌代表首次暴露 `Decisions, Decisions` 在来源牌离手前静态枚举主选择，产生不可回放的来源牌 token，`runId=3ee53c2a957b4fdaaba7d64e9db8873a` Failed；改为所有主选择从权威出牌后 pending spec 展开后，Actor1 的 Prepared 三次重复选择链确定性消费，`runId=05ebefd640864595ae272ceae4ae8e37` Passed。
- F6c（已完成）：`JointChoiceContinuation` 为每个 Actor 最多保存一帧，并按插入原序及完整 SourceAction 消费；Actor0/1 的真实 Gambler's Brew 主选择帧验证同 Actor 重入和后置抢占稳定拒绝，`runId=ddf9af571fc24faba81fb18eccbd3805` Passed。
- F6d（已完成）：明确 `CardOccurrence`/`CardStateOccurrence` 是相对于每个动作当前前缀状态的实例地址；三张同 ID/同状态 Strike 每步重新枚举后 occurrence 均为 0，增量执行与从原根严格回放整条计划得到同状态键/续用文本，`runId=b05b87d8492e48a391e785f18c419b7f` Passed，关闭 F-ISSUE-006。单人 replay helper 未分叉，最终等价哨兵归 F12。

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

执行拆分：

- F7a（已完成）：`JointRoundTransition.CompletePlayerSide` 只接受已到达屏障的状态，在任何 PhaseOne 前冻结全体 Actor 的虚无牌总数，再按 Actor 固定顺序运行 PhaseOne/手牌清理，最后对全部存活参与者运行一次共享 PhaseTwo；单 Actor EndTurn 不提前改变 live phase，基础屏障 `runId=92d774c3b3f341c8a11472a246c717ec` Passed。共享 PhaseTwo 的选择按请求 owner 归属并消费 `PlayerTurnEnd` 前缀，Power 代表见 `runId=9bc79f5cdc9548db8f287189f993d54f`，Joss Paper 遗物代表见 `runId=7d629eea37754cdfb240a1d6580bf041`。
- F7b（已完成）：远端 Actor 强制死亡后不再产生候选、不阻塞屏障，玩家侧只处理存活参与者且死者保持死亡，`runId=672c2a82f07e41d2a73c5538e5f3fd1d` Passed。Parafright 的 owner-only 复活动作恢复满血并撤销已处理死亡标记；Fat Gremlin 逃跑后离开活动 roster，`runId=43e62fb1701d44fb9257fcbd4e5a3db9` Passed。额外回合按原版 `PlayersTakingExtraTurn` 子集推进：Actor1 Ambergris 路径只恢复 Actor1 行动并增加其 TurnNumber，不增加共享 RoundNumber、不执行敌方侧并消费来源，`runId=544bf8bc1d4248ff952622314b289b94` Passed。
- F7c（已完成）：F7c1 已按冻结敌方行动名单执行敌方侧开始、纯攻击行动、侧结束、毒与下一行动准备；Fuzzy Wurm Crawler 的纯攻击对两个 Actor 同序结算，`runId=3f9a5c67effa42c5b10791292be1309b` Passed。F7c2/F7c3 将全部既有 `MonsterMoveEffects.Supports` 语义归入 owner-only、target-only、attack 后 mixed 或 attack 前 mixed；未登记效果仍沿用既有显式 unsupported 边界。Rage、Gas Bomb、Living Fog 与 Tough Egg 分别覆盖攻击加一次性 owner、自移除、前置召唤和一次性 RNG。真正同时修改 target/owner 的 Soul Siphon 进一步验证两名 Actor 各 `-2 Strength/-2 Dexterity`、敌人只 `+2 Strength` 一次，`runId=60708c6fb8f147f280bf40fca07745de` Passed；Windows 结构门禁同步改为检查集中作用域注册和两段式效果入口。
- F7d（已完成）：F7d1 按原版多人顺序对全体存活 Actor 只触发一次共享 side hook，并逐 Actor 重置资源、抽牌、执行玩家开始 hook、球与自动阶段；两 Actor的轮数/能量/手牌和共享 round 推进通过，`runId=9ad70ea00a2d493ead5c090991c9d263` Passed。F7d2 已让 Actor1 的 `ToolsOfTheTradePower` 在回合开始挂起后从稳定父状态按前缀恢复，`runId=2cf1ac4368ab4ad58ac2c056708b6842` Passed；EndTurn Power、遗物及重复自动出牌来源已由 F6b 代表覆盖。F7 至此关闭。

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

完成证据：

- `JointObjectiveScore` 保持在 `Search/Coop` 独立职责内，不引用或修改单人 `FinalPlanOrdering`；稳定联合动作序仍由 `JointOfflineSearch.CompareActions` 作最后决胜。
- 未完成边界明确排在胜利之后、失败之前；全队存活先于总战损，避免总 HP 掩盖单个 Actor 死亡。
- 战损总和、逐 Actor 向量、药水战略成本/次数、保命资源、成长、长期资源、偷窃回收、回合和动作各有双向比较哨兵；`COOP-JOINT-OBJECTIVE` 的 `runId=0661a8fe6b954efe947351377d65f4ae` Passed，实例已删除。
- “总战损”继续作为 F-ISSUE-001 workaround，不在本阶段假定不同角色 HP 等价。

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

执行拆分：

- F9a（已完成）：新增逐层确定性 `SolveBeam`，所有 Actor 共享状态预算与转置集合，终局即时排空；未命中终局而耗尽预算时返回明确的未完成评分和 `StateBudget` 停止原因。2 Actor 小根使用覆盖完整层宽的 Beam，与独立 DFS oracle 的分数、动作序及状态键一致，`runId=2ba973750d1a4b41998970ba7058a880` Passed。
- F9b（已完成）：`JointBeamRetentionPolicy` 先保留最佳总路线，再按末动作 Actor、用药/不用药通道与死亡/战损/药水成本/保命/成长/长期资源/偷窃的非支配前沿保留代表，最后按稳定总序填宽；不交换 Actor 动作。独立合成合同同时要求四类代表，`COOP-JOINT-OBJECTIVE` 的 `runId=2aeaebfe461444ce9deb69fffc2e5a62` Passed。
- F9c（已完成）：主线程按 frontier 原序完成预算、状态键、转置与终局准入；已准入父节点按 `parentIndex % laneCount` 固定分派，lane 只写独占索引槽，`WhenAll` 排空后主线程才按父序提交，异常不会产生部分层提交。同预算串行/4 lane 的分数、动作、状态键与展开数一致，预取消抛 `OperationCanceledException`。状态预算为 1 时曾因新层入口丢弃 frontier 而失败，`runId=d61d81fe8dc040b8a8adb9ae1e37f7d5`；修复后边界组 `runId=7b524dd3b0a44b7aa90c40496f8184ef` Passed，固定 lane `runId=a8904ec11db34acfa585b1ee64db640a` Passed。
- F9d（已完成）：复用生产 `BfwsBoundedOpen`，以各 Actor 的阶段、HP/格挡、能量、Stars、牌堆规模和回合桶作为有限新颖性事实；OPEN、状态数和动作数均有硬上限，最终选择仍使用 F8 字典序。2 Actor 两动作与四 Actor 一动作小根均和 DFS oracle 的分数、动作序、状态键一致，`runId=b662c18823b44f938edcbae72f197ab2` Passed。四 Actor 更深固定预算工作量归 F11，不扩大 oracle 掩盖语义问题。

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

完成证据：

- `JointPlanReplayer` 现在从同一冻结根执行动作，并在跨轮时完整运行玩家尾、敌方侧和下一玩家侧；每个成功动作保留严格快照，失败立即抛出且不返回部分结果。
- `JointStrictReplayVerifier` 将搜索终态与独立重放终态按联合 Turn/Phase、逐 Actor 投影、完整 `ContinuationStamp` 和状态键比较，错误包含动作索引、动作与首个字段差异。
- 2 Actor 与 4 Actor 的 1 HP 敌人完整致死搜索均由 Beam 找到一步终局并从根严格回放；故意改变 Actor0 Block 的诊断稳定定位 `actor[0]`。最终 `COOP-MULTI-ACTOR-ROOT` `runId=bb8330ee794148e2b59319b970fc65a9` Passed，实例已删除。
- 首次新增夹具 `runId=6d28c4399b714e368778ae67ee370be9` 暴露 Soul Siphon owner 收尾重复执行代表 Actor target 段；修正为 `target=false/owner=true`。第二次 `runId=6c73ac0f888144b5b669345718f2ca4b` 暴露 `JointTurnState` 数组的引用相等假差异；改为逐 Phase 比较。两次实例均已删除。
- 本阶段的 “actual” 是离线搜索权威 transition 的增量终态，不声称真实四客户端原生执行；真实客户端控制仍属于 F12 后的网络 Runtime 非目标。

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

完成证据：

- 四 Actor、三动作、共享状态预算 256 的固定根上，Beam 展开 97 个状态并 `Completed`，BFWS 展开 123 个状态并 `Completed`，均未超过请求硬上限。
- 同一 Beam 串行重复及 4 lane 运行的分数、逐 Actor 战损向量、完整动作序、状态键、展开数和停止原因逐项一致；既有预取消及 lane 排空合同继续由 F9c 覆盖。
- 首次串行 Beam 实测 818 ms，协调线程 `GC.GetAllocatedBytesForCurrentThread` 差值 231,792,720 bytes。该数值包含测试进程内该调用的累计分配，只作为固定夹具工作量基线，不外推峰值驻留、FPS 或可见 Steam 性能。
- `COOP-MULTI-ACTOR-ROOT` `runId=3ba63f51cc0c4fb7a35a1da4b7e690fb` Passed，实例已删除；请求仍使用既有有界 Beam frontier、BFWS OPEN、共享状态预算和取消令牌，没有新增无界队列或扩大预算。

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
