import type { AgentDetailEventMap } from '../../agent/@x/events'

export type EventMap = {
  stage_changed: { issueId: string; projectId: string; from: string; to: string }
  comment_added: { issueId: string; projectId: string; commentId: string; body: string; createdAt: string }
  agent_started: { issueId: string; projectId: string }
  agent_completed: { issueId: string; projectId: string }
  agent_paused: { issueId: string; projectId: string }
  agent_error: { issueId: string; projectId: string; error: string }
  agent_blocked: { issueId: string; projectId: string; issueNumber: number; blockedReason: string; retryCount: number }
  approval_requested: { issueId: string; projectId: string; stage: string }
  merge_queued: { issueId: string; projectId: string; issueNumber: number; position: number }
  merge_started: { issueId: string; projectId: string; issueNumber: number }
  merge_completed: { issueId: string; projectId: string; issueNumber: number }
  merge_failed: { issueId: string; projectId: string; issueNumber: number; reason: string }
  rebase_started: { issueId: string; projectId: string; issueNumber: number }
  rebase_progress: { issueId: string; projectId: string; issueNumber: number; step: 'fetching' | 'checking' | 'rebasing' | 'verifying' }
  rebase_completed: { issueId: string; projectId: string; issueNumber: number; rebased: boolean }
  rebase_conflict: { issueId: string; projectId: string; issueNumber: number; conflicts: string[]; status?: string; error?: string }
  agent_conflict_resolution_started: { issueId: string; projectId: string; issueNumber: number }
  agent_conflict_resolution_completed: { issueId: string; projectId: string; issueNumber: number }
  agent_conflict_resolution_failed: { issueId: string; projectId: string; issueNumber: number; error: string }
  check_started: { issueId: string; projectId: string; issueNumber: number }
  check_update: { issueId: string; projectId: string; checkName: string; status: string; duration?: number; autoFixed?: boolean; verdict?: string; snapshotSha?: string }
  check_suite_status_changed: { issueId: string; projectId: string; issueNumber: number; suiteStatus: string; snapshotSha: string }
  integration_started: { issueId: string; projectId: string; issueNumber: number }
  integration_step_updated: { issueId: string; projectId: string; issueNumber: number; step: string; status: string; summary?: string; output?: unknown }
  integration_completed: { issueId: string; projectId: string; issueNumber: number; steps: Array<{ step: string; status: string; output?: unknown }> }
  integration_failed: { issueId: string; projectId: string; issueNumber: number; failingStep: string; error: string; output?: unknown }
  base_drift_detected: { issueId: string; projectId: string; issueNumber: number; baseBranch: string; observedBaseSha: string | null; currentBaseSha: string | null; decision: string }
  rebase_opportunity: { issueId: string; projectId: string; issueNumber: number; decision: string; deferReason?: string }
  user_attention_requested: { issueId: string; projectId: string; issueNumber: number; reason: string; nextAction: string }
} & AgentDetailEventMap

export type EventName = keyof EventMap
