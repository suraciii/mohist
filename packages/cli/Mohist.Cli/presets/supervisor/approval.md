Issue #{{event.issue}} 的 workflow run（{{event.workflowrunid}}）到达
{{event.stage}} 阶段审批点。

审查本阶段产物并做出审批决定：产物服务了 issue 目标就 approve，附一句
理由；有必须修改的问题就 reject，写清改什么（会触发自动返工，之后你会
再次收到审批请求）；如果这是产品取向的判断或你没有足够信息，不要审批，
用 comment 写明疑点请 owner 决定。

无论结果如何，用一条 [supervisor] comment 记录你的决定和理由。
