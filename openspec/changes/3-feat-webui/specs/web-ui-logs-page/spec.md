## MODIFIED Requirements

### Requirement: 日志页面支持文本搜索

日志页面 SHALL 提供文本搜索输入框，过滤匹配的日志条目。搜索 SHALL 匹配 message、service 和原始文本字段（不区分大小写）。移动端搜索框 SHALL 占满可用宽度。

#### Scenario: 搜索过滤
- **WHEN** 用户在搜索框输入 "agent"
- **THEN** 只显示 message、service 或原始文本中包含 "agent"（不区分大小写）的条目

#### Scenario: 搜索为空时显示全部
- **WHEN** 搜索框为空
- **THEN** 显示所有未被级别筛选排除的条目

#### Scenario: 移动端搜索框占满宽度
- **WHEN** 视口宽度 < 768px
- **THEN** 搜索输入框占满容器宽度（w-full）
