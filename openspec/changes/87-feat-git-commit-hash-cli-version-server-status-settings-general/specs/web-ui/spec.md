## ADDED Requirements

### Requirement: WebUI Settings General 显示版本信息

WebUI Settings > General tab 底部 SHALL 显示版本信息区块，包含 mohist 版本号和 git commit hash。版本信息 SHALL 从 `GET /api/health` 响应中读取。

#### Scenario: Settings General 显示版本区块
- **WHEN** 用户打开 Settings > General tab
- **THEN** 页面底部显示版本信息区块
- **AND** 区块包含版本号（如 `0.1.0`）和 git hash（如 `abc1234`）
- **AND** 区块使用低视觉权重样式（灰色文字，不干扰主内容）

#### Scenario: 版本信息加载中或失败
- **WHEN** `GET /api/health` 请求失败或未完成
- **THEN** 版本区块显示 `--` 或加载状态
- **AND** 不阻塞 Settings 页面其他内容的显示
