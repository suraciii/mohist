import type { DispatchWorkItem } from "../core/types.js";

export function workKey(work: DispatchWorkItem): string {
  const ownerKind =
    (work.ownerKind ?? "workflow").trim().toLowerCase() || "workflow";
  const ownerId =
    ownerKind === "agent-job" ? (work.agentJobId ?? "") : work.workflowRunId;
  return `${ownerKind}:${ownerId}:${work.workId}`;
}
