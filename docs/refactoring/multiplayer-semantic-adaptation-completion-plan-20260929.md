# 原版多人战斗语义适配补齐计划

> 状态：执行中；M0 已完成，下一阶段为 M1 原生多人差分底座<br>
> 日期：2026-09-29<br>
> 规划基线：`bae3f5ea`<br>
> 原版语义基线：当前游戏 `v0.111.0` 的只读反编译 Core 源码；wiki 只用于发现内容，不作为最终结算依据<br>
> 前置成果：[从单人到离线四 Actor 完整迁移计划](single-to-offline-four-actor-complete-migration-plan-20260929.md)已完成 F0-F12<br>
> 当前目标函数：暂按所有 Actor 战损之和排序；这是 workaround，保留逐 Actor 战损向量，不在本计划内假定不同角色的 HP 等价

## 0. 计划目的

F 阶段已经完成的是：把 CombatSolver 当前支持的**单人战斗语义**放入 Actor 数量可变的离线联合根、联合动作、联合回合、联合搜索和严格回放框架，并保持生产单人路径不变。

本计划补齐的是原版多人相对单人的**增量语义**：

1. 原版 37 张多人专属卡牌及其 Power、Hook、选择、复制、转移、宠物和球语义；
2. 敌人生命、格挡、Power 数量和行动效果随玩家人数变化的规则；
3. 来源 Actor、决策 Actor、受益 Actor、卡牌 Owner、Power Applier/Target、宠物 Owner 等跨玩家身份；
4. 多人选择、结束回合、死亡、额外回合、药水、遗物、生成池和历史的真实规则；
5. 使用原版多人状态执行得到的 actual/simulated 严格差分，而不只比较模拟器和自身回放；
6. 在语义正确后，证明联合 Beam/BFWS 能在固定预算内得到可回放的 best-found 路线；
7. 最后为未来独立 Co-op Bot Mod 定义真实客户端控制、行动确认和承诺性修正边界。

这不是对 F 阶段的重做。F 阶段提供容器和搜索骨架，本计划增加原版多人内容与原生多人真值验证。

## 1. 当前事实与不能宣称的结论

### 1.1 已经具备

- `CombatRootSnapshot` 可保存 1-4 个固定顺序的 `CombatActorRoot`。
- `PlanAction`、`PlanCardChoice`、`ContinuationStamp`、状态键和联合检查点均带 Actor 身份。
- `JointActionTransition` 是联合 BFS、DFS oracle、Beam/BFWS 和严格回放的共同单步入口。
- `JointTurnState` 已建模独立结束、死亡、全员屏障、额外回合 Actor 子集和共享轮次。
- 卡牌、药水、选择、Power、遗物、球、宠物、敌方生命周期和 1-4 Actor 搜索已有联合基础设施。
- 所有当前 `MonsterMoveEffects.Supports` 行动已有联合 owner/target/pre/post 作用域分类。
- 生产 `CombatBeamSolver` 继续稳定拒绝 `ActorCount > 1`，生产 Runtime、UI 和部署仍是单人。

### 1.2 尚未证明

- F 阶段 strict replay 比较的是“搜索轨迹”和“从同一模拟根重新回放”；它证明内部一致性，不证明原版多人结算正确。
- 当前没有覆盖全部 37 张多人专属牌的普通版、升级版和跨玩家组合。
- 当前多人卡牌支持中同时存在专用实现、通用推断、局部 Hook 支持和缺失 OnPlay 入口，不能按“类型被引用过”判定完成。
- 原版 `PowerCmd` 的敌人 Power 多人缩放尚未形成完整、可枚举、可验证的统一模拟入口。
- 原生多人根的完整捕获、原版动作执行和逐字段 actual/simulated 差分尚未成为无人测试能力。
- 当前搜索是有界 best-found 搜索；除有限 toy fixture 外，不能声称全局最优。
- 当前没有网络协议、远端客户端执行器、行动确认或漂移后重规划，不能声称 Bot 已能控制真实四人联机。

## 2. 完成定义

只有同时满足下列条件，才可以声称“原版多人离线适配已补齐”：

1. 当前版本全部 37 张多人专属卡牌，普通版和升级版均有显式支持结论，不能依赖未经证明的宽泛推断。
2. 每张牌从稳定原生多人根执行一次原版动作，再从同根执行模拟动作，完整状态在首个检查点逐字段一致。
3. 所有带状态的多人 Power/卡牌模型都完成根捕获、Fork、引用重映射、状态键、`ContinuationStamp` 和 actual/simulated 观察。
4. 2、3、4 Actor 的敌人 HP、格挡、Power 数量、行动广播、目标顺序、死亡和胜负边界均与当前原版源码一致。
5. 跨玩家选择、卡牌移动、卡牌复制、自动/重复出牌、药水目标、球、Osty 和历史归属均使用明确身份，不以动作 Actor 猜测决策者或受益者。
6. 已核对的单人自用效果不会因加入多个 Actor 而错误广播给队友；wiki 提示只能作为盘点线索，最终以当前原版源码与原生差分为准。
7. 未支持的原版或第三方多人语义在根捕获或动作执行处稳定拒绝，消息包含类型、Actor、阶段和来源；不得默认成功、跳过候选或伪造相等。
8. 联合 BFS/DFS oracle、Beam/BFWS 和根级严格回放继续共用 `JointActionTransition`，没有为 37 张牌建立第二套搜索结算。
9. ActorCount=1 的单人生产边界、候选序、终局政策和代表性 actual/simulated fixture 不退化。
10. 搜索完成结论只表述为“固定预算内 best-found”；只有有限状态空间 oracle 可以表述为最优。

“真实 Co-op Bot 已可部署”是更晚的完成定义：还必须完成第 M12 阶段的多客户端协议、行动确认、目标失效、漂移处理和实机联机验收。离线语义完成不能替代它。

## 3. 真值来源与版本纪律

语义证据按以下优先级使用：

1. 当前游戏版本的反编译原版实现，包括卡牌 `OnPlay`、Power Hook、`PowerCmd`、回合命令、目标枚举和多人缩放；
2. 同一版本原生多人 `CombatState` 的实际执行结果；
3. 游戏内本地化、牌面说明和结构化运行日志；
4. [Slay the Spire 2 Multiplayer wiki](https://slaythespire.wiki.gg/wiki/Slay_the_Spire_2:Multiplayer)，只用于发现卡牌、规则和版本变更线索。

实施开始时必须记录游戏版本、程序集 MVID 或等价构建身份。游戏升级后：

- 先重生成多人卡牌、Power 缩放覆写、目标规则和相关 Hook 清单；
- 将旧证据标为旧版本，不直接沿用“已支持”结论；
- 只重跑受源码变化影响的最小 fixture；
- 不把 wiki 文字覆盖到与当前原版源码冲突的行为上。

## 4. 状态与身份所有权

### 4.1 每项状态必须回答的问题

每个新增多人状态都必须在实现提交中记录：

1. 主线程从哪个原生对象、在哪个稳定边界捕获；
2. 属于根快照、`SimulatedCombatState`、克隆 Model 还是 `PredictionStateStore`；
3. Fork 使用深拷贝、COW 还是不可变共享；
4. Actor、Creature、Card、Power、Orb、Osty 引用如何经同一个 `PredictionForkContext` 重映射；
5. 是否影响合法动作或结算，是否进入状态键；
6. 是否跨回合存在，是否进入 `ContinuationStamp`；
7. actual/simulated 严格差分从哪里观察；
8. 创建、叠加、减少、移除、清空、死亡和换 Owner 的准确时点；
9. Fork 前允许存在什么事务或 continuation。

### 4.2 不得再混用的身份

多人动作合同要显式区分：

| 身份 | 含义 | 典型风险 |
|---|---|---|
| `SourceActor` | 发起卡牌/药水/Hook 的 Actor | 被错误当作所有后续选择的主人 |
| `DecisionActor` | 实际需要作出选择的 Actor | `Tutor`、队友选择、跨玩家药水选择 |
| `RecipientActor` | 获得牌、格挡、资源或 Power 的 Actor | 把 Owner 收益错误给目标或反之 |
| `CardOwner` | 卡牌当前所属牌堆的 Actor | `TheBall`、复制和转移后仍沿用旧 Owner |
| `CreatorActor` | 创建卡牌/球/宠物的 Actor | 生成历史与触发归因错误 |
| `Applier` / `Target` | Power 的施加者与承受者 | `Knockdown`、全队 Power、死亡清扫 |
| `PetOwner` | Osty 等实体所属玩家 | 远端 Necrobinder 触发错误 |
| `ActionExecutor` | 未来真实客户端中负责提交动作的一方 | 网络确认和重规划，不属于离线结算身份 |

现有 `OwnerActor` 字段不能在语义不同时承担以上全部角色。新增字段只在影响合法动作、未来结算或回放身份时进入计划/状态；纯诊断身份不进入状态键。

## 5. 37 张多人专属牌初始覆盖清单

下表是规划基线，不是完成声明。状态只描述当前静态入口：

- **专用入口**：已有明确 OnPlay mirror，但仍缺原生多人差分；
- **通用候选**：现有通用攻击/格挡/抽牌/Power 路径可能覆盖，必须逐张证明；
- **已知部分**：已有部分 spec、Hook 或结果位置，但关键语义已知不完整或高风险；
- **缺 OnPlay**：下游 Hook 可能存在，但没有证明卡牌能正确建立状态。

| 颜色 | Card model | 规划基线 | 主要补齐点 | 主阶段 |
|---|---|---|---|---|
| 无色 | `BelieveInYou` | 缺 OnPlay | 队友目标、资源/牌堆收益、选择身份 | M4 |
| 无色 | `Coordinate` | 缺 OnPlay | 临时力量的来源、目标和回合清除 | M5 |
| 无色 | `GangUp` | 通用候选 | 动态伤害读取全队状态、目标和升级 | M4 |
| 无色 | `HuddleUp` | 专用入口 | 选择不得阻塞其他玩家、选择 Owner 与空选择 | M6 |
| 无色 | `Intercept` | 已知部分 | Owner 获得格挡、Covering 列表、伤害重定向与死亡 | M5 |
| 无色 | `Lift` | 通用候选 | AnyAlly 排除自己、格挡归属和升级 | M4 |
| 无色 | `TagTeam` | 通用候选 | 目标 Power、额外出牌次数消费和 Hook 顺序 | M5 |
| 无色 | `TheBall` | 已知部分 | 卡牌移交、Owner/Creator、每次出牌成长和 Hook 归因 | M6 |
| 无色 | `BeaconOfHope` | 缺 OnPlay | 全队 Power 建立、格挡 Hook、防重入状态 | M5 |
| 无色 | `Knockdown` | 已知部分 | Applier、攻击倍率、敌方侧结束时准确移除 | M5 |
| 无色 | `Mimic` | 通用候选 | 目标格挡动态伤害、目标死亡/变化 | M4 |
| 无色 | `Rally` | 通用候选 | AllAllies 范围、死亡玩家排除和顺序 | M4 |
| 铁甲战士 | `Blaze` | 缺 OnPlay | 跨玩家资源/攻击结算与来源历史 | M4 |
| 铁甲战士 | `DemonicShield` | 已知部分 | 目标得格挡、Owner 失去生命、顺序与致死边界 | M4 |
| 铁甲战士 | `Outrage` | 已知部分 | 攻击后向队友创建副本、Owner/升级/附魔 | M6 |
| 铁甲战士 | `Midnight` | 通用候选 | 全局消耗监听、动态值和卡牌实例变异 | M5 |
| 铁甲战士 | `Tank` | 缺 OnPlay | Power 建立、队友受击触发和生命周期 | M5 |
| 盗贼 | `BladeSymphony` | 缺 OnPlay | 队友牌堆/手牌交互、生成牌 Owner | M6 |
| 盗贼 | `Concoct` | 缺 OnPlay | Power 建立、伤害 Hook、目标与回合清除 | M5 |
| 盗贼 | `Fade` | 缺 OnPlay | 临时敏捷目标和准确清除 | M5 |
| 盗贼 | `Flanking` | 缺 OnPlay | Applier/Target、多人攻击 Hook 和移除 | M5 |
| 盗贼 | `Sneaky` | 缺 OnPlay | 队友出牌 Hook、费用/复制归属和消耗 | M5 |
| 储君 | `Constellation` | 专用入口 | 目标抽牌、跨玩家选择/历史和顺序 | M4 |
| 储君 | `Largesse` | 专用入口 | 为目标生成牌、卡池/Owner/RNG | M6 |
| 储君 | `Plot` | 缺 OnPlay | 目标牌堆变更、选择 Actor、跨回合身份 | M6 |
| 储君 | `HammerTime` | 缺 OnPlay | Power 建立、后续伤害/出牌 Hook 和来源 | M5 |
| 储君 | `Tutor` | 缺 OnPlay | 由队友决策、来源牌堆、同名实例和取消 | M6 |
| 死灵契约师 | `LegionOfBone` | 缺 OnPlay | 多 Osty/宠物 Owner、召唤和死亡 | M7 |
| 死灵契约师 | `Soulbound` | 缺 OnPlay | 生成牌 Hook、防重入状态和 Owner | M5 |
| 死灵契约师 | `Underworld` | 缺 OnPlay | 伤害 Hook、来源为空的原版合同、回合尾清除 | M5 |
| 死灵契约师 | `Cacophony` | 缺 OnPlay | 抽牌计数、重入、跨 Actor 抽牌归属 | M5 |
| 死灵契约师 | `GlimpseBeyond` | 缺 OnPlay | 队友牌堆/消耗/抽牌与选择身份 | M6 |
| 故障机器人 | `EnergySurge` | 缺 OnPlay | 全队能量分配、死亡玩家和额外回合 | M4 |
| 故障机器人 | `Hibernate` | 缺 OnPlay | Power 建立、霜球、死亡玩家排除和回合开始 | M7 |
| 故障机器人 | `Ignition` | 专用入口 | 目标球区、球 Owner、槽位与触发顺序 | M7 |
| 故障机器人 | `ImitationLearning` | 缺 OnPlay | `TargetPlayer`、延迟克隆列表、出牌前后 Hook | M6 |
| 故障机器人 | `OneForAll` | 专用入口 | 全队 Power 数量、Applier/Target 与 Hook 顺序 | M4 |

初始分类统计：5 张专用入口、6 张通用候选、5 张已知部分、21 张缺 OnPlay。任何类别都必须经过 M1 的原生多人差分才能改成“完成”。

## 6. 多人通用规则缺口

### 6.1 玩家人数缩放

- 敌人最大生命和当前生命：普通生成、召唤、复活、阶段转换和测试建局使用同一原版缩放入口。
- 敌人格挡：复用已完成的 `MultiplayerScalingModel`，继续验证普通、powered、主敌人、次要敌人、Act 和 Boss。
- Power 数量：在 Power 应用的唯一权威入口镜像原版 `ShouldScaleInMultiplayer` / `GetScaledAmountForMultiplayer`。
- 覆写清单必须从当前原版类型生成，不能手写成永久列表。当前源码至少包含 `ArtifactPower`、`CurlUpPower`、`FlutterPower`、`SlipperyPower`、`SkittishPower`、`ShriekPower`、`HardenedShellPower`、`RegenPower`、`ReattachPower`、`RampartPower`、`PlowPower`、`PlatingPower`，以及通过基类或其他覆写参与缩放的类型；实施时以工具输出为准。
- Artifact、Buffer、负数、零、封顶、叠加和第一次应用时序分别做最小差分，不用一个聚合 HP 结果代替。

### 6.2 敌人行动广播

- 一次性 owner/global 段只执行一次；逐玩家攻击、减益和状态段按原版稳定玩家顺序执行。
- mixed move 必须显式分成 pre-target、target、post-target，禁止对每个 Actor 重放完整 move。
- 目标死亡、逃跑、复活、召唤和行动尾仍可读取的静态 AI 状态必须保留。
- 每次游戏版本变化重新对照全部 `MonsterMoveEffects.Supports` 类型；新行动没有分类时显式拒绝。

### 6.3 自用效果和全队效果

- 对 wiki 或牌面声称“只影响自身”的既有单人牌建立反广播哨兵，当前至少盘点 `Cruelty`、`Accuracy`、`Tracking`、`ShadowStep`、`Lethality`、`ReaperForm`、`Claw`、`Maul`。
- 全队 Power、格挡、能量、抽牌、生成牌必须定义是否包含来源 Actor、死亡 Actor 和结束回合 Actor。
- “所有队友”与“所有玩家”不可合并；AnyAlly 必须排除自己，AllAllies 是否包含自己以原版实现为准。

### 6.4 生成池和 MultiplayerConstraint

- 根捕获卡池时保存 `CardMultiplayerConstraint`，多人专属牌只能在多人合法入口出现，单人限定牌不得进入多人生成结果。
- 生成、复制现有牌、固定衍生牌和随机变牌分别核对 Owner、Creator、升级、附魔、临时标志和 RNG。
- 多人专属牌不得因为 `CardOnPlayInferrer` 能推断攻击/格挡/抽牌而自动宣称支持；只有经过逐卡验证的类型可以进入显式登记表。

### 6.5 药水、遗物和跨玩家 Hook

- `AnyPlayer` 药水目标枚举已有基础，但选择 Owner、收益归属、药水 Owner、死亡目标过滤和原版取消行为必须差分。
- 可对队友使用的增益药水与只能对自己使用的药水逐项建立合法目标目录。
- 同型遗物由各 Actor 独立拥有；计数、消耗、回合触发和死亡后行为不得读取本地玩家实例。
- 第三方 gameplay subscriber 和多人专属登记项没有精确适配时继续拒绝。

### 6.6 回合、选择、死亡和战后复活

- 手动 EndTurn 在没有不可逆结算前可以撤销；强制 EndTurn 不可撤销。离线搜索可以规范化可撤销状态，但必须记录规范化依据。
- 一个 Actor 的选择不得无条件阻塞其他 Actor；需要先从原生多人命令时序证明哪些选择可并存、哪些由全局命令队列串行。
- 死亡 Actor 不能继续成为非法目标或回合参与者；队伍只有全员死亡才战败。
- “队友存活时，死亡玩家下一层以 1 HP 复活”属于战斗外 Run/地图连续性，不塞入战斗求解器；未来跨房间规划另建模块。

## 7. 分阶段实施

### M0：冻结版本化多人支持目录和失败边界

目标：建立一份不会被“类型有引用”误导的单一库存。

工作：

- 由当前原版程序集生成 37 张多人牌、相关 Power/Hook、所有 Power 缩放覆写、多人目标药水和多人规则入口。
- 新建机器可读 `MultiplayerSemanticCatalog` 或等价描述器，记录 `Unsupported`、`UnderTest`、`Verified`，以及普通版/升级版证据。
- CoverageCatalog 增加 `MultiplayerOnly` 卡牌门禁：未经显式验证的类型不能落入宽泛 OnPlay 推断。
- 为每项内容绑定权威原版方法、模拟入口、fixture ID 和状态所有权记录。
- 当前文档的 37 张表成为目录来源之一，实施后由机器可读清单生成或核对，避免双重手工状态漂移。

验收：

- 当前程序集中的多人牌、Power 缩放覆写和多人目标入口与目录一一对应；
- 任意新增但未登记的多人牌在根捕获或动作执行处稳定失败；
- L0 CoverageCatalog 与 Windows/Linux 结构门禁通过。

提交边界：目录/门禁一提交，测试协议与首个失败 fixture 一提交。

### M1：建立原生多人 actual/simulated 差分底座

目标：把原版多人执行结果变成语义真值，而不是继续用模拟器自证。

工作：

1. 在无人测试 `ScenarioBuilder` 中构造 2、3、4 玩家原生 `CombatState`，保存稳定 Actor 身份、角色、牌堆、遗物、药水、球、Osty、敌人和 RNG。
2. 在主线程稳定边界同时捕获原生根和预测根。
3. actual 侧通过原版公开命令执行一张牌、一次选择、一次药水或一次 EndTurn；simulated 侧从同根通过 `JointActionTransition` 执行同一动作。
4. 捕获每个 Actor、敌人、牌堆、Power 内部状态、球、宠物、药水、历史、RNG、挂起选择和回合阶段。
5. 输出首个字段差异以及完整差异集合；actual 失败与 simulated 失败分别报告，不把两边同时异常视为相等。

测试能力要求：

- 同一原生根可建立一个 actual 分支和至少两个独立 prediction Fork，证明根不被修改；
- 支持普通版/升级版、指定 Actor、指定目标、指定选择和跨回合停止点；
- 首个基线至少选择 `BelieveInYou`、`Knockdown`、`TheBall` 各暴露一种缺口；
- 原生多人建局不要求四个真实客户端。网络输入顺序不是本阶段的结论。

验收：

- 一个已知简单多人动作通过完整差分；
- 三个已知缺口能稳定失败到具体字段，而不是超时或泛化异常；
- fixture 总超时遵循 120 秒内环，状态未变化时不扩大超时等待。

### M2：显式建模跨玩家身份和私有状态

目标：先修正共用底层身份，再逐卡实现，避免 37 张牌各自发明 Owner 规则。

工作：

- 扩展动作/选择帧区分 `SourceActor`、`DecisionActor`、`RecipientActor`，只在语义不同的入口增加字段。
- 统一卡牌 Owner 转移、Creator 历史、Power Applier/Target、PetOwner 和目标 Creature 的引用重映射。
- 为 `InterceptPower.Covering`、`ImitationLearningPower.TargetPlayer`、待复制卡列表、Beacon 防重入、Soulbound 防重入、Cacophony 计数等私有状态选择唯一所有者。
- 所有跨回合状态接入状态键与 `ContinuationStamp`；纯瞬时事务只进入 continuation frame。
- 根模型、Fork 和 actual diff 使用同一个语义字段描述器，禁止测试另维护隐藏字段清单。

验收：

- 任意一个 Actor/Creature/Card 引用在父、两个兄弟 Fork 和根级回放中映射到正确分支实例；
- 变更 `DecisionActor`、CardOwner、Applier 或 Target 会在必要位置改变计划身份或状态键；
- ActorCount=1 的动作序列与文本格式保持兼容。

### M3：完成玩家人数缩放和生成规则

目标：在逐卡内容前关闭所有全局人数规则。

工作：

- 镜像敌人 HP 的出生、召唤、复活和阶段转换缩放。
- 把 Power 多人缩放放入唯一 Power 应用入口，传递 player count、Encounter、Act、Boss、applier、target 和 cardSource。
- 保持玩家目标不走敌人缩放；主敌人和次要敌人按原版分支处理。
- 对 Card/Power/药水生成池应用当前 `CardMultiplayerConstraint`。
- 生成当前版本的缩放类型覆盖表，新增覆写未验证时门禁失败。

最低证据：

- 2/3/4 玩家、普通/精英/Boss 各一个 HP 根；
- 主/次敌人格挡代表；
- Artifact、Plating、Skittish/Slippery、CurlUp/Flutter 及一个零/负数边界；
- 多人专属牌只在多人池出现，单人限定内容不误入多人池。

### M4：直接结算型多人牌

目标：完成不依赖长期私有 Hook 状态的伤害、格挡、抽牌、能量和直接全队 Power。

首批范围：

- `BelieveInYou`、`GangUp`、`Lift`、`Mimic`、`Rally`；
- `Blaze`、`DemonicShield`；
- `Constellation`；
- `EnergySurge`、`OneForAll`。

工作：

- 每张牌使用精确 mirror 或经过逐卡验证的显式 spec，不扩大通用推断白名单。
- 明确自己/队友/所有玩家/死亡玩家范围及稳定结算顺序。
- 对同一效果的来源、受益者、失血、得格挡、抽牌、能量和 Power 分别记账。
- 需要后续状态的牌只在本阶段建立入口，生命周期转到 M5 验收。

验收：

- 每张普通版和升级版至少一条 2 Actor actual/simulated 差分；
- 全队/队友牌增加一条 4 Actor，包含死亡 Actor 或已结束 Actor 边界；
- `DemonicShield` 单独验证“目标得格挡、Owner 失血”的顺序和致死边界。

### M5：跨玩家 Power 与 Hook 生命周期

目标：补齐由后续出牌、伤害、格挡、抽牌、回合边界触发的多人效果。

范围：

- `Coordinate`、`Intercept`、`TagTeam`、`BeaconOfHope`、`Knockdown`；
- `Midnight`、`Tank`；
- `Concoct`、`Fade`、`Flanking`、`Sneaky`；
- `HammerTime`；
- `Soulbound`、`Underworld`、`Cacophony`。

工作：

- 为每个 Power 对照原版创建、叠加、触发、消费、减少、移除和死亡清扫时点。
- 验证监听者顺序以及“谁出牌/谁受击/谁得格挡/谁抽牌”触发。
- `Intercept` 单独验证 Covering 列表、伤害重定向、覆盖者死亡和目标死亡。
- `Knockdown` 单独验证多个 Applier、攻击倍率及敌方侧结束移除，而不是普通玩家回合尾移除。
- 防重入状态进入 `PredictionStateStore`，Fork 后兄弟分支互不污染。

验收：

- 每个 Power 至少跨过一次实际触发点；跨回合 Power 使用 L2 fixture；
- 至少一项在父/兄弟 Fork 上证明内部计数和引用隔离；
- Power 已存在、再次叠加、移除后重新获得分别有代表性边界。

### M6：跨玩家选牌、转移、复制和自动出牌

目标：处理 SourceActor 与 DecisionActor/CardOwner 不同的复杂动作。

范围：

- `HuddleUp`、`TheBall`；
- `Outrage`；
- `BladeSymphony`；
- `Largesse`、`Plot`、`Tutor`；
- `GlimpseBeyond`；
- `ImitationLearning`。

工作：

- 原生多人命令观察器记录选择由谁看见、谁提交、选择期间其他 Actor 是否仍可行动。
- `JointPendingChoiceFrame` 保存 DecisionActor、来源动作、候选 Owner、placement 和原序。
- 卡牌移动后重新绑定 CardOwner；复制牌保存原版规定的升级、附魔、费用、临时标志和来源历史。
- `TheBall` 的结果位置、每次出牌成长、监听 Hook 和跨玩家移交使用同一张预测实例，不推进 live RNG。
- `ImitationLearning` 的目标玩家和待复制卡列表在 Before/AfterCardPlayed 间跨 Hook 保存并可 Fork。
- 同时出现两个 Actor 的选择时，以原生命令队列为准决定可并存还是稳定串行，不凭 UI 表象推断。

验收：

- 同名牌多实例、牌移交后再打出、复制后升级/附魔、选择取消/空选择分别有最小差分；
- 选择未完成的探针状态不进入 frontier；兄弟选择不共享可变 continuation；
- Huddle 选择不会错误冻结其他可行动 Actor。

### M7：球、宠物和角色专属多人资源

目标：完成不能只用通用卡牌/Power 表达的角色机制。

范围：

- `LegionOfBone`；
- `Hibernate`、`Ignition`；
- M4-M6 中涉及 Stars、Shiv、Osty、Orb 或角色资源的牌。

工作：

- Osty 的创建、多个宠物状态、Owner、死亡、伤害和回合 Hook 进入 Actor 私有状态。
- 球目标、球槽、球顺序、唤回/被动触发和死亡玩家排除按目标 Actor 结算。
- Hibernate 在回合开始、死亡玩家、无空槽和多个 Defect 时分别验证。
- 角色资源的全队给予与“给予指定队友”分开；不得落入 `LocalContext.GetMe()`。

验收：

- 至少一条 4 Actor 混合角色根同时包含 Orb、Osty、Stars 和普通手牌，动作后只有正确 Actor 状态变化；
- 相关状态经 Fork、状态键、续用戳和下一回合触发一致。

### M8：药水、遗物、既有单人牌反广播和第三方边界

目标：补齐不是 37 张牌本身、但会使多人实际战斗偏离的外围机制。

工作：

- 逐项目录化可对队友使用的药水、选择型药水和只能自用药水。
- 验证药水 Owner 与目标不同、目标死亡、选择取消、药水消耗、生成牌 Owner 和跨回合 continuation。
- 对同型遗物多 Owner 的计数、消耗、回合触发、战后资源和药水成本做隔离代表。
- 为既有自用牌建立反广播哨兵；如当前原版列表变化，以生成目录为准。
- 第三方多人 card/power/hook 没有完整适配器时明确拒绝，并同步 `THIRD_PARTY_ADAPTERS.md`。

验收：

- 2/4 Actor 各一组药水目标合法性与实际结算；
- 同型遗物两 Actor 只修改真实 Owner；
- 自用牌在 4 Actor 根中只影响自身；
- 未登记第三方 gameplay subscriber 的类型和 scope 出现在稳定失败消息中。

### M9：多人回合、敌人行动、死亡和选择并发

目标：从单动作正确推广到完整玩家侧、敌方侧和下一玩家侧。

工作：

- 对照原生命令队列确定可撤销 EndTurn、强制 EndTurn、挂起选择和其他 Actor 行动之间的顺序。
- 验证全员屏障前后玩家尾 Hook、额外回合 Actor 子集、敌方侧、下一玩家侧和 Round/TurnNumber。
- 对全部当前支持敌人行动重新跑 2/4 Actor 作用域目录；包含攻击所有玩家、给所有玩家减益、一次性 owner 效果和 mixed move。
- 验证一个/多个玩家死亡、全员死亡、目标在动作中死亡、敌人召唤/复活/逃跑和胜负终止。
- 战后 1 HP 复活只记录为未来跨房间层接口，不进入当前战斗状态。

验收：

- 2/4 Actor 完整跨过至少一个敌方侧并回到下一玩家可行动边界，actual/simulated 全字段一致；
- 每种 enemy move scope 至少一项代表，完整支持目录无未分类当前原版行动；
- 可撤销 EndTurn 的离线规范化不改变任何已执行不可逆 Hook。

### M10：37 张牌和通用规则的完整覆盖门禁

目标：把分散 fixture 收束成可重复的版本化验收，而不是再做新语义。

门禁矩阵：

- 37 张多人牌 × 普通/升级 × 2 Actor 最小 actual/simulated；
- 所有全队、目标队友、卡牌转移、球/宠物和多人选择类型增加 4 Actor 代表；
- 3 Actor 对根缩放、稳定顺序、胜负边界和至少一项全队牌做独立哨兵；
- 所有有状态效果增加 Fork；所有跨回合效果增加 L2；
- Power 缩放覆写目录、敌人 move scope 目录、多人药水目录、反广播目录全部为零未知项；
- 失败报告必须给出首个动作/屏障、Actor、字段、actual 和 simulated 值。

完成产物：

- 机器可读 coverage 证据；
- 本文进度表和卡牌表更新；
- `docs/TEST_MATRIX.md`、`coverage/test-evidence.json` 和 `docs/DEVELOPMENT_NOTES.md` 同步；
- 游戏版本与程序集身份写入证据。

### M11：联合搜索与质量验收

目标：在语义门禁完成后，证明搜索正确消费新增多人状态。

工作：

- 审计新增状态是否进入必要的状态键、转置标签、Pareto 和 continuation；不把纯启发式加入战斗等价性。
- 为直接结算、状态 Power、卡牌转移、选择、球/宠物、药水和跨回合各建一个有限 oracle fixture。
- BFS 与不去重 DFS 给出小空间真最优；Beam/BFWS 在同预算内不得选择不可回放或语义错误路线。
- 固定 DOP1/DOP4 下动作序、终态键、停止原因和展开计数确定。
- 保留逐 Actor HP 向量和总战损 workaround；权重、角色 HP 价值和承诺成本另立研究，不偷偷改终局语义。

验收：

- 2 Actor 两步以上及 4 Actor 一轮以内的小空间 oracle 一致；
- 至少一条包含多人专属牌的计划跨过敌方侧并从原根严格回放；
- 单人代表集、生产多人拒绝边界和结构门禁通过；
- 文档只称“固定预算 best-found”，不称普通 Beam 结果为最优。

### M12：未来 Co-op Bot Runtime、客户端控制和承诺性修正

目标：把已验证的离线联合规划器接到真实联机战斗。

当前仓库硬约束仍是生产 Runtime 只支持单人。因此本阶段属于未来独立 Co-op Bot Mod/仓库，或必须等用户明确变更项目边界后再开始；不得现在向 CombatSolver 生产 Runtime 偷加多人兼容分支。

最小架构：

1. 每个客户端安装控制 Mod，只在自己可提交的原版公开入口执行动作；不能假设一个 Mod 实例能直接操纵其他客户端。
2. 协调端从权威联机状态形成带版本、Round、Actor、状态指纹和可行动集合的稳定根。
3. 规划器返回完整路线，但 Runtime 只承诺满足前置条件的短前缀，通常是下一动作或下一不可逆边界。
4. 执行端在提交前核对 Actor、卡牌/药水实例、目标、选择、费用、回合阶段和根指纹。
5. 每个动作返回 ACK、原版结果摘要和新状态指纹；未确认前不把后续动作视为已发生。
6. 队友手动行动、目标死亡、选择改变、网络乱序、动作拒绝或 actual/simulated 漂移立即使旧前缀失效并从新根重规划。
7. 可撤销 EndTurn 作为软承诺；强制 EndTurn、用药、消耗牌、失血和随机结果是不可逆承诺，要在排序或部署政策中单独标识。

协议至少包含：

- `CombatId`、`RootRevision`、`RoundNumber`、`ActorId`、`ActionId`；
- 卡牌/药水实例身份、目标、主选择和嵌套选择；
- 前置状态指纹、允许偏差、超时和幂等键；
- 执行结果、拒绝原因、actual 新指纹和原版事件序号。

实机验收顺序：

- 两客户端：无并发的单动作提交与 ACK；
- 两客户端：一方手动插入动作，旧计划失效并重规划；
- 两客户端：目标死亡、选择取消、可撤销/强制 EndTurn；
- 四客户端：稳定原序、断线/重连、延迟/重复消息和完整一场代表战斗；
- 任何网络测试都不能反向修改离线模拟语义来迎合消息时序。

## 8. 原生验证策略

### 8.1 为什么早期不需要四个真实客户端

卡牌、Power、药水、敌人缩放、牌堆、RNG 和回合命令的权威结果由原版多人 `CombatState` 决定。只要测试宿主能建立真实 2-4 玩家状态并调用原版公开命令，就可以验证绝大部分离线语义。

真实客户端只对以下问题不可替代：

- 网络消息的权威顺序和重复/丢失；
- 一个客户端能否、何时能代表自己的 Actor 提交动作；
- 选择页面、EndTurn 撤销和目标失效在远端的可见时点；
- 队友手动行动与 Bot 计划的竞争；
- ACK、超时、断线和重连。

因此 M1-M11 先完成原生多人状态差分，M12 再做控制 Mod，可以把“战斗语义错误”和“网络承诺错误”分开定位。

### 8.2 测试层级

| 层级 | 用途 | 本计划使用方式 |
|---|---|---|
| L0 | 清单、登记、结构和编译 | M0 目录、门禁、文档；所有阶段提交前 |
| L1 | 单效果 actual/simulated | 37 张牌、缩放、药水、遗物的默认层 |
| L2 | Fork、跨回合、选择 continuation | M2、M5-M7、M9 的有状态机制 |
| L3 | 完整搜索/部署 | M10 收束代表和 M11 质量；不为每张牌跑整场 |

单个失败只保留一条首差异基线和一条最终通过证据。同一根因的多张牌先修共享语义，再选代表重跑；内容目录仍需逐项关闭，但不重复跑无信息量的整场。

## 9. 阶段依赖和进度追踪

| 阶段 | 依赖 | 当前状态 | 完成提交/证据 |
|---|---|---|---|
| M0 版本化目录与失败边界 | F12 | 已完成 | 37 张目录、OnPlay fail-closed；CoverageCatalog 发现 13 个缩放 Power/51 个跨玩家药水目标且 0 漂移；失败边界 `e5b1f3588d9246a182725880c4526608` Passed；Windows 门禁通过，Linux/WSL 环境不可用 |
| M1 原生多人差分底座 | M0 | 未开始 | — |
| M2 身份与私有状态 | M1 | 未开始 | — |
| M3 玩家人数缩放/生成 | M1-M2 | 未开始 | — |
| M4 直接结算牌 | M2-M3 | 未开始 | — |
| M5 Power/Hook 牌 | M2-M4 | 未开始 | — |
| M6 选择/转移/复制牌 | M2、M4-M5 | 未开始 | — |
| M7 球/宠物/角色资源 | M2、M4-M6 | 未开始 | — |
| M8 药水/遗物/反广播/第三方 | M2-M7 | 未开始 | — |
| M9 回合/敌人/死亡/并发选择 | M3-M8 | 未开始 | — |
| M10 完整内容门禁 | M3-M9 | 未开始 | — |
| M11 联合搜索质量 | M10 | 未开始 | — |
| M12 Co-op Bot Runtime | M11；另需项目边界授权 | 未开始 | — |

实施时每个阶段：

- 先把本表改为“进行中”，写明当批 fixture 和预计关闭的 issue；
- 每个共享语义、每一小组卡牌、测试底座和文档证据分别提交，不累计成一个巨型提交；
- 行为源码变化只重跑受影响的最小 fixture；
- 阶段完成后填写提交、fixture ID、游戏版本和直接证据，再开始下一阶段；
- 任何 workaround 或尚未解释的差异进入第 10 节，不借扩 Beam、节点、超时或吞异常绕过。

## 10. 初始问题台账

| 编号 | 问题 | 当前处理 | 关闭阶段 |
|---|---|---|---|
| MP-ISSUE-001 | 21 张多人牌没有已证明的 OnPlay 入口 | 逐张显式建模，不扩大宽泛推断 | M4-M7 |
| MP-ISSUE-002 | 5 张牌只有部分语义或已知高风险 | 拆分入口、Hook、移除/转移生命周期 | M4-M6 |
| MP-ISSUE-003 | 11 张牌虽有专用/通用入口但无原生多人差分 | 一律维持未验证状态 | M4-M7 |
| MP-ISSUE-004 | 敌人 Power 多人缩放没有统一覆盖目录 | 在权威 Power 应用入口实现并生成覆写清单 | M3 |
| MP-ISSUE-005 | 动作 Actor、选择 Actor 和受益 Actor 可能不同 | 显式身份合同，不从 SourceActor 猜测 | M2/M6 |
| MP-ISSUE-006 | 多人 Power 私有字段与防重入状态可能未完整 Fork | 统一 PredictionStateStore/Model 状态政策 | M2/M5 |
| MP-ISSUE-007 | 选择是否阻塞其他玩家、EndTurn 是否可撤销尚无原生命令证据 | 原生命令观察 + 状态机差分 | M6/M9 |
| MP-ISSUE-008 | 跨玩家药水目标、选择和取消未完整验证 | 建多人药水目录与差分 | M8 |
| MP-ISSUE-009 | 当前敌人作用域分类只证明模拟内部一致 | 对当前全部支持行动补原生 2/4 Actor 差分 | M9/M10 |
| MP-ISSUE-010 | F strict replay 不是原版多人 actual diff | M1 建立独立 actual 分支 | M1 |
| MP-ISSUE-011 | 总战损把不同角色 HP 当成等价 | 保留逐 Actor 向量和 workaround，只记录不顺带修正 | M11 后研究 |
| MP-ISSUE-012 | 有界 Beam/BFWS 不保证全局最优 | 仅有限 oracle 称最优；正常结果称 best-found | M11 |
| MP-ISSUE-013 | 生产 Runtime/UI/部署仍为单人，无客户端控制协议 | 保持边界；未来独立 Co-op Bot 层实现 | M12 |
| MP-ISSUE-014 | 游戏仍在更新，wiki 与反编译版本可能漂移 | 所有证据绑定程序集版本，升级后重生成目录 | M0/M10 |

## 11. 最终交付物

离线语义阶段 M0-M11 完成时应具有：

- 版本化的 37 张多人牌、Power 缩放、敌人行动、药水和自用效果覆盖目录；
- 原生 2/3/4 玩家建局与 actual/simulated 差分能力；
- 所有多人状态的根/Fork/状态键/续用戳/差分所有权记录；
- 普通版/升级版逐牌证据和跨回合/四 Actor 代表；
- 多人专属路线的有限 oracle、Beam/BFWS、严格回放和确定性证据；
- 保持单人生产行为与生产多人拒绝边界的回归证据；
- 更新后的架构、测试矩阵、开发笔记、覆盖证据和第三方适配手册。

M12 另行交付：

- 独立 Co-op Bot Mod 的协议、执行器、ACK/幂等、短前缀承诺和漂移重规划；
- 两客户端到四客户端的真实联机证据；
- 明确区分离线战斗语义正确性、搜索质量和网络部署可靠性的三套报告。

## 12. 明确非目标

- 本计划不在语义迁移过程中研究不同角色 HP 的最终价值函数。
- 不通过增加 Beam、节点、时间或并行度掩盖模拟差异。
- 不在 M0-M11 改造生产 UI、生产部署或让 CombatSolver 接管联机客户端。
- 不把战后复活、地图选择、奖励分配或跨房间协商塞入战斗状态；未来需要时另建 Run 层计划。
- 不因为 wiki 列出某规则就跳过当前版本原版源码和原生实际执行验证。
- 不把编译通过、类型被引用、同模拟器回放一致或聚合 HP 相同写成多人语义完成。
