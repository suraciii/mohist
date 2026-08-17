# Issue 627: Release blocked Agent work from Runner capacity

## Problem

When an Agent result reaches its durable `blocked` settlement after the
unknown-result deadline, the workflow remains `Running` for late-result
identity checks. Runner accounting must nevertheless stop treating that
attempt as active work.

## Decision

Keep `Unknown` work active until its deadline. On the durable `Blocked`
transition, materialize no active work projection and make Runner capacity and
redelivery queries require a real active work id owned by the queried runner.
Keep the original assignment and settlement identity so a matching late
authoritative report can still settle the original attempt.

## Non-goals

- Do not infer a result, retry, or create a replacement task.
- Do not clear the original worker identity used for late-result validation or
  terminal delivery routing.
- Do not increase Runner slots as a substitute for releasing blocked work.
