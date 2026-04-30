## ADDED Requirements

### Requirement: Timeout 设置含解释性层级图表

Agent section SHALL 显示静态文本图表解释 Session/Stage/Task 超时的层级关系，并提供三个独立输入框。图表 SHALL 使用 ASCII 树形结构说明"Session 是总时长上限，Stage 和 Task 是各层级的独立上限"。

#### Scenario: 显示解释性图表
- **WHEN** Agent section 加载
- **THEN** 显示静态文本图表：
  ```
  Session 是总时长上限。Stage 和 Task 是各层级的独立上限，但共享 Session 总预算。

  Session (30 min)
    ├── Stage ≤ 60 min
    │   └── Task ≤ 10 min
    └── Stage ≤ 60 min
  ```
- **AND** 图表中的数值（30/60/10）随当前输入框值动态更新

#### Scenario: Session 超时输入
- **WHEN** 用户修改 Session 输入框为 45
- **THEN** 解释性图表中 Session 行更新为 "Session (45 min)"
- **AND** dirty state 标记为已修改

#### Scenario: Stage 超时输入
- **WHEN** 用户修改 Stage 输入框为 90
- **THEN** 解释性图表中 Stage 行更新为 "Stage ≤ 90 min"
- **AND** dirty state 标记为已修改

#### Scenario: Task 超时输入
- **WHEN** 用户修改 Task 输入框为 15
- **THEN** 解释性图表中 Task 行更新为 "Task ≤ 15 min"
- **AND** dirty state 标记为已修改

#### Scenario: 输入验证
- **WHEN** 用户在任意超时输入框输入 0 或负数
- **THEN** 显示验证错误 "Must be at least 1 minute"
- **AND** Save 按钮禁用

#### Scenario: 输入验证非整数
- **WHEN** 用户在超时输入框输入非整数值
- **THEN** 显示验证错误 "Must be a whole number"
- **AND** Save 按钮禁用

### Requirement: Concurrency 设置

Agent section SHALL 提供 Max Concurrent 输入框（对应 `config.agent.maxConcurrent`）和 Poll Interval 输入框（对应 `config.agent.pollInterval` 或 `poll.interval`）。

#### Scenario: Max Concurrent 显示当前值
- **WHEN** `config.agent.maxConcurrent` 为 8
- **THEN** Max Concurrent 输入框显示 8，单位为 "agents"

#### Scenario: Max Concurrent 验证范围
- **WHEN** 用户输入 Max Concurrent 为 0 或 > 16
- **THEN** 显示验证错误
- **AND** Save 按钮禁用

#### Scenario: Poll Interval 显示当前值
- **WHEN** poll interval 为 30000ms
- **THEN** Poll Interval 输入框显示 30，单位为 "seconds"

#### Scenario: Poll Interval 验证下限
- **WHEN** 用户输入 Poll Interval < 5
- **THEN** 显示验证错误 "Must be at least 5 seconds"

### Requirement: Recovery 设置

Agent section SHALL 提供 Retry Budget（maxGracePeriods）输入框，控制 agent 失败后的最大重试次数。

#### Scenario: Retry Budget 显示当前值
- **WHEN** `config.agent.maxGracePeriods` 为 2
- **THEN** Retry Budget 输入框显示 2，单位为 "grace periods"

#### Scenario: Retry Budget 验证
- **WHEN** 用户输入负数或非整数
- **THEN** 显示验证错误
- **AND** Save 按钮禁用

#### Scenario: Retry Budget 默认值
- **WHEN** `config.agent.maxGracePeriods` 未配置
- **THEN** Retry Budget 输入框显示默认值 2

### Requirement: Section 级 Save 和 Reset

Agent section SHALL 使用一个 "Save Changes" 按钮保存所有修改，替代 per-field Save。SHALL 追踪 dirty state（是否有未保存的修改）。SHALL 提供 "Reset to Defaults" 按钮。

#### Scenario: 修改后显示 Save 按钮
- **WHEN** 用户修改了任意 Agent section 字段
- **THEN** "Save Changes" 按钮变为可点击状态（非 disabled）
- **AND** 按钮样式变为高亮（表示有未保存更改）

#### Scenario: 无修改时 Save 按钮禁用
- **WHEN** Agent section 加载完成且未做任何修改
- **THEN** "Save Changes" 按钮为 disabled 状态
- **AND** 显示为灰色

#### Scenario: Save 所有更改
- **WHEN** 用户修改了 Session=45、Stage=90、Max Concurrent=4
- **AND** 点击 "Save Changes"
- **THEN** 调用 API 批量保存所有修改的字段
- **AND** 成功后 dirty state 清除
- **AND** Save 按钮恢复为 disabled
- **AND** 显示成功提示（如 toast 或内联消息）

#### Scenario: Save 失败
- **WHEN** 保存 API 调用失败
- **THEN** dirty state 保持不变
- **AND** 显示错误消息
- **AND** 输入框值保持用户修改后的值（不回滚）

#### Scenario: Reset to Defaults
- **WHEN** 用户点击 "Reset to Defaults"
- **THEN** 弹出确认对话框 "Reset all agent settings to defaults?"
- **AND** 确认后将所有字段恢复为默认值并保存

#### Scenario: 保存时显示 loading 状态
- **WHEN** Save 请求进行中
- **THEN** Save 按钮显示 loading 状态（如 "Saving..." 或 spinner）
- **AND** 按钮禁用防止重复提交
