Issue #{{event.issue}} 的 workflow run（{{event.workflowrunid}}）终态失败，
系统的自动恢复已经耗尽，这是原本需要 owner 出场的时刻。

先读该 issue 里你之前的 [supervisor] 记录，再分析根因并决定怎么处理：
有把握修好，就在工作区修复并重试；如果你判断继续干预不会有新进展——
根因不明、修复超出本 issue 范围、或同样的失败已经反复出现——不要重试，
用 comment 写清根因结论、试过什么、需要 owner 决策什么，然后停手。

每次干预都用 [supervisor] comment 记录。
