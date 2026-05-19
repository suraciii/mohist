import { DatabaseManager } from '../db/database';
import { IssueTaskQueueRepo } from '../db/issue-task-queue-repo';
import { CoderSessionRepo, type CoderSession } from '../db/coder-session-repo';
import type { WorkItemAttempt } from '../workflow/domain';
import { Log } from '../util/log';

const log = Log.create({ service: 'attempt-reconciliation' });

export interface AttemptReconciliationResult {
  reconciled: boolean;
  interruptedCount: number;
  reasons: string[];
  interruptedAttempts: WorkItemAttempt[];
}

export interface WorkflowAttemptEvidencePort {
  hasActiveQueueTask(issueId: string): boolean;
  hasLiveProcess(session: CoderSession): boolean;
  findQueueTaskById(queueTaskId: string): { status: string } | null;
  findRunningCoderSessionsByAttemptEvidence(attempt: WorkItemAttempt, issueId: string): CoderSession[];
}

export class DatabaseAttemptEvidencePort implements WorkflowAttemptEvidencePort {
  private taskQueueRepo: IssueTaskQueueRepo;
  private coderSessionRepo: CoderSessionRepo;

  constructor(db: DatabaseManager) {
    this.taskQueueRepo = new IssueTaskQueueRepo(db);
    this.coderSessionRepo = new CoderSessionRepo(db);
  }

  hasActiveQueueTask(issueId: string): boolean {
    const running = this.taskQueueRepo.findRunningByIssueId(issueId);
    if (running) return true;
    const pending = this.taskQueueRepo.findPendingByIssueId(issueId);
    return pending.length > 0;
  }

  hasLiveProcess(session: CoderSession): boolean {
    if (!session.processPid) return false;
    try {
      process.kill(session.processPid, 0);
      return true;
    } catch {
      return false;
    }
  }

  findQueueTaskById(queueTaskId: string): { status: string } | null {
    const task = this.taskQueueRepo.findById(queueTaskId);
    return task ? { status: task.status } : null;
  }

  findRunningCoderSessionsByAttemptEvidence(attempt: WorkItemAttempt, issueId: string): CoderSession[] {
    if (!attempt.coderSessionId && !attempt.acpSessionId && !attempt.executionId && !attempt.processPid) return [];
    const sessions: CoderSession[] = [];
    if (attempt.coderSessionId) {
      const session = this.coderSessionRepo.findById(attempt.coderSessionId);
      if (session && (session.status === 'running' || session.status === 'probing')) {
        sessions.push(session);
      }
    }
    if (sessions.length === 0 && attempt.acpSessionId) {
      const issueSessions = this.coderSessionRepo.findByIssueId(issueId);
      for (const s of issueSessions) {
        if (s.acpSessionId === attempt.acpSessionId && (s.status === 'running' || s.status === 'probing')) {
          sessions.push(s);
        }
      }
    }
    if (sessions.length === 0 && (attempt.executionId || attempt.processPid)) {
      const issueSessions = this.coderSessionRepo.findByIssueId(issueId);
      for (const s of issueSessions) {
        const executionMatches = attempt.executionId && s.executionId === attempt.executionId;
        const processMatches = attempt.processPid && s.processPid === attempt.processPid;
        if ((executionMatches || processMatches) && (s.status === 'running' || s.status === 'probing')) {
          sessions.push(s);
        }
      }
    }
    return sessions;
  }
}

export class AttemptReconciliationService {
  private evidencePort: WorkflowAttemptEvidencePort;

  constructor(evidencePort: WorkflowAttemptEvidencePort) {
    this.evidencePort = evidencePort;
  }

  static fromDatabase(db: DatabaseManager): AttemptReconciliationService {
    return new AttemptReconciliationService(new DatabaseAttemptEvidencePort(db));
  }

  reconcileRunningAttempts(
    issueId: string,
    runningAttempts: WorkItemAttempt[],
  ): AttemptReconciliationResult {
    const result: AttemptReconciliationResult = {
      reconciled: false,
      interruptedCount: 0,
      reasons: [],
      interruptedAttempts: [],
    };

    if (runningAttempts.length === 0) return result;

    for (const attempt of runningAttempts) {
      if (this.isAttemptEvidenceLive(attempt, issueId)) continue;
      result.interruptedAttempts.push(attempt);
      result.interruptedCount += 1;
      const reason = 'agent-lost';
      result.reasons.push(reason);
    }

    if (result.interruptedAttempts.length === 0) {
      return result;
    }

    result.reconciled = true;

    log.info('Reconciliation: no live evidence, marking running attempts as interrupted', {
      issueId,
      count: result.interruptedAttempts.length,
    });

    return result;
  }

  private isAttemptEvidenceLive(attempt: WorkItemAttempt, issueId: string): boolean {
    if (!this.hasStrongAttemptSpecificEvidence(attempt) && this.evidencePort.hasActiveQueueTask(issueId)) {
      return true;
    }

    if (attempt.queueTaskId) {
      const queueTask = this.evidencePort.findQueueTaskById(attempt.queueTaskId);
      if (queueTask?.status === 'running' || queueTask?.status === 'pending') return true;
    }

    const sessions = this.evidencePort.findRunningCoderSessionsByAttemptEvidence(attempt, issueId);
    for (const session of sessions) {
      if (attempt.processPid && session.processPid === attempt.processPid && this.evidencePort.hasLiveProcess(session)) {
        return true;
      }
      if (!attempt.processPid && this.evidencePort.hasLiveProcess(session)) {
        return true;
      }
    }

    if (this.hasAttemptSpecificEvidence(attempt)) {
      return false;
    }

    return this.evidencePort.hasActiveQueueTask(issueId);
  }

  private hasStrongAttemptSpecificEvidence(attempt: WorkItemAttempt): boolean {
    return Boolean(
      attempt.queueTaskId
      || attempt.coderSessionId
      || attempt.acpSessionId
      || attempt.processPid,
    );
  }

  private hasAttemptSpecificEvidence(attempt: WorkItemAttempt): boolean {
    return Boolean(
      attempt.queueTaskId
      || attempt.coderSessionId
      || attempt.acpSessionId
      || attempt.executionId
      || attempt.processPid,
    );
  }
}
