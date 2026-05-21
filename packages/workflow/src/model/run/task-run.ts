import type {
  CausedByMetadata,
  TaskResetMetadata,
  TaskRunState,
  TaskRunStatus,
  WorkItemAttempt,
} from './types';

export class TaskRun {
  status: TaskRunStatus = 'pending';
  dependsOn: string[] = [];
  attempts = 0;
  duration = 0;
  artifacts: string[] = [];
  events: string[] = [];
  output: unknown | null = null;
  reason: string | null = null;
  causedBy: CausedByMetadata | null = null;
  resetBy: TaskResetMetadata | null = null;
  latestAttempt: WorkItemAttempt | null = null;

  constructor(
    readonly id: string,
    readonly title: string,
    readonly order: number,
    readonly uses?: string,
  ) {}

  get terminal(): boolean {
    return this.status === 'completed' || this.status === 'failed' || this.status === 'skipped';
  }

  get succeeded(): boolean {
    return this.status === 'completed';
  }

  resetForFreshAttempt(resetBy: TaskResetMetadata | null = null): void {
    this.status = 'pending';
    this.attempts = 0;
    this.duration = 0;
    this.artifacts = [];
    this.events = [];
    this.output = null;
    this.reason = null;
    this.causedBy = null;
    this.resetBy = resetBy;
    this.latestAttempt = null;
  }

  state(): TaskRunState {
    return {
      id: this.id,
      title: this.title,
      uses: this.uses,
      status: this.status,
      order: this.order,
      dependsOn: [...this.dependsOn],
      attempts: this.attempts,
      duration: this.duration,
      artifacts: [...this.artifacts],
      events: [...this.events],
      output: this.output,
      reason: this.reason,
      causedBy: this.causedBy,
      resetBy: this.resetBy,
      latestAttempt: this.latestAttempt,
    };
  }

  startWorkAttempt(now: string, evidence: Partial<Pick<WorkItemAttempt, 'queueTaskId' | 'acpSessionId' | 'coderSessionId' | 'executionId' | 'processPid'>> = {}): WorkItemAttempt {
    this.status = 'running';
    const attemptNumber = this.latestAttempt ? this.latestAttempt.attemptNumber + 1 : 1;
    this.latestAttempt = {
      state: 'running',
      attemptNumber,
      startedAt: now,
      completedAt: null,
      output: null,
      error: null,
      diagnostic: null,
      queueTaskId: evidence.queueTaskId ?? null,
      acpSessionId: evidence.acpSessionId ?? null,
      coderSessionId: evidence.coderSessionId ?? null,
      executionId: evidence.executionId ?? null,
      processPid: evidence.processPid ?? null,
    };
    return this.latestAttempt;
  }

  completeWorkAttempt(result: { output?: unknown; artifacts?: string[]; events?: string[]; duration?: number; reason?: string }, now: string): WorkItemAttempt | null {
    if (!this.latestAttempt || this.latestAttempt.state !== 'running') return null;
    this.status = 'completed';
    this.resetBy = null;
    this.attempts = this.latestAttempt.attemptNumber;
    this.output = result.output ?? this.output;
    this.artifacts = result.artifacts ?? this.artifacts;
    this.events = result.events ?? this.events;
    this.duration = result.duration ?? this.duration;
    this.reason = result.reason ?? this.reason;
    this.latestAttempt = {
      ...this.latestAttempt,
      state: 'completed',
      completedAt: now,
      output: result.output ?? null,
    };
    return this.latestAttempt;
  }

  failWorkAttempt(error: string, diagnostic: string | null = null, now: string): WorkItemAttempt | null {
    if (!this.latestAttempt || this.latestAttempt.state !== 'running') return null;
    this.status = 'failed';
    this.resetBy = null;
    this.attempts = this.latestAttempt.attemptNumber;
    this.reason = error;
    this.latestAttempt = {
      ...this.latestAttempt,
      state: 'failed',
      completedAt: now,
      error,
      diagnostic,
    };
    return this.latestAttempt;
  }

  interruptWorkAttempt(reason: string, diagnostic: string | null = null, now: string): WorkItemAttempt | null {
    if (!this.latestAttempt || this.latestAttempt.state !== 'running') return null;
    this.status = 'pending';
    this.latestAttempt = {
      ...this.latestAttempt,
      state: 'interrupted',
      completedAt: now,
      error: reason,
      diagnostic,
    };
    return this.latestAttempt;
  }

  synthesizeLatestAttempt(now: string): void {
    if (this.latestAttempt) return;
    if (this.status === 'completed') {
      this.latestAttempt = {
        state: 'completed',
        attemptNumber: Math.max(1, this.attempts),
        startedAt: now,
        completedAt: now,
        output: this.output,
        error: null,
        diagnostic: null,
        queueTaskId: null,
        acpSessionId: null,
        coderSessionId: null,
        executionId: null,
        processPid: null,
      };
    } else if (this.status === 'failed' || this.status === 'skipped') {
      this.latestAttempt = {
        state: 'failed',
        attemptNumber: Math.max(1, this.attempts),
        startedAt: now,
        completedAt: now,
        output: this.output,
        error: this.reason,
        diagnostic: null,
        queueTaskId: null,
        acpSessionId: null,
        coderSessionId: null,
        executionId: null,
        processPid: null,
      };
    } else if (this.status === 'running') {
      this.latestAttempt = {
        state: 'running',
        attemptNumber: Math.max(1, this.attempts),
        startedAt: now,
        completedAt: null,
        output: null,
        error: null,
        diagnostic: null,
        queueTaskId: null,
        acpSessionId: null,
        coderSessionId: null,
        executionId: null,
        processPid: null,
      };
    }
  }
}
