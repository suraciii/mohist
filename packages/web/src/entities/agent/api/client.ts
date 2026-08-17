import { request, projectApiPath } from "../../../shared/api/client";
import type {
  AgentActivity,
  AgentSessionInfo,
  AgentStatus,
} from "../model/types";

type AgentRuntime = "opencode" | "pi";
const DEFAULT_AGENT_RUNTIME: AgentRuntime = "opencode";

export type AgentExecutabilityState =
  "not-configured" | "not-executable" | "unknown" | "executable";

export interface AgentExecutabilityFixEntryPoint {
  label: string;
  path: string;
  command: string;
}

export interface AgentExecutabilityGap {
  code: string;
  message: string;
  nextAction: string;
  fixEntryPoint: AgentExecutabilityFixEntryPoint;
}

export interface AgentExecutabilityResult {
  state: AgentExecutabilityState;
  gaps: AgentExecutabilityGap[];
  pendingLaunchNote: string | null;
}

export interface AgentInfo {
  id: string;
  projectId: string;
  name: string;
  purpose: string | null;
  description: string;
  instructions: string;
  agentConfig: Record<string, unknown> | null;
  effectiveExecutionConfig?: {
    runtime: AgentRuntime;
    model: string | null;
    variant: string | null;
  } | null;
  skills: string[];
  permissions: string[];
  allowedSubagentAgentIds?: string[] | null;
  maxConcurrentRuns: number | null;
  status: string;
  createdAt: string;
  updatedAt: string;
  executability?: AgentExecutabilityResult | null;
}

export interface AgentCreateRequest {
  name: string;
  purpose?: string | null;
  description?: string | null;
  instructions: string;
  agentConfig?: Record<string, unknown> | null;
  skills?: string[] | null;
  permissions?: string[];
  maxConcurrentRuns?: number | null;
  allowedSubagentAgentIds?: string[] | null;
}

export interface AgentUpdateRequest {
  name?: string | null;
  purpose?: string | null;
  description?: string | null;
  instructions?: string | null;
  agentConfig?: Record<string, unknown> | null;
  skills?: string[] | null;
  permissions?: string[];
  maxConcurrentRuns?: number | null;
  allowedSubagentAgentIds?: string[] | null;
}

export interface AgentAvailabilityCapacity {
  usedSlots: number;
  totalSlots: number;
}

export interface AgentAvailabilityResponse {
  canStartNow: boolean;
  waitingReason: string | null;
  activeRuns: number;
  maxConcurrentRuns: number | null;
  capacity: AgentAvailabilityCapacity;
  observedAt: string;
}

export interface AgentAvailabilitySummaryEntry {
  agentId: string;
  canStartNow: boolean;
  waitingReason: string | null;
  activeRuns: number;
  maxConcurrentRuns: number | null;
  capacity: AgentAvailabilityCapacity;
  queuedCount: number;
}

export interface AgentWaitingWorkItem {
  jobId: string;
  status: string;
  waitingReason: string;
  submittedAt: string | null;
}

export interface AgentStatusDetailResponse {
  agentId: string;
  agentName: string;
  availability: AgentAvailabilityResponse;
  waitingWork: AgentWaitingWorkItem[];
}

export function getAgentStatus(projectId?: string | null) {
  return request<AgentStatus>(projectApiPath(projectId, "/agent/status"));
}

export function getAgentDetailStatus(projectId: string, agentRef: string) {
  return request<AgentStatusDetailResponse>(
    projectApiPath(projectId, `/agents/${encodeURIComponent(agentRef)}/status`),
  );
}

export function getAgentSessions(params?: {
  status?: string;
  limit?: number;
  projectId?: string | null;
}) {
  const search = new URLSearchParams();
  if (params?.status) search.set("status", params.status);
  if (params?.limit != null) search.set("limit", String(params.limit));
  const qs = search.toString();
  return request<AgentSessionInfo[]>(
    projectApiPath(params?.projectId, `/agent/sessions${qs ? `?${qs}` : ""}`),
  );
}

export function getAgentActivity(params?: {
  limit?: number;
  projectId?: string | null;
}) {
  const search = new URLSearchParams();
  if (params?.limit != null) search.set("limit", String(params.limit));
  const qs = search.toString();
  return request<AgentActivity>(
    projectApiPath(params?.projectId, `/agent/activity${qs ? `?${qs}` : ""}`),
  );
}

export function listAgents(
  projectId: string,
  params?: { status?: string; all?: boolean },
) {
  const search = new URLSearchParams();
  if (params?.status) search.set("status", params.status);
  if (params?.all) search.set("all", "true");
  const qs = search.toString();
  return request<AgentInfo[]>(
    projectApiPath(projectId, `/agents${qs ? `?${qs}` : ""}`),
  );
}

export function getAgentListAvailability(projectId: string) {
  return request<AgentAvailabilitySummaryEntry[]>(
    projectApiPath(projectId, "/agents/availability"),
  );
}

export function getAgent(projectId: string, id: string) {
  return request<AgentInfo>(
    projectApiPath(projectId, `/agents/${encodeURIComponent(id)}`),
  );
}

export function createAgent(projectId: string, data: AgentCreateRequest) {
  return request<AgentInfo>(projectApiPath(projectId, "/agents"), {
    method: "POST",
    body: JSON.stringify(data),
  });
}

export function updateAgent(
  projectId: string,
  id: string,
  data: AgentUpdateRequest,
) {
  return request<AgentInfo>(
    projectApiPath(projectId, `/agents/${encodeURIComponent(id)}`),
    {
      method: "PATCH",
      body: JSON.stringify(data),
    },
  );
}

export function archiveAgent(projectId: string, id: string) {
  return request<AgentInfo>(
    projectApiPath(projectId, `/agents/${encodeURIComponent(id)}`),
    {
      method: "DELETE",
    },
  );
}

export function unarchiveAgent(projectId: string, id: string) {
  return request<AgentInfo>(
    projectApiPath(projectId, `/agents/${encodeURIComponent(id)}/unarchive`),
    {
      method: "POST",
    },
  );
}

export function readAgentDefinitionModelAndVariant(
  agent: Pick<AgentInfo, "agentConfig"> | null | undefined,
): {
  model: string | null;
  variant: string | null;
  reasoningEffort: string | null;
  runtime: AgentRuntime;
} {
  const config = agent?.agentConfig;
  const rawModel =
    config && typeof config.model === "string" && config.model.trim()
      ? config.model
      : null;
  const rawVariant =
    config && typeof config.variant === "string" && config.variant.trim()
      ? config.variant
      : null;
  const rawReasoningEffort =
    config &&
    typeof config.reasoningEffort === "string" &&
    config.reasoningEffort.trim()
      ? config.reasoningEffort
      : null;
  const rawRuntime =
    config?.runtime === "opencode" || config?.runtime === "pi"
      ? config.runtime
      : null;
  return {
    model: rawModel,
    variant: rawVariant,
    reasoningEffort: rawReasoningEffort,
    runtime: rawRuntime ?? DEFAULT_AGENT_RUNTIME,
  };
}

export function readAgentModelAndVariant(
  agent:
    | (Pick<AgentInfo, "agentConfig"> &
        Partial<Pick<AgentInfo, "effectiveExecutionConfig">>)
    | null
    | undefined,
): {
  model: string | null;
  variant: string | null;
  reasoningEffort: string | null;
  runtime: AgentRuntime;
} {
  const definition = readAgentDefinitionModelAndVariant(agent);
  const effective = agent?.effectiveExecutionConfig;
  const effectiveRuntime =
    effective?.runtime === "opencode" || effective?.runtime === "pi"
      ? effective.runtime
      : null;
  const effectiveModel =
    typeof effective?.model === "string" && effective.model.trim()
      ? effective.model
      : null;
  const effectiveVariant =
    typeof effective?.variant === "string" && effective.variant.trim()
      ? effective.variant
      : null;
  return {
    model: effectiveModel ?? definition.model,
    variant: definition.variant ?? effectiveVariant,
    reasoningEffort: definition.reasoningEffort,
    runtime: effectiveRuntime ?? definition.runtime,
  };
}

export function writeAgentModelAndVariant(
  _current: Record<string, unknown> | null | undefined,
  model: string | null,
  variant: string | null,
  runtime: AgentRuntime = DEFAULT_AGENT_RUNTIME,
  reasoningEffort: string | null = null,
): Record<string, unknown> | null {
  const next: Record<string, unknown> = {};
  if (model === null) {
    if (variant !== null) next.variant = variant;
    if (reasoningEffort !== null) next.reasoningEffort = reasoningEffort;
    if (runtime !== DEFAULT_AGENT_RUNTIME) next.runtime = runtime;
    return Object.keys(next).length > 0 ? next : null;
  }
  next.model = model;
  if (variant !== null) {
    next.variant = variant;
  }
  if (reasoningEffort !== null) {
    next.reasoningEffort = reasoningEffort;
  }
  next.runtime = runtime;
  return next;
}
