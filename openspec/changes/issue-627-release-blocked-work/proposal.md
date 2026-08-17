# Issue 627: Release blocked Agent work from Runner capacity

## Problem

When an Agent result reaches its durable `blocked` settlement after the
unknown-result deadline, the workflow row remains `Running` and keeps its
original runner assignment. Runner capacity queries therefore count the work
again even though the unresolved settlement has already closed dispatch and
the task cannot be redelivered.

## Decision

Runner capacity and in-flight redelivery queries must exclude workflow rows
whose indexed `AttentionStatus` is `blocked`. The original assignment and
settlement identity remain persisted so a matching authoritative late result
can still settle the original attempt and all other identities remain fenced.

## Non-goals

- Do not infer a result, retry, or create a replacement task.
- Do not clear the original worker identity used for late-result validation or
  terminal delivery routing.
- Do not increase Runner slots as a substitute for releasing blocked work.
