# SpecTests 提速二期:调度收尾与尾巴消除

给执行 agent 的实施计划。目标:SpecTests 4 核执行窗口 **32.4s -> <=29s**,同时 `dotnet test Mohist.sln` 的 4 核墙钟必须实际下降;不能靠移动测试、增加 skip 或把成本转移到另一个进程来达标。

## 背景(必读)

一期已落地(commit `eba22a9ef` 并行化 + `d8bfb9eb8` 迁移模板克隆),4 核 45.5s -> 34.4s。设计约定见 `design/testing.md` "Spec parallelism (server)" 一节:**xUnit collection = 调度单元,collection 内类串行,墙钟主要受最长类链与 fixture 初始化影响**。collection 定义集中在 `packages/server/tests/Mohist.Server.SpecTests/Support/MohistCollections.cs`。

一期后的量化现状(4 核钉核,`final-4core.trx`):

- SpecTests 执行窗口 32.4s;当前进程 CPU 约 90s,`90/4 = 22.5s` 只是**当前 suite 形状的理论下界**。拆 fixture 或增加 testhost 后必须重新测量,不能继续沿用该数字。
- 管线已满:全程在飞测试约 14 个,平均并发 <4 的窗口仅 4s。**并行度不是问题,不要加线程**(16 线程实测 37.4s,比 8 线程的 34.4s 更差)。
- 剩余可治的三块:① `IntegrationWorkflow` 单链 20.2s 未拆;② WorkflowGrain 三分片成本失衡(5.9 / 6.5 / 12.2s);③ OtelTracing 是 2.2s 串行尾巴。
- TRX 的单测试 `startTime` / `duration` 不包含 collection fixture 的 `InitializeAsync` / `DisposeAsync`;T0 的 `cost` 与 `span` 只用于定位和相对比较,不能当作真实调度时间或完整 CPU 成本。

## 测量协议(每个任务改前改后都要执行)

先构建,计时区间内禁止 build:

```bash
dotnet build packages/server/tests/Mohist.Server.SpecTests/Mohist.Server.SpecTests.csproj -p:SkipWebBuild=true -v q
/usr/bin/time -f 'elapsed=%e user=%U sys=%S' \
  taskset -c 0-3 dotnet test packages/server/tests/Mohist.Server.SpecTests/Mohist.Server.SpecTests.csproj \
  -p:SkipWebBuild=true --no-build \
  --logger "trx;LogFileName=spec.trx" \
  --results-directory packages/server/tests/Mohist.Server.SpecTests/TestResults/phase2/run-01
```

- `taskset -c 0-3` 模拟 CI 4 核,不可省略。记录 TRX 执行窗口、`elapsed`、`user + sys` 三个数字。
- 每个状态至少跑 2 轮,用中位数比较;单轮最好成绩不能作为结论。
- 跑前确认机器没有明显竞争负载。需要交替 A/B 时,为基线 commit 建独立 worktree;**不要用无路径限制的 `git stash` 切换**,避免卷入用户的其他改动。
- T0 从当前源码重建 collection 归属,所以分析 TRX 时源码必须与生成该 TRX 的 revision 一致。历史基线在基线 worktree 中分析。
- 当前 SpecTests 初始基线是 `total=2801, Passed=2792, NotExecuted=9`。T1/T2/T5 不改变计数;T3 只允许显式新增的 orderer 测试增加 Passed。每个被接受的任务完成后冻结新的 outcome 向量,下一任务不得减少 total/Passed 或增加 Failed/NotExecuted。
- 当前 HEAD 已知 `Mohist.Cli.Tests.CliReferenceDocsTests.CliReference_DocumentsRealTopLevelCommandGroupsAndCriticalSubcommands` 有一个与本计划无关的预存失败(docs 含 `mo status`)。开始执行时先跑一次 unfiltered solution:若该失败仍存在,A/B 性能对照统一排除它;若已被其他改动修复,不得继续 filter。最终验证不得出现基线之外的新失败。

## T0:落地时间线分析脚本

新建 `scripts/analyze-spectests-trx.py`(可执行,python3 标准库):

```python
#!/usr/bin/env python3
"""解析 SpecTests trx,按 xUnit collection 重建执行时间线。
用法: python3 scripts/analyze-spectests-trx.py <run.trx> [source-root]"""
import sys, re, glob
import xml.etree.ElementTree as ET
from datetime import datetime
from collections import Counter, defaultdict

NS = '{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}'
SPECS = (sys.argv[2] if len(sys.argv) > 2
         else 'packages/server/tests/Mohist.Server.SpecTests')

def dur(s):
    h, m, sec = s.split(':')
    return int(h) * 3600 + int(m) * 60 + float(sec)

root = ET.parse(sys.argv[1]).getroot()
cls_of = {d.get('id'): d.find(NS + 'TestMethod').get('className')
          for d in root.iter(NS + 'UnitTest')}

coll = {}
for f in glob.glob(f'{SPECS}/**/*.cs', recursive=True):
    src = open(f).read()
    for m in re.finditer(r'\[Collection\("([^"]+)"\)\]\s*(?:\[[^\]]*\]\s*)*'
                         r'public\s+(?:sealed\s+)?(?:abstract\s+)?class\s+(\w+)', src):
        coll[m.group(2)] = m.group(1)
# WorkflowGrainSpecs 基类的 [Collection] 沿继承传播,显式 collection 优先。
base = coll.get('WorkflowGrainSpecs')
for f in glob.glob(f'{SPECS}/**/*.cs', recursive=True):
    for m in re.finditer(
            r'class\s+(\w+)\s*:\s*(?:[\w.]+\.)?WorkflowGrainSpecs',
            open(f).read()):
        coll.setdefault(m.group(1), base)

rows = []
outcomes = Counter()
for r in root.iter(NS + 'UnitTestResult'):
    outcome = r.get('outcome', 'Unknown')
    outcomes[outcome] += 1
    if outcome != 'NotExecuted' and r.get('startTime') and r.get('duration'):
        rows.append((cls_of.get(r.get('testId'), '?').split('.')[-1],
                     datetime.fromisoformat(r.get('startTime')).timestamp(),
                     dur(r.get('duration'))))
if not rows:
    raise SystemExit('trx contains no executed test results')
t0 = min(s for _, s, _ in rows)

agg = defaultdict(lambda: [1e18, 0.0, 0.0, 0])  # start, end, cost, n
classes = defaultdict(lambda: [0.0, 0])
for cls, s, d in rows:
    collection = coll.get(cls) or f'(default) {cls}'
    a = agg[collection]
    a[0] = min(a[0], s - t0); a[1] = max(a[1], s - t0 + d); a[2] += d; a[3] += 1
    classes[(collection, cls)][0] += d; classes[(collection, cls)][1] += 1

window = max(a[1] for a in agg.values())
outcome_text = ' '.join(f'{k}={v}' for k, v in sorted(outcomes.items()))
print(f'test-window {window:.1f}s | {outcome_text} | sum(dur) {sum(d for *_, d in rows):.1f}s')
print(f'{"collection":42} {"cost":>7} {"tests":>5} {"span":>15}')
for c, a in sorted(agg.items(), key=lambda kv: -kv[1][2])[:30]:
    print(f'{c:42} {a[2]:6.1f}s {a[3]:5} {a[0]:6.1f}->{a[1]:6.1f}s')
print('\nslowest classes:')
for (collection, cls), a in sorted(classes.items(), key=lambda kv: -kv[1][0])[:30]:
    print(f'  {a[0]:6.1f}s {a[1]:4}  {collection} / {cls}')
print('\nlast to finish:')
for c, a in sorted(agg.items(), key=lambda kv: kv[1][1])[-8:]:
    print(f'  finish {a[1]:5.1f}s  {c}')
```

**验收**:

- 对任一匹配当前源码 revision 的 TRX 输出 outcome、collection 成本、慢类与收尾表;
- `final-4core.trx` 输出 `test-window` 约 32.4s、`Passed=2792`、`NotExecuted=9`;
- 文档明确 `span start` 是首测时间而非 collection 获得线程的时间。

## T1:拆 IntegrationWorkflow

现状(类 -> 实测成本):

| 类 | 成本 | 测试数 |
|---|---|---|
| WorkflowRunControlApiSpecs | 5.8s | 33 |
| EpicLifecycleSpecs | 4.2s | 24 |
| EpicBatchMembershipApiSpecs | 3.6s | 15 |
| WorkflowRerunFromStageApiSpecs | 3.4s | 10 |
| WorkflowRetrySessionHealthGuardSpecs | 1.9s | 6 |
| WorkflowRunDetailApiSpecs | 0.6s | 11 |
| WorkflowSessionSpecs | 0.6s | 3 |

步骤:

1. `MohistCollections.cs` 增加定义(与现有 IntegrationIssue2/3 同款):
   ```csharp
   [CollectionDefinition("IntegrationWorkflow2")]
   public class IntegrationWorkflow2Collection : ICollectionFixture<MohistIntegrationFixture>;
   ```
2. 按测试成本分两组:
   - 留在 `IntegrationWorkflow`:WorkflowRunControlApiSpecs、EpicBatchMembershipApiSpecs、WorkflowRunDetailApiSpecs(约 10.0s)
   - 改为 `[Collection("IntegrationWorkflow2")]`:EpicLifecycleSpecs、WorkflowRerunFromStageApiSpecs、WorkflowRetrySessionHealthGuardSpecs、WorkflowSessionSpecs(约 10.1s)
3. 分片会增加一个完整 `MohistIntegrationFixture`;单独记录改前改后的 `user + sys`,确认没有用大量新增 CPU 换取小幅墙钟变化。

**验收**:测试计数/outcome 不变;两片 median cost 均 <=12s;二者不再是收尾表最晚三名;SpecTests median `elapsed` 不回退超过 0.5s,`user + sys` 增幅 <=8%。

## T2:重平衡 WorkflowGrain 三分片

现状:`final-4core.trx` 的三片成本为 WorkflowGrain 5.9s、WorkflowGrain2 6.5s、WorkflowGrain3 12.2s。原方案把 4.8s 的 WorkflowRetrySpecs 整体移到第一片后会变成约 10.7 / 6.5 / 7.4s,无法满足差值 <3s,禁止采用。

步骤:

1. 用两轮相同 revision/config 的 TRX 取类成本中位数。当前可执行候选:
   - `WorkflowCheckLoopArtifactSpecs`:`WorkflowGrain3` -> `WorkflowGrain`
   - `WorkflowArtifactBindingSpecs`:`WorkflowGrain3` -> `WorkflowGrain`
   - `WorkflowRunQuerierSpecs`:`WorkflowGrain3` -> `WorkflowGrain2`
2. 该候选按 `final-4core.trx` 预计约 8.2 / 8.3 / 8.2s。若两轮中位数已经明显漂移,用 T0 的慢类表重新求三组,优先最少移动类,不要拆测试或复制 fixture。
3. collection 变化会改变类共享的 fixture 实例和可见状态;不要声称“零风险”。除全套测试外,至少连续重复三片相关测试 5 轮,确认无顺序/共享状态依赖。

**验收**:测试计数/outcome 不变;三片 median cost 最大差值 <3s;相关测试 5 轮全绿;SpecTests median `elapsed` 不回退超过 0.5s。

## T3:Orderer 成本权重替代字母序

问题:`Support/CostDescendingCollectionOrderer.cs` 现在只分“具名/默认”两档。当前 TRX 中 WorkflowGrain3 的首测晚至 18.8s;这包含排队与 fixture 初始化,不是精确的“拿到线程”时间,但足以说明重链启动过晚。

步骤:

1. 把 `Weight` 改为三档显式权重表,重 collection 先排:

   ```csharp
   private static readonly Dictionary<string, int> HeavyCollections = new(StringComparer.Ordinal)
   {
       // Weights derived from trx timeline (scripts/analyze-spectests-trx.py);
       // re-derive when the suite's shape changes noticeably.
       ["IntegrationWorkflow"] = 3,
       ["IntegrationWorkflow2"] = 3,
       ["IntegrationApi"] = 3,
       ["IntegrationIssue"] = 3,
       ["IntegrationIssue2"] = 3,
       ["WorkflowGrain"] = 3,
       ["WorkflowGrain2"] = 3,
       ["WorkflowGrain3"] = 3,
       ["IntegrationSessions"] = 2,
       ["MohistIntegration"] = 2,
       ["MohistIntegration2"] = 2,
       ["RunnerGrain"] = 2,
   };

   private static int Weight(ITestCollection collection) =>
       collection.DisplayName.StartsWith(DefaultCollectionPrefix, StringComparison.Ordinal)
           ? 0
           : HeavyCollections.GetValueOrDefault(collection.DisplayName, 1);
   ```
2. 给 orderer 增加聚焦测试,覆盖四个行为:weight 3 > weight 2、weight 2 > 普通具名、普通具名 > 默认 collection、同权重按 ordinal 名称稳定排序。
3. `span start` 只打印诊断,不再作为 `<2s` 硬门。比较 T2/T3 各至少两轮的整体执行窗口。

**验收**:排序测试全绿;只有这些新增测试增加 Passed,Failed/NotExecuted 不变;SpecTests median 执行窗口相对 T2 至少下降 0.5s且 `elapsed` 不回退。若没有可重复收益,撤销权重表与测试,在计划末尾记录实验结果,保留现有简单 orderer与 T2 outcome 向量。

## T4:OtelTracing 挪独立测试程序集(消 2.2s 尾巴)

原理:OtelTracing 共享进程级 `Microsoft.AspNetCore` ActivitySource,必须与同进程其他 HTTP spec 隔离。新程序集仍保留单一串行 `OtelTracing` collection;solution 级 VSTest 并发让这条串行链与主 SpecTests 重叠,进程隔离防止 ActivitySource 污染。

边界选择:本任务只有第二个测试程序集复用现有 fixture 的真实压力,但抽完整 shared-support 项目会扩大改动。先让 `Mohist.Server.OtelSpecTests` 显式 ProjectReference `Mohist.Server.SpecTests`;共享 fixture/support 继续只有一个权威实现。禁止复制 `MohistIntegrationFixture`、`BacklogFixture`、`GrainTestConfig` 或 fake。若未来出现第三个消费者,再单独评估抽 test-support 项目。

步骤:

1. 新建 `packages/server/tests/Mohist.Server.OtelSpecTests/Mohist.Server.OtelSpecTests.csproj`:
   - PackageReference 与 SpecTests 保持一致;
   - ProjectReference 显式指向 `Mohist.Server` 与 `Mohist.Server.SpecTests`;
   - 复用完整 fixture 时同步保留 SpecTests 的运行时资源复制 target;不能等“编译报错”判断,因为缺模板/skill-data 是运行时失败。
2. 精确移动以下 8 个 spec 类,它们**全部**属于 `OtelTracing`:
   - MohistOpenTelemetryRegistrationSpecs
   - OtelExecutionChainTracingSpecs
   - OtelExporterFailureIsolationSpecs
   - OtelInboundHttpTracingSpecs
   - OtelOrleansSourceNameSpecs
   - OtelOutboundHttpTracingSpecs
   - OtelSignalRTracingSpecs
   - OtelSourceSubscriptionSpecs
3. 同时移动专属 `OtelSignalRTestHub.cs`。`Support/OtelTestHost.cs`、`MohistIntegrationFixture`、`BacklogFixture` 及其依赖留在原 SpecTests 项目,通过 ProjectReference 复用,不移动、不复制。
4. `OtelTracingCollection` 定义(含 `DisableParallelization` 与 xml-doc)搬入新程序集;从 `MohistCollections.cs` 删除。新程序集不需要 `xunit.runner.json`,因为只有这一个串行 collection。
5. `dotnet sln Mohist.sln add packages/server/tests/Mohist.Server.OtelSpecTests/Mohist.Server.OtelSpecTests.csproj`。
6. `design/testing.md` 追加精确约定:`Otel tracing specs live in Mohist.Server.OtelSpecTests; they remain one serial OtelTracing collection, while process isolation lets that chain overlap Mohist.Server.SpecTests.` 不要把所有未来 process-global spec 都归到 Otel 程序集。

验证:

先把 T3 后(若 T3 被拒绝则为 T2 后)的 SpecTests outcome 记为 `B_total / B_passed / B_not_executed`。下面的 solution 命令默认不加 filter;只有执行时基线仍存在已知 CLI 失败才追加测量协议中的同一 filter。

```bash
dotnet build Mohist.sln -p:SkipWebBuild=true -v q
dotnet test packages/server/tests/Mohist.Server.SpecTests/Mohist.Server.SpecTests.csproj \
  -p:SkipWebBuild=true --no-build --logger "trx;LogFileName=spec.trx"
dotnet test packages/server/tests/Mohist.Server.OtelSpecTests/Mohist.Server.OtelSpecTests.csproj \
  -p:SkipWebBuild=true --no-build --logger "trx;LogFileName=otel.trx"
/usr/bin/time -f 'elapsed=%e user=%U sys=%S' \
  taskset -c 0-3 dotnet test Mohist.sln -p:SkipWebBuild=true --no-build
```

**验收**:

- OtelSpecTests 必须为 `total=20, Passed=19, NotExecuted=1`;原 SpecTests 必须为 `B_total-20 / B_passed-19 / B_not_executed-1`;两个项目聚合后严格等于拆分前的 `B` 向量;
- SpecTests TRX 不再出现 OtelTracing,OtelSpecTests 只发现上述 8 类,没有因 ProjectReference 重复发现原 SpecTests;
- 4 核 SpecTests median 执行窗口 <=29s;
- 4 核 solution `elapsed` median 相对拆分前冻结的 `B` 状态至少下降 1.0s,`user + sys` 增幅 <=5%。若 solution 没有可重复收益,撤销拆程序集,不能只凭原 SpecTests 数字下降就落地。

## T5(机会性,可跳过):离群慢测试瘦身

>1.5s 的单测试共 4 个,逐个打开看,能在**不减少覆盖边界**的前提下缩数据量才改,否则放过:

- `Specs/Runner/Data/TaskLogStoreSpecs.AppendAsync_TerminalBatchPrunesRowsOutsideRetainedTail`(3.16s)——看保留尾修剪的数据量能否缩到边界值 +1;
- `Specs/Events/EpicReconciliationServiceSpecs.ReconcileOnceAsync_ReadyEpicAfterFirstBatch_IsReached`(1.99s);
- `Specs/Runner/Api/RunnerConfigApiSpecs.Config_ConfiguredPolicy_ProjectsAllFields`(1.74s);
- `Specs/Workflow/Querier/WorkflowRunQuerierSpecs.RunnerPoll_SkipsNonRunnableRowsBeyondFirstPage`(1.71s)。

**验收**:被改测试的断言语义不变(修剪/分页边界仍被覆盖);测试总数与 outcome 不变;至少两轮确认该测试 median 下降且完整 suite 不回退。

## 禁做清单(均已被一期实验量化排除,不要回锅)

- **去 collection / 追求测试级全并行**:xUnit v2/v3 并行粒度就是 collection;每类自带 fixture 会增加 150-250s CPU;共享 host 全并行制造 FakeTimeProvider 竞态。
- **调 maxParallelThreads**:8 是实测甜点(16 线程 37.4s > 8 线程 34.4s),保持 `xunit.runner.json` 现值。
- **web 测试 shard / 加机器**:见 commit `52ff2b6ff` 正文的实验记录。
- **动 CI Build 步骤(75s)**:analyzer 即 lint,缓存 obj/bin 有陈旧风险,一律不碰。
- **用 `it.skip`/`[Fact(Skip)]` 换速度**:违反 testing.md 硬规则;聚合 outcome 守卫必须捕获新增 skip。
- **复制共享测试 support**:Otel 新程序集通过显式 ProjectReference 复用;不要制造第二份 fixture/fake。

## 提交约定

按被接受的任务分 commit,message 风格参照 `eba22a9ef` / `d8bfb9eb8`(`test(server): ...`,正文写机制与两轮中位数)。T0 单独提交(`chore(test): ...` 或 `test(server): ...` 均可)。只有真实共同作者才添加 `Co-Authored-By`;禁止固定或伪造 agent 身份。被性能门拒绝的实验不提交代码,只把结果记录到本计划末尾。

## 完成定义

- T0 落地,T1-T4 均完成验证且只有通过各自正确性、资源与性能门才落地,T5 可选;
- 两轮 4 核 SpecTests median 执行窗口 **<=29s**;
- 两轮 4 核 solution median `elapsed` 使用与基线相同的 filter 集合且实际下降,不是只转移到其他程序集;
- 两个 spec 程序集聚合 outcome 严格等于拆分前冻结向量;相对初始 `2801 / 2792 / 9`,只允许被接受的 orderer 测试增加 Passed,无新增失败、skip、漏测或重复发现;
- 原 SpecTests 收尾表里最后完成者与次后完成者差 <2s(无显著单链尾巴);
- unfiltered `dotnet test Mohist.sln -p:SkipWebBuild=true --no-build` 不得出现基线之外的新失败;若已知 CliReferenceDocsTests 失败在执行前已被其他改动修复,最终必须全绿。

若完成所有被性能门接受的任务后仍 >29s,把每阶段两轮的执行窗口、`elapsed`、`user + sys`、outcome 与收尾表记录到本文件末尾并停手。重新计算当时 suite 的 CPU 下界;不要把未解释的差距直接归类为“CPU 地板”。
