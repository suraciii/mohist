# GitHub

GitHub 集成把 GitHub 变成 Mohist 的需求入口、进度公告板与审批来源：需求以 GitHub issue 的形式进入产线，进度与结果回写到同一个 issue 上，审批可以直接在 PR review 上完成。Mohist 始终是状态的唯一裁判——GitHub 上看到的是产线的投影，不是第二份状态。

交付维度（Integrate 阶段走 GitHub PR）是另一条线，见 [Workflow Profile](workflow-profiles.md) 与 [GitHub PR Action](actions/github-pr.md)，本篇不重复。

## 心智模型

- **GitHub 管「要什么」，Mohist 管「做出来」**：GitHub issue 是需求单；进入 Mohist 后由产线负责执行与状态。
- **人在 GitHub 的三个动作驱动产线**：打供料标签（喂需求）、PR review（做审批）、关闭 issue（撤需求）。
- **Mohist 在 GitHub 的留痕**：状态标签、关键节点评论、完成时关闭 issue。
- **Mohist 以独立 bot 身份留痕**：评论、标签、PR 都是 bot 所为，与你的手工作区分——branch protection 的 review 门槛因此对产线产出同样有效（bot 不能批准自己的 PR）。
- **快照即独立**：Mohist issue 创建时照搬 GitHub issue 的标题与正文，之后两边各自演化、互不回读。要改需求，在 Mohist issue 上改。

## 连接仓库

前提：仓库已注册为 Project 的仓库（见[仓库](repositories.md)）。然后一条命令建立连接：

```bash
mo github connect owner/repo
```

连接按仓库地址自动匹配已注册的仓库资源；匹配不到时报错并提示先注册。连接需要两样 GitHub 侧配置（向导会打印步骤）：

1. **事件推送**：在仓库设置里添加 Webhook，指向你的 Mohist 服务地址——GitHub 上的动作才能实时到达 Mohist。
2. **GitHub 身份**：Mohist 在 GitHub 留痕所用的身份，两种形态（向导引导配置）：
   - **GitHub App（推荐）**：一个属于这套部署的 bot（`your-mohist[bot]`），安装到仓库即可。Mohist 按需换取按仓库收窄的短命令牌，不长期持有 GitHub 访问令牌；回写与交付（push、PR）都以 bot 身份完成，Runner 不再长期保存 GitHub 凭据。
   - **fine-grained PAT（降级）**：只需 Issues 读写权限，仅供回写；它不能触碰代码——clone、push、PR 仍走 Runner 上已有的登录（见 [Runner 指南](runner.md)），留痕身份是你或 Runner 自有账号。

可选配置：

| 配置 | 默认 | 说明 |
|---|---|---|
| 供料标签 | `mohist` | 打上它即视为需求进入 |
| 供料方式 | 直接启动 | 可改为仅入 backlog（`--feed-mode backlog`），人工再启动 |
| 审批者名单 | 空（关闭） | 名单内 GitHub 用户的 PR review 才算审批 |

一个 GitHub 仓库只能连接到一个 Project；一个 Project 可以连接多个仓库。

## 需求入口：打标签即供料

给 GitHub issue 打上供料标签，Mohist 创建对应 issue：标题、正文照搬；目标仓库取连接绑定的仓库；`p0`–`p4` 标签映射为 Mohist 优先级，其余标签不带入。供料方式默认直接启动——从打标签到进产线，不需要离开 GitHub。

规则：

- **一个 GitHub issue 只供料一次**：取消标签再打上，不会重复创建。
- **来源可追**：Mohist issue 上能看到它来自哪个 GitHub issue，可跳转回去。
- **撤销即取消**：GitHub issue 被关闭而 Mohist issue 尚未完成时，Mohist issue 取消——需求方撤回了需求，产线不白跑。
- **谁能供料由 GitHub 仓库权限决定**：能往仓库打标签的人就是能供料的人，Mohist 不再加一层名单（名单只用于审批，见下节）。

## 进度回写

有 GitHub 来源的 issue，Mohist 把进度投影回 GitHub issue。

**状态标签**（`mohist:` 前缀，互斥，同一时刻最多一个）：

| 标签 | 含义 |
|---|---|
| `mohist:in-progress` | 产线运行中 |
| `mohist:awaiting-approval` | 停在审批点，等人决策 |
| `mohist:blocked` | 阻塞，需要人介入 |
| `mohist:done` | 完成（同时关闭 GitHub issue） |

**关键节点评论**（四类，不刷屏）：供料确认（附 Mohist issue 链接）、到达审批点、完成（交付摘要 + PR 链接）、取消（原因）。失败细节走通知渠道（见 [Hermes 通知](hermes-notifications.md)），不回刷 GitHub。

回写失败不阻塞产线：回写是公告板，不是产线状态本身；失败在 Mohist 侧留记录、可见可查。

## PR review 即审批

连接配置了审批者名单后，Check 审批门接受 GitHub PR review 作为审批：

| Review 结论 | 产线动作 |
|---|---|
| Approve | 审批通过 |
| Request changes | 打回，review 正文作为打回理由 |
| Comment | 不产生审批 |

- 只有名单内的 GitHub 用户作出的 review 算数；名单为空即关闭此能力。
- 审批留痕署名 GitHub 用户身份，与 `mo run approve --author` 等价。
- 适用范围是 Check 门（代码审核）；Plan 门审的是计划，彼时还没有 PR，不适用。
- 已知边界：按事件到达时的状态决策；之后 review 被 dismiss 或因新 push 过期，不追溯。

## GitHub 事件进入事件路由

连接建立后，GitHub 上的动作（打标签、关闭、review、PR checks 结果）以 Mohist 事件的形式实时到达，可以被[事件路由](event-routing.md)的表达式直接订阅——例如「PR checks 一变红就叫监管 Agent 来看」。有来源链接的 GitHub 事件挂在对应 issue 的谱系下，「订阅 issue #42 名下的一切」自然覆盖它们。

## 非目标

- **双向同步**：GitHub 侧对标题、正文的编辑不回读；状态只从 Mohist 单向投影到 GitHub，两份真源必然腐坏。
- **GitHub Projects**：看板列与自定义字段不读不写。Projects 的 Status 字段是 project 级数据（同一 issue 在两个看板里可有两个状态），与「Mohist 是唯一状态裁判」冲突；而回写到 issue 的标签与状态在 Projects 看板上自然可见，无需专门集成。
- **GitHub 评论 @ 触发 Agent**：后续阶段按真实需求评估。
- **层级映射**：GitHub sub-issues、milestone 与 Mohist 父子 issue、Epic 不做映射；需求层级在 Mohist 侧管理。
- **GitHub 上的运行控制**：pause / stop / retry 等例外操作留在 Mohist 各入口（CLI、Web、Slack、通知建议动作）。

## 实装差距

> **当前实装差距：** 连接仓库与事件入站已实装：`mo github connect` 建立连接并打印
> GitHub 侧配置清单，打标签、关闭 issue、PR review、check suite 完成等事件验签后实时
> 进入事件路由，可被订阅。供料翻译器、进度回写器与 PR review 审批仍未实装；GitHub
> 身份（App / PAT）的配置与使用也未交付。

当前 GitHub 仅是交付目标（`mohist/github-pr` profile），供料 / 回写 / 审批各能力由
后续 issue 推进落地。

---

设计边界与协议细节见 [`design/github-integration.md`](../design/github-integration.md)。
