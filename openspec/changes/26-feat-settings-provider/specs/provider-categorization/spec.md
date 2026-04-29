## ADDED Requirements

### Requirement: Provider 分组定义

系统 SHALL 将 Settings 页面的 provider 按以下固定顺序分组展示：

1. **已连接**（Connected）— `configured === true` 的 provider
2. **推荐**（Recommended）— 预定义的推荐 provider 列表
3. **Coding Plan** — SDK 类型为 coding plan 的 provider
4. **中国区**（China）— 服务于中国市场的 provider
5. **国际区**（International）— 服务于国际市场的 provider
6. **Custom** — 用户自定义 provider

每个 provider SHALL 只出现在一个分组中。分组优先级从上到下递减。

#### Scenario: 已连接 provider 分组

- **WHEN** 一个 provider 的 `configured === true`
- **THEN** 该 provider SHALL 出现在"已连接"分组，不出现其他分组
- **AND** "已连接"分组 SHALL 始终显示在列表最顶部

#### Scenario: 推荐 provider 分组

- **WHEN** 一个 provider 的 id 在推荐列表中（如 openai、anthropic、deepseek、google、groq、mistral）
- **AND** 该 provider 未配置
- **THEN** 该 provider SHALL 出现在"推荐"分组

#### Scenario: Coding Plan 分组

- **WHEN** 一个 provider 的 id 标记为 coding plan 类型（如 kimi-for-coding、minimax-for-coding、zhipuai-coding-plan、alibaba-coding-plan）
- **AND** 该 provider 未配置
- **THEN** 该 provider SHALL 出现在"Coding Plan"分组

#### Scenario: 中国区分组

- **WHEN** 一个 provider 的 id 标记为中国区（如 glm、qwen、moonshot、doubao、spark、yi、baichuan）
- **AND** 该 provider 不属于已连接、推荐、Coding Plan 分组
- **THEN** 该 provider SHALL 出现在"中国区"分组

#### Scenario: 国际区分组

- **WHEN** 一个 provider 的 id 标记为国际区（如 xai、perplexity、together、fireworks、cohere）
- **AND** 该 provider 不属于已连接、推荐、Coding Plan 分组
- **THEN** 该 provider SHALL 出现在"国际区"分组

#### Scenario: Custom 分组

- **WHEN** 一个 provider 的 `isBuiltin === false`
- **THEN** 该 provider SHALL 出现在"Custom"分组
- **AND** "Custom"分组 SHALL 始终显示在列表最底部

#### Scenario: 空分组隐藏

- **WHEN** 某分组下无 provider（例如搜索过滤后、或无已连接 provider）
- **THEN** 该分组 SHALL 隐藏，不显示空标题

### Requirement: 分组折叠展开

每个分组 SHALL 默认折叠，只显示前 5 个 provider。用户可点击展开按钮查看该分组全部 provider。

#### Scenario: 默认折叠显示

- **WHEN** 用户进入 Settings 页面
- **THEN** 每个分组 SHALL 只显示前 5 个 provider
- **AND** 当分组内 provider 数量超过 5 个时，显示 "Show all (N)" 按钮（N 为该分组总数）

#### Scenario: 展开全部

- **WHEN** 用户点击某分组的 "Show all (N)" 按钮
- **THEN** 该分组 SHALL 展开显示全部 provider
- **AND** 按钮文本变为 "Show less"

#### Scenario: 收起分组

- **WHEN** 用户点击 "Show less" 按钮
- **THEN** 该分组 SHALL 折叠回只显示前 5 个 provider
- **AND** 按钮文本恢复为 "Show all (N)"

#### Scenario: 少于 5 个不显示按钮

- **WHEN** 某分组内 provider 数量 ≤ 5
- **THEN** 该分组 SHALL 显示全部 provider，不显示展开/收起按钮

### Requirement: 分组标题显示统计

每个分组标题 SHALL 显示该分组内的 provider 总数。

#### Scenario: 分组标题格式

- **WHEN** "推荐"分组包含 6 个 provider
- **THEN** 分组标题 SHALL 显示为 "Recommended (6)"

#### Scenario: 已连接分组标题

- **WHEN** 用户已连接 3 个 provider
- **THEN** "已连接"分组标题 SHALL 显示为 "Connected (3)"
