# Draft 阶段

## 概述

用户创建 Issue 的初始阶段。

## 触发

用户创建 Issue

## 输入

用户的想法（可能很模糊）

```
示例:
  标题: "加一个搜索功能"
  内容: (空白或简单一句话)
```

## 输出

Issue (状态: draft)

## 执行

```
用户创建 Issue
     │
     ▼
crawlph 检测到新 Issue
     │
     ▼
设置状态: draft
     │
     ▼
自动触发 Explore 阶段
```

## Issue Body 结构

```markdown
# [需求标题]

## 状态
draft

## 描述
(用户初始输入，可能很简短)

## 验收标准
(待 Explore 阶段补充)

## 技术方案
(待 Plan 阶段补充)

## 任务
(待 Plan 阶段补充)

## OpenSpec
(待 Plan 阶段创建)
```

## 转换条件

自动转换到 Explore 阶段
