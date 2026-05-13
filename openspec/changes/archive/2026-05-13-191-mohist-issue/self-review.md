## Self-Review

- Alignment: `proposal.md` covers each issue requirement called out in the task, including the standalone template artifact, `mo instructions`, thinner `mohist` skill, UI ASCII prototype guidance, AgentSkills alignment, and bundled shared-skill installation behavior.
- Completeness: `design.md` specifies all five template groups, CLI behavior, installer behavior, UI guidance requirements, and regression coverage expectations. `tasks.json` includes implementation and test tasks for each design decision.
- Consistency: proposal, design, and tasks use the same naming for `issue-templates.md`, `mo instructions`, `mohist`, `mohist-explore`, and the supported labels. Task outputs and dependencies match the design's migration plan.
- Feasibility: the task order is executable from shared artifact creation through CLI wiring, skill rewrite, installer refactor, and regression tests. No missing prerequisite or circular dependency was found.
- Dependency completeness: every non-initial task declares `dependsOn`; each dependency points to an existing earlier task with lower priority; no cycles were identified.

<promise>PASS</promise>
