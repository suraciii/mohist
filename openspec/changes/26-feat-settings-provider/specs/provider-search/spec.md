## ADDED Requirements

### Requirement: Provider 实时搜索过滤

Settings 页面 provider 列表 SHALL 在顶部提供搜索输入框，按 provider 名称和 id 进行实时过滤。搜索 SHALL 同时作用于所有分组（已连接、推荐、分类、未分类）。

#### Scenario: 按名称搜索匹配

- **WHEN** 用户在搜索框输入 "deep"
- **THEN** 系统 SHALL 显示所有名称或 id 包含 "deep"（大小写不敏感）的 provider
- **AND** 隐藏所有不匹配的 provider

#### Scenario: 按名称搜索无匹配

- **WHEN** 用户在搜索框输入 "xyznotfound"
- **AND** 没有 provider 的名称或 id 包含该字符串
- **THEN** 系统 SHALL 显示空状态提示 "No providers found matching your search"

#### Scenario: 清空搜索恢复全部

- **WHEN** 用户清空搜索框内容
- **THEN** 系统 SHALL 恢复显示所有 provider，回到分组折叠的默认状态

#### Scenario: 搜索与分组联动

- **WHEN** 用户在搜索框输入关键词
- **THEN** 系统 SHALL 在每个分组内分别过滤，保留包含匹配 provider 的分组
- **AND** 包含匹配 provider 的分组 SHALL 自动展开
- **AND** 不包含任何匹配 provider 的分组 SHALL 隐藏

### Requirement: 搜索框交互

搜索框 SHALL 支持 Escape 键清空、自动聚焦、输入防抖（300ms）。

#### Scenario: Escape 键清空搜索

- **WHEN** 搜索框有内容且处于聚焦状态
- **AND** 用户按下 Escape 键
- **THEN** 系统 SHALL 清空搜索框并恢复全部 provider 显示

#### Scenario: 输入防抖

- **WHEN** 用户快速连续输入多个字符
- **THEN** 系统 SHALL 在最后一次输入后等待 300ms 再执行过滤
- **AND** 不对每次按键都触发过滤
