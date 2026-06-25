## ADDED Requirements

### Requirement: 更新编排 facade 只持有 stage 编排与公共入口

`mo update` 编排 facade SHALL 只持有公共更新入口（全量/单组件）、stage 编排与 Finalize。五个组件一致性运行时校验 SHALL 驻留在独立的**运行时校验器**协作类中；等待/轮询服务可用 SHALL 驻留在独立的**服务就绪探测器**协作类中。facade SHALL 委托给这些协作类，而不在内部内联实现校验或轮询。

#### Scenario: 运行时一致性校验位于独立类型

- **WHEN** 检查更新模块的运行时一致性校验（五个组件一致性检查）所在位置
- **THEN** 这些校验 SHALL 位于一个独立的运行时校验器类型中
- **AND** 更新编排 facade SHALL NOT 在自身内部内联实现这些校验

#### Scenario: 服务就绪探测位于独立类型

- **WHEN** 检查更新模块的服务就绪等待与轮询逻辑所在位置
- **THEN** 这些逻辑 SHALL 位于一个独立的服务就绪探测器类型中
- **AND** 探测器 SHALL 暴露就绪 DTO 供 facade 消费

#### Scenario: facade 只编排不实现

- **WHEN** 检查更新编排 facade 的方法体
- **THEN** facade SHALL 仅包含公共入口、stage 顺序编排与 Finalize
- **AND** SHALL 通过委托调用校验器与探测器，而非在 facade 内实现其细节

### Requirement: 更新协作类依赖收窄

提取出的运行时校验器与服务就绪探测器 SHALL 各自只依赖其职责所需的部分基础设施（校验器：http/命令执行/文件系统/环境/输出；探测器：http/输出）。更新编排 facade 的单一构造器注入依赖数 SHALL 严格低于重构前的 12 项。

#### Scenario: 校验器只依赖其所需基础设施

- **WHEN** 检查运行时校验器类型的构造器注入依赖
- **THEN** 它 SHALL 只包含 http、命令执行、文件系统、环境与输出
- **AND** SHALL NOT 依赖更新流水线其余基础设施

#### Scenario: 探测器只依赖 http 与输出

- **WHEN** 检查服务就绪探测器类型的构造器注入依赖
- **THEN** 它 SHALL 只包含 http 与输出
- **AND** SHALL NOT 持有校验器或编排相关的依赖

#### Scenario: facade 依赖数显著下降

- **WHEN** 比较更新编排 facade 重构前后的构造器注入依赖数量
- **THEN** 重构后依赖数 SHALL 严格少于 12
- **AND** SHALL 反映为"少数 collaborator + 输出"而非平铺的全部基础设施

### Requirement: 表格渲染器按实体域 partial 拆分

TableRenderer SHALL 按 partial 类按实体域聚簇拆分。核心 partial SHALL 只包含分发入口与共享基础设施（表格写入、JSON 取值、截断）。实体渲染分支 SHALL 驻留在按域聚簇的 partial 中（Issues 簇、Runners 簇、Epics 簇、Entities 簇）。

#### Scenario: 核心 partial 只保留分发与共享基础设施

- **WHEN** 检查 TableRenderer 核心 partial 文件的内容
- **THEN** 它 SHALL 只包含分发入口、表格写入、JSON 取值与截断基础设施
- **AND** SHALL NOT 包含任何具体实体渲染分支

#### Scenario: 实体分支按域聚簇到各自 partial

- **WHEN** 检查 TableRenderer 的实体渲染分支所在文件
- **THEN** Issue/模板/工作流状态/交付失败/反馈与标签格式化 SHALL 集中在 Issues 簇 partial
- **AND** Runner 列表与容量/心跳/作用域格式化 SHALL 集中在 Runners 簇 partial
- **AND** Epic 列表/详情/成员关系 SHALL 集中在 Epics 簇 partial
- **AND** Project/Agent/Session/Repo 等瘦小 peer SHALL 合收在 Entities 簇 partial

### Requirement: 信息模块分离采集、渲染与 systemd 解析

InfoCollector SHALL 拆分为三个职责单一的类型：采集器（仅负责采集系统信息与选项记录）、渲染器（仅负责三种输出格式与行格式化，只依赖 TextWriter 与 InfoResult）、systemd 解析器（仅负责单元可用性/uptime/时间戳解析与单元字段记录，为无实例依赖的静态类型）。路径与进程启发式分类 SHALL 作为采集器内部的私有静态细节保留。

#### Scenario: 渲染器只依赖 TextWriter 与 InfoResult

- **WHEN** 检查渲染器类型的依赖面
- **THEN** 它 SHALL 只依赖 TextWriter 与 InfoResult
- **AND** SHALL NOT 直接依赖文件系统、命令执行、环境或 systemd

#### Scenario: systemd 解析器为静态无依赖类型

- **WHEN** 检查 systemd 解析器类型
- **THEN** 它 SHALL 为静态类型且无实例依赖
- **AND** SHALL 负责单元可用性、uptime 与时间戳解析及单元字段记录

#### Scenario: 采集器只持有采集与启发式

- **WHEN** 检查采集器类型的内容
- **THEN** 它 SHALL 仅包含采集系统信息与选项记录
- **AND** 路径/进程启发式分类 SHALL 作为其私有静态细节保留
- **AND** SHALL NOT 内联渲染或 systemd 解析逻辑

### Requirement: CLI 行为逐字节保持不变

本次重构 SHALL NOT 改变任何 CLI 命令的输出格式、参数、退出码或交互。所有现有 CLI 命令测试 SHALL 不加修改地通过。SHALL NOT 新增或删除任何 CLI 子命令或 CLI 依赖。

#### Scenario: 现有 CLI 测试不变通过

- **WHEN** 在重构后运行 `packages/cli/tests/Mohist.Cli.Tests/` 全套命令测试
- **THEN** 所有测试 SHALL 通过
- **AND** 无任何测试 SHALL 被弱化以适配结构改动

#### Scenario: 输出逐字节一致

- **WHEN** 对比重构前后任一 CLI 命令（update、info、表格输出涉及的列表/详情命令）的 stdout/stderr
- **THEN** 二者 SHALL 逐字节一致
- **AND** 退出码 SHALL 一致

#### Scenario: 子命令集合与依赖清单不变

- **WHEN** 检查重构前后的 CLI 子命令集合与 CLI 项目依赖清单
- **THEN** 二者 SHALL 完全一致
- **AND** SHALL NOT 出现新增或删除的子命令或依赖

### Requirement: 目标文件各自只承担单一变更原因

重构后三个目标文件（更新模块、表格渲染器、信息采集器）SHALL 各自只承担单一变更原因。三者单文件圈复杂度（scc）SHALL 显著下降，且 SHALL NOT 任一仍居 cli 包复杂度前五。

#### Scenario: 单文件聚焦单一职责

- **WHEN** 检查三个目标文件中任一文件的职责
- **THEN** 该文件 SHALL 只为一个变更原因而改变
- **AND** SHALL NOT 混合多个并列职责

#### Scenario: 三者脱离 cli 包复杂度前五

- **WHEN** 用 scc 对 `packages/cli/Mohist.Cli/` 按单文件圈复杂度排序
- **THEN** 三个目标文件 SHALL 均不在前五
- **AND** 更新编排 facade、核心表格 partial、采集器各自的复杂度 SHALL 显著低于重构前对应的单体文件
