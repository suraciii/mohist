# SpecTests 提速二期:调度收尾与尾巴消除

给执行 agent 的实施计划。目标:SpecTests 4 核墙钟 **32.4s → ≤29s**,做完本计划即停手(剩余为 CPU 地板,见"背景")。

## 背景(必读)

一期已落地(commit `eba22a9ef` 并行化 + `d8bfb9eb8` 迁移模板克隆),4 核 45.5s→34.4s。设计约定见 `design/testing.md` "Spec parallelism (server)" 一节:**xUnit collection = 调度单元,collection 内类串行,墙钟 = 最长类链**。collection 定义集中在 `packages/server/tests/Mohist.Server.SpecTests/Support/MohistCollections.cs`。

一期后的量化现状(4 核钉核,trx 实测):

- 墙钟 32.4s;进程 CPU ~90s → **CPU 地板 = 90/4 ≈ 22.5s**,这是本计划无法突破的下限。
- 管线已满:全程在飞测试 ~14 个,平均并发 <4 的窗口仅 4s。**并行度不是问题,不要加线程**(16 线程实测 37.4s,比 8 线程的 34.4s 更差)。
- 剩余可治的三块:① `IntegrationWorkflow` 单链 20.2s 未拆;② WorkflowGrain 三分片成本失衡(12.2 / 6.5 / 轻);③ OtelTracing 串行尾巴 2.2s(xUnit 强制串行 collection 在并行阶段后单独跑)。

## 测量协议(每个任务改前改后都要执行)

```bash
dotnet build packages/server/tests/Mohist.Server.SpecTests/Mohist.Server.SpecTests.csproj -p:SkipWebBuild=true -v q
taskset -c 0-3 dotnet test packages/server/tests/Mohist.Server.SpecTests/Mohist.Server.SpecTests.csproj \
  -p:SkipWebBuild=true --no-build --logger "trx;LogFileName=run.trx"
```

- `taskset -c 0-3` 模拟 CI 4 核,不可省略。
- 跑前 `uptime` 确认 load average < 3;负载高时改用交替对照(改动态与 `git stash` 基线态轮流各跑 2 次)。
- 结果全绿(0 Failed)且 ≥2 轮才算数。已知 `Mohist.Cli.Tests.CliReferenceDocsTests` 有一个与本计划无关的预存失败(docs 含 `mo status`),不要修、不要计入判断——本计划只跑 SpecTests。
- 时间线分析用 T0 落地的脚本。

## T0:落地时间线分析脚本

新建 `scripts/analyze-spectests-trx.py`(可执行,python3 标准库):

```python
#!/usr/bin/env python3
"""解析 SpecTests trx,按 xUnit collection 重建执行时间线。
用法: python3 scripts/analyze-spectests-trx.py <run.trx>"""
import sys, re, glob
import xml.etree.ElementTree as ET
from datetime import datetime
from collections import defaultdict

NS = '{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}'
SPECS = 'packages/server/tests/Mohist.Server.SpecTests'

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
# WorkflowGrainSpecs 基类的 [Collection] 沿继承传播,子类未显式声明时归属基类的 collection
base = coll.get('WorkflowGrainSpecs')
for f in glob.glob(f'{SPECS}/**/*.cs', recursive=True):
    for m in re.finditer(r'class\s+(\w+)\s*:\s*WorkflowGrainSpecs', open(f).read()):
        coll.setdefault(m.group(1), base)

rows = []
for r in root.iter(NS + 'UnitTestResult'):
    if r.get('startTime') and r.get('duration'):
        rows.append((cls_of.get(r.get('testId'), '?').split('.')[-1],
                     datetime.fromisoformat(r.get('startTime')).timestamp(),
                     dur(r.get('duration'))))
t0 = min(s for _, s, _ in rows)

agg = defaultdict(lambda: [1e18, 0.0, 0.0, 0])  # start, end, cost, n
for cls, s, d in rows:
    a = agg[coll.get(cls) or f'(default) {cls}']
    a[0] = min(a[0], s - t0); a[1] = max(a[1], s - t0 + d); a[2] += d; a[3] += 1

wall = max(a[1] for a in agg.values())
print(f'wall {wall:.1f}s | tests {len(rows)} | sum(dur) {sum(d for *_, d in rows):.1f}s')
print(f'{"collection":42} {"cost":>7} {"tests":>5} {"span":>15}')
for c, a in sorted(agg.items(), key=lambda kv: -kv[1][2])[:30]:
    print(f'{c:42} {a[2]:6.1f}s {a[3]:5} {a[0]:6.1f}→{a[1]:6.1f}s')
print('\nlast to finish:')
for c, a in sorted(agg.items(), key=lambda kv: kv[1][1])[-8:]:
    print(f'  finish {a[1]:5.1f}s  {c}')
```

**验收**:对任一 trx 运行输出成本表与收尾表;`cost` 列 top1 与本计划"背景"数字量级一致。

## T1:拆 IntegrationWorkflow

现状(类 → 实测成本):

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

1. `MohistCollections.cs` 增加定义(与现有 IntegrationIssue2/3 同款,注释已说明数字分片约定):
   ```csharp
   [CollectionDefinition("IntegrationWorkflow2")]
   public class IntegrationWorkflow2Collection : ICollectionFixture<MohistIntegrationFixture>;
   ```
2. 按成本分两组(目标各 ~10s):
   - 留在 `IntegrationWorkflow`:WorkflowRunControlApiSpecs、EpicBatchMembershipApiSpecs、WorkflowRunDetailApiSpecs(≈10.0s)
   - 改为 `[Collection("IntegrationWorkflow2")]`:EpicLifecycleSpecs、WorkflowRerunFromStageApiSpecs、WorkflowRetrySessionHealthGuardSpecs、WorkflowSessionSpecs(≈10.1s)
3. 文件位置:`Specs/Workflow/Api/`、`Specs/Workflow/Grain/`、`Specs/Epic/Domain|Api/` 下,按类名 grep 即得。

**验收**:全绿;时间线里 IntegrationWorkflow 与 IntegrationWorkflow2 的 cost 均 ≤12s,二者 span 结束时间都不再是全场最晚的前三名。

## T2:重平衡 WorkflowGrain 三分片

现状:WorkflowGrain3 ≈ 12.2s,WorkflowGrain2 ≈ 6.5s,WorkflowGrain(组一,经 `WorkflowGrainSpecs` 基类继承获得归属)很轻。三片共享同一 fixture 类型,挪动零风险(每片有独立 fixture 实例,时钟/grain 状态互不影响)。

步骤:把 `Specs/Workflow/Grain/WorkflowRetrySpecs.cs`(4.8s,WG3 最重)的 `[Collection("WorkflowGrain3")]` 改为 `[Collection("WorkflowGrain")]`。预期三片变为 ~7.4 / 6.5 / ~7s。

**验收**:全绿;时间线里 WorkflowGrain、WorkflowGrain2、WorkflowGrain3 三者 cost 差 <3s。

## T3:Orderer 成本权重替代字母序

问题:`Support/CostDescendingCollectionOrderer.cs` 现在只分"具名/默认"两档,同档字母序 tiebreak 把重 collection 排到队尾(实测 WorkflowGrain3 等到 18.8s 才拿到线程)。

步骤:把 `Weight` 改为三档显式权重表,重的先跑:

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

**验收**:全绿;时间线里权重 3 的 collection 的 span 起点全部 <2s。

## T4:OtelTracing 挪独立测试程序集(消 2.2s 尾巴)

原理:OtelTracing 的 8 个类共享进程级 `Microsoft.AspNetCore` ActivitySource,必须串行(见 MohistCollections.cs 中该 collection 的 xml-doc,搬移时保留);xUnit 又把串行 collection 排在并行阶段之后,所以它是纯尾巴。挪进独立程序集后,VSTest 按程序集并行、**进程隔离**,ActivitySource 天然不串,约束依旧成立。

步骤:

1. 新建 `packages/server/tests/Mohist.Server.OtelSpecTests/Mohist.Server.OtelSpecTests.csproj`,内容参照 SpecTests 的 csproj(PackageReference 同款;ProjectReference 指向 Mohist.Server;不需要 CopyTemplates/CopyCliSkillData target,除非搬过去的类编译报缺资源再补)。
2. 整体移动 `Specs/SystemSpecs/Otel/` 目录下 OtelTracing collection 的 8 个 spec 类与它们独占的 support 类(如 `OtelSignalRTestHub`、OtelTestHost 类;用编译错误驱动:先搬 spec,缺什么搬什么;若 support 被留下的类共用则复制而非移动)。`MohistOpenTelemetryRegistrationSpecs` 等不属于 OtelTracing collection 的类留在原地。
3. `OtelTracingCollection` 定义(含 DisableParallelization 与 xml-doc)搬入新程序集;从 `MohistCollections.cs` 删除。
4. `dotnet sln Mohist.sln add packages/server/tests/Mohist.Server.OtelSpecTests/Mohist.Server.OtelSpecTests.csproj`。CI 用 `dotnet test Mohist.sln`,无需改动。
5. 新程序集不需要 xunit.runner.json(串行 collection 不受线程数影响)。
6. `design/testing.md` "Spec parallelism" 末尾追加一行:`- OtelTracing lives in its own assembly (process isolation replaces serial-tail scheduling); new process-global-state specs go there too.`

**验收**:`dotnet test Mohist.sln -p:SkipWebBuild=true`(钉 4 核)两个程序集都全绿;SpecTests 的 trx 时间线里不再出现 OtelTracing;SpecTests 墙钟比 T3 完成时再降 ≥1.5s。

## T5(机会性,可跳过):离群慢测试瘦身

>1.5s 的单测试共 4 个,逐个打开看,能在**不减少覆盖边界**的前提下缩数据量才改,否则放过:

- `Specs/Runner/Data/TaskLogStoreSpecs.AppendAsync_TerminalBatchPrunesRowsOutsideRetainedTail`(3.16s)——看保留尾修剪的数据量能否缩到边界值 +1;
- `Specs/Events/EpicReconciliationServiceSpecs.ReconcileOnceAsync_ReadyEpicAfterFirstBatch_IsReached`(1.99s);
- `Specs/Runner/Api/RunnerConfigApiSpecs.Config_ConfiguredPolicy_ProjectsAllFields`(1.74s);
- `Specs/Workflow/Querier/WorkflowRunQuerierSpecs.RunnerPoll_SkipsNonRunnableRowsBeyondFirstPage`(1.71s)。

**验收**:被改测试的断言语义不变(修剪/分页边界仍被覆盖);全绿。

## 禁做清单(均已被一期实验量化排除,不要回锅)

- **去 collection / 追求测试级全并行**:xUnit v2/v3 并行粒度就是 collection;每类自带 fixture 会 +150~250s CPU;共享 host 全并行制造 FakeTimeProvider 竞态。
- **调 maxParallelThreads**:8 是实测甜点(16 线程 37.4s > 8 线程 34.4s),保持 `xunit.runner.json` 现值。
- **web 测试 shard / 加机器**:见 commit `52ff2b6ff` 正文的实验记录。
- **动 CI Build 步骤(75s)**:analyzer 即 lint,缓存 obj/bin 有陈旧风险,一律不碰。
- **用 `it.skip`/`[Fact(Skip)]` 换速度**:违反 testing.md 硬规则。

## 提交约定

按任务分 commit,message 风格参照 `eba22a9ef` / `d8bfb9eb8`(`test(server): ...`,正文写机制与实测数字,结尾 `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`)。T0 单独提交(`chore(test): ...` 或 `test(server): ...` 均可)。

## 完成定义

- T0–T4 全部落地(T5 可选);
- 4 核钉核 SpecTests 墙钟 **≤29s**,≥2 轮全绿;
- `dotnet test Mohist.sln -p:SkipWebBuild=true` 除 CliReferenceDocsTests 预存失败外全绿;
- 时间线收尾表里最后完成者与次后完成者差 <2s(无显著单链尾巴)。

若 T1–T4 全部完成仍 >29s,记录实测时间线到本文件末尾并停手——剩余差距属于 CPU 地板与分散等待,超出本计划范围。
