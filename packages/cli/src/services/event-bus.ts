export type EventMap = {
  stage_changed: { issueId: string; projectId: string; from: string; to: string };
  comment_added: { issueId: string; projectId: string; commentId: string; body: string; createdAt: string };
  agent_started: { issueId: string; projectId: string };
  agent_completed: { issueId: string; projectId: string; issueNumber: number };
  agent_paused: { issueId: string; projectId: string; issueNumber: number };
  agent_error: { issueId: string; projectId: string; error: string };
  approval_requested: { issueId: string; projectId: string; stage: string };
  tool_call: { issueId: string; projectId: string; toolName: string; status: string; locations?: string[] };
  question_asked: { issueId: string; projectId: string; questionId: string; question: string };
  question_answered: { issueId: string; projectId: string; questionId: string; answer: string };
  explore_crystallized: { sessionId: string; issueId: string; projectId: string };
  agent_text_chunk: { issueId: string; projectId: string; text: string; stepIndex: number };
  main_tool_call: { issueId: string; projectId: string; executionId: string; toolName: string; state: 'started' | 'completed' | 'failed'; args?: string; result?: string; error?: string; duration?: number; stepIndex?: number };
  coder_text_chunk: { issueId: string; projectId: string; executionId: string; acpSessionId: string; text: string; coderSessionId?: string; model?: string };
  coder_thought_chunk: { issueId: string; projectId: string; executionId: string; acpSessionId: string; text: string; coderSessionId?: string; model?: string };
  coder_tool_call: { issueId: string; projectId: string; executionId: string; acpSessionId: string; toolName: string; state: 'started' | 'completed'; toolCallId: string; title?: string; rawInput?: unknown; rawOutput?: unknown; rawOutputMetadata?: Record<string, unknown>; status?: string; coderSessionId?: string; model?: string };
  ralph_task_update: { issueId: string; projectId: string; executionId: string; taskId: string; taskIndex: number; totalTasks: number; status: 'started' | 'completed' | 'failed' | 'retrying'; attempt?: number; error?: string };
  ralph_loop_progress: { issueId: string; projectId: string; executionId: string; completed: number; failed: number; total: number };
  plan_round_start: { issueId: string; projectId: string; roundType: string; roundLabel: string; roundIndex: number; acpSessionId?: string; coderSessionId?: string };
  plan_session_update: { issueId: string; projectId: string; roundType: string; roundIndex: number; sessionUpdate: string; data: unknown; acpSessionId?: string; coderSessionId?: string };
  'config:providers:changed': { providers: Array<{ id: string; name?: string; apiKey?: string; baseURL?: string; sdk?: string; models?: string[] }> };
  build_stage_started: { issueId: string; projectId: string; stage: 'build'; changePath: string; tasksCount: number; timestamp: string };
  build_tasks_snapshot: { issueId: string; projectId: string; total: number; pending: number; passed: number };
  build_stage_completed: { issueId: string; projectId: string; completed: number; failed: number; total: number; duration: number; timestamp: string };
  build_stage_failed: { issueId: string; projectId: string; reason: string; details: unknown; timestamp: string };
  merge_queued: { issueId: string; projectId: string; issueNumber: number; position: number };
  merge_started: { issueId: string; projectId: string; issueNumber: number };
  merge_completed: { issueId: string; projectId: string; issueNumber: number };
  merge_failed: { issueId: string; projectId: string; issueNumber: number; reason: string };
  merge_blocked: { issueId: string; projectId: string; issueNumber: number; conflictingFiles: string[]; retryCount: number };
  agent_conflict_resolution_started: { issueId: string; projectId: string; issueNumber: number; conflictFiles: string[] };
  agent_conflict_resolution_completed: { issueId: string; projectId: string; issueNumber: number };
  agent_conflict_resolution_failed: { issueId: string; projectId: string; issueNumber: number; error: string };
  agent_build_fix_started: { issueId: string; projectId: string; issueNumber: number; attempt: number };
  agent_build_fix_completed: { issueId: string; projectId: string; issueNumber: number; attempt: number };
  check_started: { issueId: string; projectId: string; issueNumber: number };
  check_update: { issueId: string; projectId: string; checkName: string; status: string; duration?: number; autoFixed?: boolean; verdict?: string; snapshotSha?: string };
  check_suite_status_changed: { issueId: string; projectId: string; issueNumber: number; suiteStatus: string; snapshotSha: string };
  agent_stopped: { issueId: string; projectId: string; issueNumber: number; reason: string };
  coder_recovery_status: { issueId: string; projectId: string; executionId: string; acpSessionId: string; status: 'detected' | 'recovering' | 'recovered' | 'failed'; attempt: number; reason?: string };
  coder_session_started: { issueId: string; projectId: string; coderSessionId: string; acpSessionId: string; executionId?: string; model?: string; coderType?: string; stage?: string; taskDescription?: string; title?: string | null };
  coder_session_status_changed: { issueId: string; projectId: string; coderSessionId: string; acpSessionId: string; status: string; lastDataAt?: string | null; probeSentAt?: string | null; probeDeadlineAt?: string | null; failureReason?: string | null };
  coder_session_completed: { issueId: string; projectId: string; coderSessionId: string; status: 'completed' | 'failed'; duration: number };
  rebase_started: { issueId: string; projectId: string; issueNumber: number };
  rebase_progress: { issueId: string; projectId: string; issueNumber: number; step: string };
  rebase_completed: { issueId: string; projectId: string; issueNumber: number; rebased: boolean };
  rebase_conflict: { issueId: string; projectId: string; issueNumber: number; conflicts: string[]; status?: string; error?: string };
  agent_blocked: { issueId: string; projectId: string; issueNumber: number; blockedReason: string; retryCount: number };
  skill_started: { skillName: string; runId: string; projectId: string };
  skill_completed: { skillName: string; runId: string; projectId: string; issueId?: string };
  skill_failed: { skillName: string; runId: string; projectId: string; error: string };
  plan_round_complete: { issueId: string; projectId: string; roundType: string; roundLabel?: string; roundIndex: number; duration: number; verdict?: string };
  schedule_triggered: { skillId: string; skillName: string; scheduleType: string };
  schedule_completed: { skillId: string; skillName: string; issueId: string };
  schedule_failed: { skillId: string; skillName: string; error: string };
  stage_task_update: { issueId: string; projectId: string; stage: string; taskId: string; taskTitle: string; status: 'started' | 'completed' | 'failed' | 'retrying'; attempt: number; artifacts: string[] };
  integration_started: { issueId: string; projectId: string; issueNumber: number };
  integration_step_updated: { issueId: string; projectId: string; issueNumber: number; step: string; status: string; summary?: string; output?: unknown };
  integration_completed: { issueId: string; projectId: string; issueNumber: number; steps: Array<{ step: string; status: string; output?: unknown }> };
  integration_failed: { issueId: string; projectId: string; issueNumber: number; failingStep: string; error: string; output?: unknown };
  integration_preflight_refreshed: { issueId: string; projectId: string; issueNumber: number; status: 'passed' | 'failed'; snapshot?: unknown };
  base_branch_advanced: { projectId: string; issueId: string; issueNumber: number; baseBranch: string; newBaseSha: string; previousBaseSha: string };
  base_drift_detected: { projectId: string; issueId: string; issueNumber: number; drifted: boolean; observedBaseSha: string | null; currentBaseSha: string | null; decision: string };
  rebase_opportunity_opened: { projectId: string; issueId: string; issueNumber: number; decision: string; safeWindow: boolean; deferReason?: string };
  active_work_protected: { projectId: string; issueId: string; issueNumber: number; deferReason: string };
  safe_rebase_window_opened: { projectId: string; issueId: string; issueNumber: number };
  rebase_decision_made: { projectId: string; issueId: string; issueNumber: number; decision: string; reason?: string };
  rebase_task_scheduled: { projectId: string; issueId: string; issueNumber: number; reason: string };
  candidate_evidence_invalidated: { projectId: string; issueId: string; issueNumber: number; staleEvidence: { review: boolean; mergeReady: boolean; approval: boolean } };
  user_attention_requested: { projectId: string; issueId: string; issueNumber: number; reason: string; suggestion: string };
};

export type EventName = keyof EventMap;
export type EventListener<T extends EventName = EventName> = (data: EventMap[T]) => void;

type ListenerEntry = {
  listener: EventListener;
};

import { Log } from '../util/log';
import type { WorkflowLogRepo } from '../db/workflow-log-repo';

const log = Log.create({ service: 'event-bus' });

export class EventBus {
  private listeners = new Map<string, Set<ListenerEntry>>();

  on<T extends EventName>(event: T, listener: EventListener<T>): void {
    if (!this.listeners.has(event)) {
      this.listeners.set(event, new Set());
    }
    const entry: ListenerEntry = { listener: listener as EventListener };
    this.listeners.get(event)!.add(entry);
  }

  off<T extends EventName>(event: T, listener: EventListener<T>): void {
    const set = this.listeners.get(event);
    if (!set) return;
    for (const entry of set) {
      if (entry.listener === listener) {
        set.delete(entry);
        break;
      }
    }
    if (set.size === 0) {
      this.listeners.delete(event);
    }
  }

  emit<T extends EventName>(event: T, data: EventMap[T]): void {
    const set = this.listeners.get(event);
    if (!set) return;
    for (const entry of set) {
      try {
        entry.listener(data);
      } catch {
        // swallow listener errors to avoid disrupting other listeners
      }
    }
  }

  emitPersistent<T extends EventName>(
    event: T,
    data: EventMap[T],
    opts: { issueId: string; sessionId?: string | null; workflowLogRepo?: WorkflowLogRepo }
  ): void {
    try {
      this.emit(event, data);
    } catch (err) {
      log.warn('emitPersistent: EventBus.emit failed', { event, error: String(err) });
    }

    if (opts.workflowLogRepo) {
      try {
        opts.workflowLogRepo.insert(opts.issueId, opts.sessionId ?? null, event, data);
      } catch (err) {
        log.warn('emitPersistent: workflow_log write failed', { event, error: String(err) });
      }
    }
  }

  removeAllListeners(event?: EventName): void {
    if (event) {
      this.listeners.delete(event);
    } else {
      this.listeners.clear();
    }
  }
}

export const eventBus = new EventBus();
