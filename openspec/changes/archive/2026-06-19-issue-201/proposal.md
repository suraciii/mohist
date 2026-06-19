## Why

Mohist 目前只有一套硬编码的三段式 PRD 写作规范（来自 mohist-explore skill）。用户管理不同类型工作（bug 报告、产品需求、refactor、spike 探索）时，无法为自己的项目定义 issue 写作规范——每个 section 该写什么、不该写什么、怎么写。就像 GitHub 让每个仓库自定义 issue template 一样，需要在 project 级别引入可配置的 issue template，让用户掌控自己项目的 issue 形态，与决定"issue 怎么执行"的 workflow profile 形成正交的两个配置维度。

## What Changes

- 引入 Issue Template 作为 project 级一等资源，与 workflow profile / repositories / variables 并列；两者镜像对称（都通过 `suitable_for` 匹配、都有 `isDefault`、都有内置默认）。
- 定义 template schema：frontmatter（`name` / `about` / `suitable_for` / `defaults`）+ `sections` 数组（每个 section 含 `title` / `guidance` / `placeholder`）。`guidance` 是模板主内容，定义"这个 section 应该写什么、不该写什么、怎么写"。
- 提供内置默认模板 `mohist/default`（三段式 PRD：User Voice / Product Shape / Domain Model / Acceptance Criteria / Non-Goals），每个 section 携带 guidance，内容取自当前 mohist-explore skill 的对应写作说明。
- 默认模板不可删除（保证开箱即用），但可被 project 禁用；project 可追加自定义模板。
- CLI 新增 `mo issue template list` 与 `mo issue template get <name>`（create/update/delete 后续）。
- API 新增 template 的 list / get 端点（CRUD 后续）。
- Web UI 创建 issue 对话框新增模板选择器，选择后按 section 顺序预填 body 骨架。
- template 的 `suitable_for` 标签与 workflow profile 的 `suitable_for` 共用同一套匹配语义。

本次为纯新增，不改现有 issue schema，无破坏性迁移。

## Capabilities

### New Capabilities

- `issue-template`: issue 模板系统的完整契约——数据模型（frontmatter + sections/guidance）、内置默认 `mohist/default`、作为 project 资源与 workflow profile 镜像对称的语义（`suitable_for` 匹配、`isDefault`、默认不可删但可禁用、project 可追加自定义），以及 list/get 读取面（CLI 命令、API 端点、Web UI 选择器消费）。

### Modified Capabilities

无。本次为纯新增，不改变现有能力的需求层级行为；CLI / API / Web UI 的改动都是对新模板能力的消费（细节见 Impact）。`issue-body-frontmatter`（body 文件 frontmatter）与 `explore-issue-handoff`（workflow `suitable_for` 推荐）均不受影响。

## Impact

- **数据模型 / 存储**: 新增 Issue Template 概念（属 project）。具体存储位置（project 目录或数据库表）留给实现决定；内置 `mohist/default` 作为打包数据始终可用。
- **API**: 新增模板 list / get 端点，返回当前 project 的可用模板（内置默认 + project 自定义）。
- **CLI**: `mo issue template list` / `mo issue template get <name>`，复用 list API。
- **Web UI**: 创建 issue 对话框新增模板选择器，消费 list API，选择后按 `template.sections` 顺序预填 body 骨架（placeholder）。
- **默认模板数据来源**: `mohist/default` 的 sections guidance 内容取自当前 mohist-explore skill 的对应写作说明——仅作为默认模板的数据来源，不改 skill 本身（见 Non-Goals）。
- **与 workflow profile 的对称**: `suitable_for` 匹配语义需与现有 workflow profile 的 `suitable_for` 保持一致（同一套标签与匹配逻辑）。
