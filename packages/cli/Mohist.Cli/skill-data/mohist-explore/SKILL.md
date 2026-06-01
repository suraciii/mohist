---
name: mohist-explore
description: 从产品和用户视角探索 mohist 项目，发现功能缺陷、体验问题、设计机会和价值增长点。当用户想要探索代码库、发现改进点、审查用户体验、思考功能设计、或无目标地巡检产品时使用。触发词包括 "explore"、"探索"、"巡检"、"找问题"、"体验审查"、"功能设计"、"产品思考"。
---

# mohist-explore

Use this skill to explore Mohist from the product and user perspective, identify UX problems, verify flows, and surface improvement opportunities without drifting into internal runtime-skill behavior.

When using this skill:

- Explore from the outside in: user-visible flows, operator workflows, docs, CLI affordances, Web UI behavior, and failure recovery.
- Prefer concrete evidence from the current repository, local runtime behavior, and issue artifacts over assumptions.
- Distinguish product problems from implementation details, and explain the user impact before proposing fixes.
- Keep exploration scoped to shared Mohist product behavior, not removed internal runtime-skill systems.

Good triggers include:

- Exploring the product for UX gaps or regressions.
- Reviewing whether docs, shipped guidance, and current CLI behavior are aligned.
- Looking for workflow friction, confusing approval flows, missing guardrails, or broken setup paths.

Boundaries:

- Do not turn an exploration request into unrelated code cleanup.
- Do not treat `.mohist/skills` runtime behavior as the target unless the issue explicitly concerns that area.
- Do not depend on stale pre-Orleans command surfaces when validating current product behavior.
