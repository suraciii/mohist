# 仓库

一个 Project 是一个产品的工作空间。产品的代码可能分布在多个代码库里——server 一个、web 一个。Project 通过声明**仓库（repository）**来引用这些代码库：仓库是 Project 声明的执行资源，Issue 在哪个代码库里工作，由 issue 的**目标仓库**决定。

## 心智模型

- **Project = 产品，仓库 = 资源**。Project 不再等于"一个代码库"；它声明自己用到的一个或多个仓库，就像流水线声明它要用到的资源。
- 每个仓库有一个**资源名**（project 内唯一，如 `server`、`web`）、git 地址和 base branch。资源名是稳定的引用句柄，issue 用它声明"我改哪个代码库"。
- 每个 project 恰好有一个 **default 仓库**。只有一个仓库时它自然就是 default。
- 不同 Project 的数据仍然完全隔离；仓库声明不跨 project 共享。

## 管理仓库

仓库是往项目集合里加成员，动词用 `add`：

```bash
mo repo list
mo repo add server --git-url /path/to/my-server --base-branch main
mo repo add web    --git-url /path/to/my-web    --base-branch main
mo repo set-default server
mo repo update web --base-branch develop
mo repo delete web
```

- 创建 project 时用 `--path` 指定的代码库，会注册为该 project 的 default 仓库——单仓库场景一条命令起步，和过去完全一样。
- default 仓库不能直接删除：先 `set-default` 到别的仓库。
- 仍有未完结 issue 绑定的仓库不能删除。

## Issue 与仓库

- 每个 issue 有一个**目标仓库**：创建时用 `--repo <资源名>` 指定，不指定就落到 default 仓库。
- issue 启动后目标仓库不可更改——分支已经建在那里了。
- issue 的 workflow 全程发生在目标仓库里：worktree 分支、diff、commits、Integrate 合回的 base branch，都是目标仓库的。

```bash
mo issue create "server: 加订阅 API" --repo server
mo issue create "web: 订阅管理页"    --repo web
mo issue list --repo server
```

一个需求横跨多个仓库时，不要试图让一个 issue 同时改两个代码库——一个 issue 只在一个仓库里工作。把需求拆成每个仓库一个子 issue，见 [复合 Issue 与子 Issue](sub-issues.md)。

## Runner 约束

Runner 必须能访问 project 声明的**所有**仓库。把仓库加进 project 前，确认 runner 所在机器上这些代码库都存在且可用。

## 单仓库承诺

只有一个仓库的 project，体验与过去完全一致：`--repo` 全部可以省略，所有 issue 自动落在唯一的仓库上，你不需要理解本篇的任何概念。多仓库是**加入第二个仓库那一刻**才需要面对的复杂度。

## Non-goals

- **发布协同**：多个仓库的变更"同时上线"的协调不在 Mohist 职责内。Mohist 只负责把每个 issue 的分支合回各自仓库的 base branch。
- **一个 issue 检出多个仓库**：联调型工作（需要同时看到两个代码库）暂不支持，将来按真实需求单独评估。

## 实装差距

本篇是产品 spec，当前实装尚未对齐：project 仍是单仓库模型（创建时 `--path` 绑定唯一代码库），`mo repo` 命令组、default 仓库、issue 目标仓库均未实装，由对应 issue 立项推进。落地时，现有 project 的代码库将平移为它的 default 仓库。

---

对应源码：`packages/server/src/Mohist.Server/Project/`；CLI `packages/cli/`。
