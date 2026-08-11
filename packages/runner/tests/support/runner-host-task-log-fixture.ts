import type { DispatchWorkItem } from "../../src/core/types.js"

type WorkOverrides = Partial<Pick<DispatchWorkItem,
  "workflowRunId" | "workId" | "uses" | "ownerKind" | "agentJobId"
> & { actionWorkId: string }>

export function taskLogWork(overrides: WorkOverrides = {}): DispatchWorkItem {
  const workflowRunId = overrides.workflowRunId ?? "wf-336"
  return {
    workflowRunId,
    workId: overrides.workId ?? "work-336",
    workType: "task",
    uses: overrides.uses ?? "test/log",
    ownerKind: overrides.ownerKind ?? "workflow",
    agentJobId: overrides.agentJobId ?? "aj-336",
    ...(overrides.actionWorkId ? { with: { workId: overrides.actionWorkId } } : {}),
    variables: {
      workspace: { path: "/virtual/mohist-runner-host-task-log" },
      repository: { gitUrl: "https://example.test/repository.git", baseBranch: "main", name: "master", remoteFingerprint: "fake-fingerprint", remoteIdentityVersion: "1" },
      project: { id: "project-1", name: "Mohist Local" },
      issue: { number: 1 },
      mohist: { runId: workflowRunId },
    },
  }
}
