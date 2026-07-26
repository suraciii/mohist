# 仓库

一个 Project 是一个产品的工作空间。产品的代码可能分布在多个代码库里——server 一个、web 一个。Project 通过声明**仓库（repository）**来引用这些代码库：仓库是 Project 声明的执行资源，每个 Issue 绑定其中一个目标仓库工作。

## 心智模型

- **Project = 产品，仓库 = 资源**。Project 不再等于"一个代码库"；它声明自己用到的一个或多个仓库，就像流水线声明它要用到的资源。
- 每个仓库有一个**资源名**（project 内唯一，如 `server`、`web`）、git 地址和 base branch。资源名是稳定的管理引用句柄。
- 每个 project 恰好有一个 **default 仓库**。只有一个仓库时它自然就是 default。
- 不同 Project 的数据仍然完全隔离；仓库声明不跨 project 共享。

## 管理仓库

仓库是往项目集合里加成员，动词用 `add`：

```bash
mo repo list
mo repo create server --git-url /path/to/my-server --base-branch main
mo repo create web    --git-url /path/to/my-web    --base-branch main
mo repo set-default server
mo repo edit web --base-branch develop
mo repo delete web
```

- 创建 project 时用 `--path` 指定的代码库，会注册为该 project 的 default 仓库——单仓库场景一条命令起步，和过去完全一样。
- default 仓库不能直接删除：先 `set-default` 到别的仓库。
- 非 default 仓库只有在没有未完成 Issue 绑定时可以删除。backlog 和 in_progress Issue 会阻止删除；done 和 cancelled Issue 保留历史目标仓库名但不阻止删除。
- 有 backlog 或 in_progress Issue 使用仓库时，不能修改它的 git 地址或 base branch。切换 default 不影响已经绑定的 Issue。

## Issue 与仓库

每个 Issue 在创建时绑定一个目标仓库。`mo issue create "Web change" --repo web` 显式选择；省略 `--repo` 时绑定创建时的 default 仓库，之后切换 default 不会改写已有 Issue。未启动的 Issue 可用 `mo issue edit <编号> --repo <资源名>` 重指派；首次启动后绑定永久锁定。`mo issue list --repo <资源名>` 根据已存储的绑定筛选，`mo issue view` 显示目标仓库。

工作流的 workspace、分支、diff、rebase、本地集成和 GitHub Pull Request 都使用该 Issue 的目标仓库。Issue 运行期间，目标仓库的 git 地址和 base branch 保持不变；Runner 必须能访问 Project 声明的每个仓库。

## Runner 约束

Runner 必须能访问 Project 声明的**所有**仓库。把仓库加进 Project 前，确认 Runner 所在机器上该 Git 地址可用；使用本机路径或 `file://` 地址时，该路径必须对 Runner 所在机器可见。

## 单仓库承诺

只有一个仓库的 Project，体验与过去完全一致：所有 Issue 自动使用该仓库，你不需要理解本篇的任何概念。多仓库是**加入第二个仓库那一刻**才需要面对的复杂度。

## Non-goals

- **发布协同**：多个仓库的变更"同时上线"的协调不在 Mohist 职责内。
- **一个 issue 检出多个仓库**：联调型工作（需要同时看到两个代码库）暂不支持，将来按真实需求单独评估。
