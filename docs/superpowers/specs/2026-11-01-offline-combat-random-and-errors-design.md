# 离线战斗：可复现随机数与结算错误检测

日期：2026-11-01。对应 OSS-374。

离线宿主增加一层游戏随机数边界和一层结算错误检测。攻击、伤害、豁免和技能检定仍走游戏自己的规则。默认运行时是 Unity Mono。游戏版本按仓库现有规则，当前为 2.7.0。

## 游戏随机数

会话在 Harmony 边界打上之后、进入后续启动阶段之前，创建一份 `System.Random`。默认种子是 `GameRandom.DefaultSeed`，值为 `1`。`environment.json` 记录最近一次 `Init` 的种子，以及这次 `Init` 之后的游戏随机调用次数。完整快照只留在进程内，不写入报告。

游戏调用 `InitState` 时换成传入的种子。负数种子沿用 `System.Random` 的取绝对值，这一点写进 `docs/offline.md`。

整数 `Range` 用 `Next(min, max)`，上界不包含。浮点 `Range` 和 `value` 用 `NextDouble()`，结果落在 `[min, max)`。`value` 落在 `[0, 1)`。文档写明这是离线边界的取值，不是 Unity 玩家里的序列。

实现时读取 2.7.0 的 UnityEngine 程序集。只给实际存在的内部调用装前缀：整数 `Range`、浮点 `Range`、`value`、`InitState`、`state` 的 get 和 set。托管包装方法的方法体保持不动。包装已经处理的 `min >= max` 不再在前缀里处理。内部方法本身在 `min >= max` 时仍被调用的，前缀返回 `min`，不抛出 `System.Random` 的参数异常。

接入方式和现有边界一样。重写后的内部调用仍会抛出 `UNITY_NATIVE_UNAVAILABLE`，Harmony 前缀返回 `false` 接住它。前缀使用显式参数，以兼容 Harmony 2.0.4。命中记在边界 `unity-random` 上，合计全部随机入口。`gameRandom.methods` 再按方法名分开计数。

`GameRandom.Save` 复制这份 `System.Random` 的私有字段 `SeedArray`、`inext` 和 `inextp`。`Restore` 把这三个字段抄回去。第一次保存或恢复时如果字段对不上，抛出异常并写明运行时，不改用另一套算法。一局开始前保存外面的进度并设上这一局的种子，结束时恢复。

`Random.state` 的 get 保存一份快照，并返回一个只在本次进程内有效的 `State`。set 用这个值恢复同一条序列。无法识别的 `State` 抛出 `InvalidOperationException`。另一个进程里的 `State` 不能恢复。

`GameRandom.Record(action)` 只记录这次调用里的游戏随机入口，记下方法名、参数和结果。记录不能嵌套。同一份状态重放，记录一致。

游戏或规则代码里另外 `new System.Random()` 的，不替换、不统一种子。

## 三份随机数

| 用途 | 类型 | 规则 |
|---|---|---|
| 游戏结算 | `GameRandom` 持有的那份 `System.Random` | 只有 `InitState`、`Random.state`、`GameRandom.Init` 和 `GameRandom.Restore` 会改它 |
| fuzz 生成参数 | 现有的 `FuzzRandom` | 只在组成调用参数时使用 |
| 外部动作采样 | `ActionSampleRandom` | 调用方用种子构造；`Next(minInclusive, maxExclusive)` 只读自己的一份 |

`ActionSampleRandom` 不读取、不写入 `GameRandom`。这次不做成训练入口。

fuzz 库不引用游戏类型。`FuzzOptions` 增加 `BeforeExecution` 和 `AfterExecution`。引擎每次执行都调用它们，包括失败用例缩小和 `Fuzz.Replay`。生成参数发生在这对钩子之外。

`WotrGameFixture.Fuzz` 安装钩子。执行前先 `Save`，再调用调用方已有的 `BeforeExecution`。执行后先调用调用方已有的 `AfterExecution`，再 `Restore`。`Restore` 在调用方钩子抛出时仍然执行。每个用例因此从进入前的同一份游戏随机状态开始，用例内部的骰点不留给下一个用例。`WotrGameFixture.Fuzz` 不在每个用例上另外调用 `Init`。

## 未控制的随机来源

`System.Random` 的构造函数上有一个只计数的 Harmony 前缀，原来的构造照常执行。计数区分有种子和无种子。边界 id 是 `system-random-observe`。

某个 `RuleScope` 里出现无种子构造时，`RuleScopeResult` 和 `environment.json` 都记下次数。这本身不抛出结算异常。四类结算示例要求次数为零；不为零则该示例失败，报告保留这条说明。

不给 Owlcat 的规则类加补丁，也不替换它们的随机实现。实现结算示例时核对 2.7.0 里攻击、伤害、豁免和技能检定实际走到的随机入口。除 `UnityEngine.Random` 和上述 `System.Random` 构造之外又发现别的来源时，把类型名写入 `gameRandom.uncontrolled` 和 `docs/offline.md`。

## 错误检测

`RuleScope.Run(operation, action, allowances)` 开始时记住已捕获日志的位置，只看这次调用期间新出现的记录。作用域之前的初始化日志留在外面，例如创建单位时缺少 Player、装备插不进槽。

游戏自己的 `catch` 保持原样。判定和报告里的 `logErrors` 相同：严重级别是 `Error` 或 `Exception`，或者这条日志带有异常。Warning 不失败。

成功时返回 `RuleScopeResult`。其中有这次放过的日志和对应原因，以及作用域内的无种子 `System.Random` 构造次数。有新的错误日志时抛出 `RuleSettlementException`。异常包含操作名，以及每条错误的来源、级别、频道、正文和异常文本。生命值等状态即使已经改变，仍然失败。多条错误写进同一个异常。

`RuleLogAllowance` 包含非空原因和一个对 `CapturedLog` 的匹配函数。原因是空字符串或空白时，`Run` 在执行操作前抛出 `ArgumentException`。没有忽略全部错误的开关。

操作自己抛出的异常照原样抛出，其中包括 `UNITY_NATIVE_UNAVAILABLE`。普通离线测试仍用 `UnityNative.SkipReason` 跳过，分类保持 `unity-runtime-required`，不算通过。调用方不能把这种结果当成有效成功样本。

不用 `RuleScope` 的现有测试保持现状。日志继续写入报告，不因为历史错误让整个运行失败。

## 最小结算示例

集成测试位于 `tests/fixtures/RuleTests/`，夹具 mod 位于 `tests/fixtures/RuleMod/`。夹具 mod 只有 `Info.json` 和返回成功的入口方法，不含游戏内容。测试项目把 `WotrModAssembly` 指到这个 mod 的输出。`tests/test_rules.py` 在 `WOTR_TEST_INTEGRATION=1` 时运行该测试项目，并写入 `tests/README.md`。没有游戏文件时不运行。默认运行时是 Unity Mono。

测试源码写死一个 2.7.0 原版 `BlueprintUnit` 的 guid。选定标准是 `OfflineGame.CreateUnit` 能创建它，并且下面的徒手武器蓝图不在装备槽里。运行时不再搜索单位。

单位在 `RuleScope` 之外创建。攻击使用该单位身上游戏已经提供的徒手武器蓝图，测试代码不另行创建武器物品。测试检查这件武器不在单位装备槽里。武器入槽不在这次实现。

每次结算都包在 `RuleScope` 里，顺序是：

1. 技能检定。读取游戏写回规则对象的骰点和成败。
2. 豁免。读取游戏写回的结果。
3. 攻击。读取游戏写回的 d20 和命中结果。
4. 伤害。走游戏的伤害规则，目标生命值必须发生变化。

开发时如果结算因为缺少上下文而让作用域失败，按日志用游戏自己的接口补上那一层状态，把补上的步骤留在提交的测试里。测试运行时不在失败后自动重试。骰点仍来自游戏规则。

种子 `1` 完整跑两遍。四次结算的骰点、成败和生命值变化一致，`GameRandom.Record` 的记录一致。种子 `2` 再跑一遍，记录必须和种子 `1` 不同。每遍开始前 `Save` 并 `Init`，结束后 `Restore`。

另一个测试先改变目标生命值，再在 `catch` 里把异常写进游戏已经捕获的日志通道。`RuleScope` 抛出 `RuleSettlementException`。异常文本包含操作名和那条例外。生命值已经变化不能让测试通过。

## 报告和文档

`environment.json` 增加 `gameRandom`：

| 字段 | 内容 |
|---|---|
| `algorithm` | `System.Random` |
| `runtimeKind` | 当前的 `unity-mono` 或 `netfx` |
| `seed` | 最近一次 `Init` 的种子 |
| `callsSinceInit` | 这次 `Init` 之后的游戏随机调用次数 |
| `intRange` | `[min, max)` |
| `floatRange` | `[min, max)` |
| `methods` | 每个实际命中的 Unity 随机方法及其次数 |
| `uncontrolled` | 无种子 `System.Random` 的构造次数，以及示例里发现的其他随机来源类型名 |

`boundaries` 包含 `unity-random` 和 `system-random-observe`。

`docs/offline.md` 的边界表写上这两项，并写明浮点上界、负数种子和 `Random.state` 只在本次进程内有效。`src/WotR.Testing.Offline/README.md` 的 API 表写上 `GameRandom`、`ActionSampleRandom` 和 `RuleScope`。`CHANGELOG.md` 的 Unreleased 用英文记下这些可见变更。`docs/fuzzing.md` 写明生成参数不消耗游戏随机数，游戏代码执行前后会保存并恢复游戏随机状态。

## 代码位置

| 位置 | 职责 |
|---|---|
| `src/WotR.Testing.Offline/GameRandom.cs` | 持有游戏 `System.Random`，提供 `Init`、`Save`、`Restore`、`Record` 和 `GameRandomCall` |
| `src/WotR.Testing.Offline/ActionSampleRandom.cs` | 调用方自己的采样随机数 |
| `src/WotR.Testing.Offline/RuleScope.cs` | `RuleScopeResult`、`RuleLogAllowance` 和 `RuleSettlementException` |
| `src/WotR.Testing.Offline/UnityRandomBoundary.cs` | `unity-random` 与 `system-random-observe` 的 Harmony 前缀 |
| `src/WotR.Testing.Offline/Boundaries.cs` | 在 `DeclareHarmonyBoundaries` 里登记上述两个边界 |
| `src/WotR.Testing.Fuzzing/FuzzOptions.cs` | `BeforeExecution` 和 `AfterExecution` |
| `src/WotR.Testing.Fuzzing/FuzzEngine.cs` | 在每次执行前后调用钩子 |
| `src/WotR.Testing.Offline/WotrGameFixture.cs` | 为 `Fuzz` 套上保存和恢复，并把 `gameRandom` 写入 `environment.json` |
| `src/WotR.Testing.Offline/GameSession.cs` | `PatchAll` 装上前缀后调用 `GameRandom.Init(DefaultSeed)` |

游戏程序集的方法体保持不变。现有的 IL 对比继续适用。

## 验证

`tests/WotR.Testing.Fuzzing.Tests/` 不需要游戏。它验证钩子在每次执行前后被调用，包括缩小；参数生成发生在钩子之外；钩子为空时现有行为不变。

`tests/fixtures/RuleTests/` 在有游戏文件且 `WOTR_TEST_INTEGRATION=1` 时验证：

- 同一个种子的两次 `Record` 一致。
- `Save` 之后继续取值，`Restore` 后的下一值等于未继续取值的那一条序列的下一值。
- 整数 `Range(0, 1)` 只有 `0`；`Range(1, 3)` 只有 `1` 和 `2`。
- 浮点 `Range(0f, 1f)` 和 `value` 都在 `[0, 1)`。
- 四类真实结算在种子 `1` 上可重复，种子 `2` 的记录不同。
- 徒手武器蓝图不在装备槽中，伤害改变了生命值。
- 故意写入的被吞错误让 `RuleScope` 失败，并指出操作名和异常。
- 一个会调用 `UnityEngine.Random` 的 fuzz 用例结束后，游戏随机调用次数回到该用例开始前。

仓库检查仍运行 `python tests/check_repository.py`、`python -m unittest discover -s tests -p 'test_*.py'`，以及 `tests/` 上现有的 ruff 和 ty 检查。

本机有 AttributeFeats 消费者时，运行它的 `scripts/Invoke-OfflineMechanicsTests.ps1`。本机有 Haste 消费者时，在不排除掷骰专长的情况下构造约 2700 个 fact，索引开启和关闭都要跑完真实规则。没有实际跑到的检查不写成已通过。

`netfx` 使用同一套代码。只有实际跑过 `WotrRuntime=netfx` 时，才把序列是否与 Mono 相同写入本次结果；不预先声称两者一致。

## 不在这次里

不复刻 Unity 原生随机序列。不改游戏方法体。不用预期骰点代替结算。不做武器入槽和整场战局复位，那是 OSS-61。不做训练用的 Reset、Observe 和 Step，那是 OSS-436。不提交或发布游戏 DLL、资源和快照。
