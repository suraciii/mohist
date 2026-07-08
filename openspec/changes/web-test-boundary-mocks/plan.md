# Web 测试架构迁移执行方案：vi.mock → 边界 mock，终局 isolate:false

> 本方案面向执行 agent。所有验收判据必须可机检；所有"停止询问"条件必须严格遵守。
> 阅读顺序：先读 §1 硬约束与 §2 禁止重试清单，再动手。

## 0. 使命与成功判据

**使命**：消除 web 测试的每文件固定开销（jsdom 重建 + 模块图重执行 + setup 重跑），
使单个测试只付自身工作的成本，并行算力全部用于真实工作。

**成功判据（全部满足才算完成）**：

| # | 判据 | 机检方式 |
|---|------|----------|
| S1 | 测试文件内 `vi.mock(` 调用数 = 0（config 级 alias 白名单除外） | `grep -r "vi\.mock(" src tests --include="*.ts*" \| wc -l` 输出 0 |
| S2 | 全量 `--sequence.shuffle` 三个不同 seed 全绿 | 见 §7 翻转协议 |
| S3 | `isolate: false` 已翻转且全量连续两次绿 | 配置生效 + 两次 `vitest run` 绿 |
| S4 | 测试数 ≥ 4404、文件数 ≥ 293（覆盖不减；期间新增测试同样计入） | vitest 输出对照 |
| S5 | CI web-test job 的 vitest Duration ≤ 100s（当前 208s） | CI run 日志 |
| S6 | 无真实网络/真实时间引入（testing.md 硬约束 1、2 不破） | review + 全量在断网语义下依然绿 |

## 1. 硬约束（不变量，任何一步违反即停止回滚）

1. **主干任何时刻全绿**。每批迁移是原子提交，单独可 revert。
2. **`isolate: true` 保持默认，直到 §7 翻转协议全部通过**。迁移期间的 no-isolate 只用于度量与批验收，不进 CI。
3. **不降低断言强度**：迁移一个测试时，原断言表达的产品契约必须有等价或更强的行为断言。做不到 → 停止询问（§9）。
4. **不引入 flaky**：批验收必须含乱序（shuffle）验证。
5. 遵守 `design/testing.md` 全部硬性原则；`waitFor` 仅用于驱动 MSW 真实异步收敛，timeout 用默认值。

## 2. 禁止重试清单（已实验排除，不要再走）

| 路径 | 排除原因（2026-07-08 实测） |
|---|---|
| 直接翻 `isolate: false` | 415 个测试挂，根因 vi.mock 依赖 per-file 模块注册表 |
| `pool: 'vmThreads'` | 131+ 失败，跨 realm 行为差异，失败集运行间不稳定（flaky 配方） |
| happy-dom 替换 jsdom | environment 仅降 12%（147→130s），2 个 localStorage 保真度失败；且 isolate:false 后此项成本消失，优化目标错误 |
| `deps.optimizer` 预打包 | 无收益（import 139.8→147.5s，噪声级） |
| `pool: 'threads'` | 与 forks 差异个位数百分比，不值得冒原生模块兼容风险 |
| vitest `--shard` | 用户明确否决：摊薄浪费不是消除浪费 |
| `test.concurrent` 文件内并发 | 测试体是同步 CPU 工作（合规测试无真实等待可交错），零收益且引入竞态脏数据 |

## 3. 基线事实（迁移前，用于进度对照）

CI run 28937448826（4 vCPU runner）web-test job：

```
Duration 208.07s (transform 16.32s, setup 64.00s, import 102.89s, tests 162.20s, environment 229.63s)
293 files / 4404 passed / 1 skipped
```

- 计算量 ≈ 575s，固定开销（env+import+setup）= 396.5s = **69%**
- vi.mock 现状：**152 个文件、432 处调用**
- no-isolate 失败基线：**415 个测试**（失败文件清单是迁移工作清单，见 §5）
- 仓库内已有目标模式的参考实现：`src/entities/template/api/*.test.tsx`（MSW + 真 QueryClient）

复核命令：

```bash
cd packages/web
grep -rl "vi\.mock(" src tests --include="*.ts*" | wc -l        # 152
grep -rh "vi\.mock(" src tests --include="*.ts*" | wc -l        # 432
TZ=UTC npx vitest run --no-isolate 2>&1 | tail -3               # 失败基线
```

vi.mock 目标分类（决定替换策略）：

| 目标（处数） | 替换策略 | 所属波次 |
|---|---|---|
| entities/* + api/client（~250） | MSW / queryOptions 纯函数测试 | 波1 |
| react-router-dom（40） | MemoryRouter + customRender 选项 | 波2 |
| project-context（20） | 真 Provider 注入 | 波2 |
| @tanstack/react-query（20） | 真 QueryClient，断言行为或 queryOptions | 波1 |
| shared/* hooks 如 useDocumentTitle（16） | 让真实现跑，断言 `document.title` | 波2 |
| sonner（21） | 真渲染 `<Toaster/>` 断言 DOM；不可行则 config alias shim（§6.3 决策规则） | 波3 |
| @microsoft/signalr（8） | config 级 alias 全局假模块 | 波3 |
| widgets/*（~30，页面测试 stub 子组件） | 渲染真实子树 + MSW | 波4 |

## 4. Phase 0：基建（每步独立提交，每步验收 = `TZ=UTC npx vitest run` 全绿 + `npm run typecheck -w packages/web`）

### 0.1 msw 转正

现在 msw 经 shadcn 传递引入（`npm ls msw` 验证），升级即断。执行：
`npm i -D msw -w packages/web`（版本对齐现有 2.x）。

### 0.2 vitest 配置开启自动恢复

`packages/web/vite.config.ts` 的 `test` 块加：

```ts
restoreMocks: true,
unstubGlobals: true,
unstubEnvs: true,
```

⚠️ 风险：依赖"mock 跨测试残留"的既有测试会挂。全量跑，挂掉的文件逐个修
（把 mock 行为设置从 beforeAll/模块级移进 beforeEach）。这些修复本身就是在还债。

### 0.3 setup.ts 全局 afterEach 增强

`tests/setup.ts` 现有 `cleanup()` 基础上补：

```ts
afterEach(() => {
  cleanup()
  window.localStorage.clear()
  window.sessionStorage.clear()
  document.title = ''
  document.documentElement.className = ''
  vi.useRealTimers()
})
```

注意：node 环境 project 无 window/document，包在 `typeof window !== 'undefined'` 守卫内
（setup.ts 已有此模式）。

### 0.4 MSW handler 工厂库

新建 `tests/support/handlers/{issue,agent,project,epic,settings,coder-session,runner,inbox}.ts`。
每个导出 `defaultXxxHandlers(overrides?)`，返回该 entity 常用端点的 happy-path 响应。
数据用固定常量（禁 `Date.now()`/`Math.random()`，testing.md 硬约束）。
参照 `src/entities/template/api/useProjectTemplates.test.tsx` 里 `defaultHandlers` 的形状。
MSW server 单例放 `tests/support/msw.ts`，**两阶段接线**（执行中修正）：

- **迁移期（isolate:true）**：导出共享 `server` + `useMswServer(...handlers)` 文件级
  helper（beforeAll listen('error') / afterEach resetHandlers+复挂基础 handler / afterAll close）。
  不接进全局 setup.ts——立即全局 listen 会与 12 个既有自建 `setupServer` 文件冲突，
  且 'error' 模式会炸掉当前"容忍后台请求静默失败"的未迁移测试。
- **翻转前整合步（§7 前置）**：既有自建 setupServer 文件全部迁到共享 server 后，
  listen/close 收敛到 setup.ts 全局一次，移除各文件 beforeAll/afterAll，
  onUnhandledRequest 全局收紧为 'error'。per-file listen/close 与 isolate:false
  不兼容（close 会拔掉同 worker 后续文件的拦截器），此整合是翻转的硬前置。

`onUnhandledRequest: 'error'` 是 S6 的执行机制——未 mock 的请求必须炸而不是静默。

### 0.5 customRender 扩展

`tests/test-utils.tsx` 增加选项（保持向后兼容，现有调用零改动）：

```ts
interface CustomRenderOptions extends ... {
  route?: string                    // MemoryRouter initialEntries
  project?: Partial<typeof TEST_PROJECT>
  queryClient?: QueryClient         // 暴露给测试做 cache 断言/invalidate
}
```

Router 从 BrowserRouter 换 MemoryRouter（行为等价 + 可控 initialEntries）。
换完全量跑——BrowserRouter→MemoryRouter 可能影响依赖 URL 的既有测试。

### 0.6 vi.mock 棘轮

无 eslint 配置，用脚本棘轮。新建 `packages/web/scripts/check-vi-mock-ratchet.mjs`：
统计 `vi.mock(` 总数，与 `scripts/vi-mock-baseline.json`（初值 432）比较，
超出即 exit 1 并打印"新增了 vi.mock，请用边界 mock（见 openspec/changes/web-test-boundary-mocks/plan.md）"。
每批迁移后手动下调 baseline，提交里包含 baseline 变更（这就是进度记录）。
接进 CI：`.github/workflows/ci.yml` web-test job 的 Test 步骤后加一步跑该脚本。

## 5. 迁移波次（核心工作）

### 通用批协议（每批 5–15 个文件）

1. 选批 → 逐文件改写（模板见下）→ 该批文件单独验证：
   ```bash
   TZ=UTC npx vitest run <批内文件列表>                          # 常规绿
   TZ=UTC npx vitest run --no-isolate <批内文件列表>              # 无泄漏
   TZ=UTC npx vitest run --no-isolate --sequence.shuffle <批内文件列表>  # 乱序绿
   ```
2. 全量 `TZ=UTC npx vitest run` 绿 + typecheck 绿。
3. 下调棘轮 baseline，原子提交：`test(web): 边界 mock 迁移 <范围>，vi.mock 432→N`。
4. 记录全量 no-isolate 失败数变化（进度指标，写进提交信息）。

### 波1：entities api 测试（~60–70 文件，最机械，先打样）

**打样结论（2026-07-08，agent-usage + cost-rollup 已迁，全绿）**：

- 模式 1a 成立：queryOptions seam（src 侧 +9 行/模块）+ options 对象断言 + MSW fetcher
  断言，测试数逐条等价保留，零 vi.mock，文件留在 node project。
- **node 环境两个技术细节（后续批次必须遵守）**：
  1. fetch 相对路径适配器包在 MSW 代理外层（`tests/support/msw.ts` 的
     `absolutizeRelativeFetchUrls`，listen 之后安装）；不要放 setup.ts——会被
     MSW 的 fetchProxy 盖在外面而失效。
  2. handler 路径一律用 `*/api/...` 通配前缀；裸相对路径在 node 下匹配不到
     绝对 URL 请求。
- **范围修正**：mutation + toast 型文件（agent-sessions、subscription-queries 等
  同时 vi.mock useMutation/useQueryClient/sonner 的）不属于纯 1a，依赖波 3 的
  sonner 决策，划入波 3 之后的批次；波 1 只收 query 型文件。
- glue 覆盖：hook 一行 `useQuery(xxxQueryOptions(useProject().projectId))` 不再有
  直接单测，由渲染该 hook 的页面/面板 spec 传递覆盖；若某 entity 无任何上层
  spec 覆盖，在该 entity 批次里补一个合并的 hooks-glue jsdom 测试文件。

两个子模式，**优先 1a**（保住 node 环境收益）：

**模式 1a（优先）——测 queryOptions/fetcher 纯函数，node 环境，零 React**：
适用于现在只断言 queryKey 形状 / fetch URL / 数据变换的测试（多数）。
若 api 模块未导出 queryOptions，加导出（TanStack 官方 queryOptions 模式，属正向重构；
src 改动 ≤ 10 行/模块，超出见 §9）：

```ts
// BEFORE（vi.mock useQuery 断言 queryKey）
vi.mock('@tanstack/react-query', ...); vi.mock('../../project/@x/project-context', ...)
expect(config.queryKey).toEqual(['agent', 'usage', 'proj-1'])

// AFTER（无 mock，node 环境）
import { agentUsageQueryOptions } from './agent-usage'
expect(agentUsageQueryOptions('proj-1').queryKey).toEqual(['agent', 'usage', 'proj-1'])
// fetcher 走 MSW：
server.use(...)
const data = await agentUsageQueryOptions('proj-1').queryFn(...)
expect(data).toEqual(FIXTURE)
```

**模式 1b——确需 hook 行为（enabled 逻辑、select、invalidate 联动）**：
renderHook + MSW + 真 QueryClient，参照 `entities/template/api` 现有写法。
⚠️ renderHook 需要 DOM：此类文件加 `// @vitest-environment jsdom` 或改名 `.test.tsx`，
并从 vite.config.ts 的 node project exclude 中相应处理。**尽量少用 1b**。

### 波2：router / context / shared hooks（~76 处）

- `vi.mock('react-router-dom')` 断言 navigate 调用 → customRender({ route }) + 断言渲染结果或
  location 探针（`useLocation` 探针组件挂在测试树里）。
- project-context mock → customRender({ project })。
- useDocumentTitle mock → 删 mock，断言 `document.title`。

### 波3：库边界（sonner 21 处、signalr 8 处）

**signalr**：建 `tests/support/signalr-fake.ts`（形状取现有 8 处 vi.mock factory 的并集：
HubConnectionBuilder 链式 API + 可从测试触发 on 回调的控制端口）。
vite.config.ts 测试配置加 `resolve.alias`（仅 test 生效需用 projects 内 alias 或条件判断）：
`'@microsoft/signalr': './tests/support/signalr-fake.ts'`。
全局 alias 对所有文件一致 → 不产生注册表分叉，与 isolate:false 兼容。这是唯一许可的"模块替换"。

**sonner 决策规则**：先挑 2 个文件试真渲染（customRender wrapper 挂 `<Toaster />`，
断言 `screen.getByText(toast文案)`）。若出现 act 警告或需要真实时间等待 → 放弃，
sonner 也走 config alias shim（`tests/support/sonner-fake.ts` 记录调用）。两小时内定案，不纠结。

### 波4：页面测试 stub widgets（~30 处，最需判断，放最后）

`vi.mock('../../../widgets/xxx')` → 删 mock 渲染真实子树，子树的数据需求由 MSW handler 工厂满足。
预期 tests 项耗时上涨 10–20%，被固定开销消失淹没，可接受。
若某处 stub 是为了隔离一个自身有复杂副作用的 widget → 那是该 widget 的设计问题，停止询问（§9）。

### 单例清理（与波次并行推进）

工作清单 = no-isolate 失败文件清单 − vi.mock 文件清单（当前已知嫌疑：
`tests/settings-search-registry.spec.tsx`、`src/shared/api/events-hub.test.tsx` 一族）。
每个单例给 reset seam（测试专用导出 `__resetForTest()` 或 Provider 化），在 setup.ts afterEach 调用。
改 src 模块边界的决策参照 `design/` 约定，单模块改动超 30 行 → 停止询问。

## 6. 增量翻转：node project 先行

vitest 的 `isolate` 是 project 级选项。**波1 完成（模式 1a 文件全部无 mock）后**，
node project 单独翻转，提前兑现收益：

```ts
// vite.config.ts node project
test: { name: 'node', environment: 'node', isolate: false, ... }
```

翻转门：node project 文件单独 `--no-isolate` + shuffle（3 seeds）全绿 ×2。
jsdom project 维持 isolate:true 直到 §7。

## 7. 终局翻转协议（jsdom project）

前置：S1 达成（vi.mock = 0）。顺序执行，任一步失败回到迁移阶段：

```bash
TZ=UTC npx vitest run --no-isolate                                    # 1) 绿
TZ=UTC npx vitest run --no-isolate                                    # 2) 再绿（连续两次）
TZ=UTC npx vitest run --no-isolate --sequence.shuffle --sequence.seed=1
TZ=UTC npx vitest run --no-isolate --sequence.shuffle --sequence.seed=42
TZ=UTC npx vitest run --no-isolate --sequence.shuffle --sequence.seed=20260708  # 3) 三 seed 乱序绿
```

全过 → vite.config.ts 两个 project 都置 `isolate: false`，提交，推送，
观测 CI run 记录 web-test job 的 vitest Duration（对照 S5 ≤ 100s）。

## 8. 守护常驻（翻转后立即添加）

1. 棘轮脚本已在 CI（Phase 0.6），baseline 锁 0。
2. `.github/workflows/` 加 weekly scheduled workflow：
   `vitest run --sequence.shuffle --sequence.seed=$(date +%s)`，失败即有人引入了顺序依赖，
   seed 在日志里可复现。
3. `design/testing.md` web 节更新：mock 只许出现在系统边界（MSW/alias 白名单）；
   `vi.mock` 禁止新增；单例需带 reset seam。（先改 spec 再改代码的项目约定，
   此文档更新应在波1 放量前完成。）

## 9. 停止询问人类的条件

- 某测试的原断言无法用行为断言等价表达（会降低断言强度）。
- 单个 src 模块为 testability 的改动超过 30 行，或需要改变模块公共 API。
- 波3 sonner 两小时决策规则触发后 alias shim 也不可行。
- 全量 no-isolate 失败数在某批迁移后不降反升（说明改写引入了新泄漏模式）。
- 任何步骤中出现无法在 3 次尝试内定位根因的偶发失败。

## 10. 预期数字（完成后核对）

| 指标 | 迁移前（run 28937448826） | 预期 |
|---|---|---|
| vitest 计算量 | ~575s | ~220–240s |
| 其中固定开销 | 396s（69%） | ~25s（per-worker ×4） |
| 单测平均计算成本 | ~130ms | ~50ms |
| CI web-test vitest Duration | 208s | ~80–90s（S5 门槛 100s） |
| CI web-test job 墙钟 | 226s | ~135s |

注：届时流水线瓶颈回到 .NET job（199s）；其后续优化（迁移链 squash 等）与内容哈希缓存
（Turborepo）是独立轨道，不在本方案范围。
