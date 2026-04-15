## ADDED Requirements

### Requirement: Web UI 提供日志查看页面

Web UI SHALL 提供 `/logs` 路由，显示结构化日志查看页面。

#### Scenario: 导航到日志页面
- **WHEN** 用户点击 Header 中的 "Logs" 导航链接
- **THEN** 导航到 `/logs` 路由
- **AND** 显示日志查看页面

### Requirement: 日志页面支持级别筛选

日志页面 SHALL 提供级别筛选 UI（DEBUG / INFO / WARN / ERROR），用户可以切换每个级别的显示/隐藏。默认全部显示。

#### Scenario: 按级别筛选
- **WHEN** 用户取消 "DEBUG" 级别的勾选
- **THEN** 日志列表不显示 level 为 DEBUG 的条目
- **AND** 其他级别的条目正常显示

#### Scenario: 多级别筛选
- **WHEN** 用户只勾选 "ERROR" 和 "WARN"
- **THEN** 只显示 level 为 ERROR 或 WARN 的条目

### Requirement: 日志页面支持文本搜索

日志页面 SHALL 提供文本搜索输入框，过滤匹配的日志条目。搜索 SHALL 匹配 message、service 和原始文本字段（不区分大小写）。

#### Scenario: 搜索过滤
- **WHEN** 用户在搜索框输入 "agent"
- **THEN** 只显示 message、service 或原始文本中包含 "agent"（不区分大小写）的条目

#### Scenario: 搜索为空时显示全部
- **WHEN** 搜索框为空
- **THEN** 显示所有未被级别筛选排除的条目

### Requirement: 日志页面支持自动跟随

日志页面 SHALL 提供 "Auto-follow" 开关。开启时，列表自动滚动到底部显示最新日志。用户手动向上滚动时，自动跟随 SHALL 暂停；用户滚动到底部时恢复。

#### Scenario: 开启自动跟随
- **WHEN** 用户勾选 "Auto-follow"
- **AND** 新日志条目到达
- **THEN** 列表自动滚动到底部，显示最新条目

#### Scenario: 手动滚动暂停自动跟随
- **WHEN** 自动跟随已开启
- **AND** 用户向上滚动离开底部（距离底部超过 10px）
- **THEN** 自动跟随暂停，不自动滚动

#### Scenario: 滚动回底部恢复自动跟随
- **WHEN** 自动跟随已开启且当前处于暂停状态
- **AND** 用户滚动到底部（距离底部不超过 10px）
- **THEN** 自动跟随恢复

### Requirement: 日志页面显示结构化信息

每条日志条目 SHALL 结构化显示：时间、级别（带颜色标记）、服务名（service tag）、消息文本。

#### Scenario: 结构化显示
- **WHEN** 日志条目 `{"level":"ERROR","time":"2026-04-15T10:30:00","service":"agent-runner","message":"LLM call failed"}`
- **THEN** 显示时间为 `10:30:00`（本地时间格式）
- **AND** 级别显示为 `ERROR`（红色或醒目颜色标记）
- **AND** 服务名显示为 `agent-runner`（等宽字体）
- **AND** 消息显示为 `LLM call failed`（等宽字体）

### Requirement: 日志页面支持导出

日志页面 SHALL 提供 "Export" 按钮，将当前筛选后的日志行导出为文本文件下载。

#### Scenario: 导出筛选后的日志
- **WHEN** 用户点击 "Export" 按钮
- **THEN** 浏览器下载一个文本文件
- **AND** 文件包含当前级别和文本筛选后的所有日志行

#### Scenario: 无日志时禁用导出
- **WHEN** 筛选后无日志条目
- **THEN** "Export" 按钮为禁用状态

### Requirement: 日志页面显示截断提示

当日志来源被截断（API 返回 `truncated: true`）时，SHALL 显示提示信息告知用户。

#### Scenario: 显示截断提示
- **WHEN** API 返回 `truncated: true`
- **THEN** 日志列表上方显示提示 "Log output truncated; showing latest chunk"

#### Scenario: 无截断时不显示提示
- **WHEN** API 返回 `truncated: false`
- **THEN** 不显示截断提示

### Requirement: 日志页面显示日志文件路径

日志页面 SHALL 显示当前读取的日志文件路径。

#### Scenario: 显示文件路径
- **WHEN** API 返回 `file: "/home/user/.mohist/logs/dev.log"`
- **THEN** 页面显示 "File: /home/user/.mohist/logs/dev.log"

### Requirement: useLogs hook 管理轮询和缓冲

`useLogs` hook SHALL 封装日志轮询逻辑：定期调用 `GET /api/logs/tail`，使用 cursor 增量读取，维护本地缓冲区（上限 2000 条，超出丢弃最旧）。SHALL 监听 `document.visibilitychange`，tab 隐藏时暂停轮询，回到前台时立即拉取一次增量。

#### Scenario: 轮询增量加载
- **WHEN** 组件挂载且使用 useLogs hook
- **THEN** 首次请求无 cursor 获取初始日志
- **AND** 之后每 3 秒带上次 cursor 请求增量
- **AND** 新增条目追加到缓冲区尾部

#### Scenario: 缓冲区上限
- **WHEN** 缓冲区已有 2000 条
- **AND** 收到 100 条新日志
- **THEN** 缓冲区保留最新 2000 条（丢弃最旧 100 条）

#### Scenario: 后台 tab 暂停轮询
- **WHEN** 用户切换到其他浏览器标签页
- **THEN** useLogs 暂停轮询定时器
- **AND** 用户回到 mohist 标签页时立即执行一次日志拉取

### Requirement: parseLogLine 容错解析

`parseLogLine` SHALL 解析 JSONL 字符串。当某行不是合法 JSON 时，SHALL 返回原始文本并将 level 设为 `null`，不抛出异常。

#### Scenario: 合法 JSONL 解析
- **WHEN** 传入字符串 `'{"level":"INFO","message":"hello"}'`
- **THEN** 返回 `{ raw: "...", level: "INFO", time: null, service: null, message: "hello" }`

#### Scenario: 非法 JSONL 回退
- **WHEN** 传入字符串 `"garbage line"`
- **THEN** 返回 `{ raw: "garbage line", level: null, time: null, service: null, message: "garbage line" }`
- **AND** 不抛出异常
