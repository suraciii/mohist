import type {
  CheckRunState,
  CheckRunStatus,
  WorkItemAttempt,
} from './types';

export class StageCheck {
  status: CheckRunStatus = 'pending';
  message: string | null = null;
  output: unknown | null = null;
  runCount = 0;
  latestAttempt: WorkItemAttempt | null = null;

  constructor(
    readonly name: string,
    readonly title: string,
  ) {}

  resetForFreshAttempt(): void {
    this.status = 'pending';
    this.message = null;
    this.output = null;
    this.runCount = 0;
    this.latestAttempt = null;
  }

  state(): CheckRunState {
    return {
      name: this.name,
      title: this.title,
      status: this.status,
      message: this.message,
      output: this.output,
      runCount: this.runCount,
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

  completeWorkAttempt(now: string): WorkItemAttempt | null {
    if (!this.latestAttempt || this.latestAttempt.state !== 'running') return null;
    this.status = 'passed';
    this.runCount = this.latestAttempt.attemptNumber;
    this.latestAttempt = {
      ...this.latestAttempt,
      state: 'completed',
      completedAt: now,
    };
    return this.latestAttempt;
  }

  failWorkAttempt(error: string, diagnostic: string | null = null, now: string): WorkItemAttempt | null {
    if (!this.latestAttempt || this.latestAttempt.state !== 'running') return null;
    this.status = 'failed';
    this.runCount = this.latestAttempt.attemptNumber;
    this.message = error;
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
    if (this.status === 'passed') {
      this.latestAttempt = {
        state: 'completed',
        attemptNumber: Math.max(1, this.runCount),
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
    } else if (this.status === 'failed' || this.status === 'error') {
      this.latestAttempt = {
        state: 'failed',
        attemptNumber: Math.max(1, this.runCount),
        startedAt: now,
        completedAt: now,
        output: this.output,
        error: this.message,
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
        attemptNumber: Math.max(1, this.runCount),
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
