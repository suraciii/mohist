## ADDED Requirements

### Requirement: SSE 连接心跳检测

SSE 端点 SHALL 每 30 秒发送一次心跳注释（`: heartbeat\n`），保持连接活跃并检测断开。如果 `stream.writeSSE` 写入失败（连接已断），SHALL 立即清理该连接的所有 event listener 并结束 stream。

#### Scenario: 正常连接收到心跳
- **WHEN** SSE 客户端已连接 30 秒
- **THEN** 客户端收到 `: heartbeat\n` 注释
- **AND** 客户端忽略该注释（SSE 规范行为）

#### Scenario: 连接断开后清理 listener
- **WHEN** SSE 客户端异常断开（进程崩溃、网络中断）
- **AND** server 尝试发送心跳或事件时检测到写入失败
- **THEN** 该连接的所有 event listener 被清理
- **AND** stream 结束
- **AND** EventBus 的 listener Map 中不再包含该连接的 handler
