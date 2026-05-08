import { execFile } from 'child_process';
import { promisify } from 'util';
import { Stage, IssueStatus, MergeState, type Issue } from '../types';
import { loadHealthGatePolicies, type HealthGatePolicy } from '../workflow/workflow-loader';
import { loadWorkflow } from '../workflow/workflow-loader';
import { Log } from '../util/log';
import type { IssueRepo } from '../db/issue-repo';
import type { ProjectRepo } from '../db/project-repo';
import type { StageExecutionRepo } from '../db/stage-execution-repo';
import type { EventBus } from './event-bus';

const execFileAsync = promisify(execFile);
const log = Log.create({ service: 'post-merge-finalizer' });

const MAX_LOG_LENGTH = 50000;

function truncateLog(text: string, maxLength: number = MAX_LOG_LENGTH): string {
  if (text.length <= maxLength) return text;
  const half = Math.floor(maxLength / 2);
  return text.slice(0, half) + '\n\n...[truncated]...\n\n' + text.slice(-half);
}

export interface HealthGateResult {
  passed: boolean;
  enabled: boolean;
  command: string;
  timeout: number;
  duration: number;
  exitCode?: number;
  timedOut: boolean;
  summary: string;
  logExcerpt: string;
}

export interface FinalizationResult {
  success: boolean;
  healthGateResult?: HealthGateResult;
  error?: string;
}

export class PostMergeFinalizer {
  constructor(
    private readonly issueRepo: IssueRepo,
    private readonly projectRepo: ProjectRepo,
    private readonly stageExecutionRepo: StageExecutionRepo,
    private readonly eventBus: EventBus,
  ) {}

  async finalize(issue: Issue): Promise<FinalizationResult> {
    const project = this.projectRepo.findById(issue.projectId);
    if (!project) {
      return { success: false, error: 'Project not found' };
    }

    const workflow = loadWorkflow(project.path);
    if (typeof workflow === 'string') {
      return { success: false, error: 'Failed to load workflow config' };
    }

    const policies = loadHealthGatePolicies(workflow);
    const policy = policies.postMerge;

    if (!policy.enabled) {
      log.info('PostMerge health gate disabled, completing without verification', {
        issueNumber: issue.number,
      });
      const disabledResult: HealthGateResult = {
        passed: true,
        enabled: false,
        command: policy.command,
        timeout: policy.timeout,
        duration: 0,
        timedOut: false,
        summary: 'PostMerge health gate disabled',
        logExcerpt: '',
      };

      this.recordHealthGateResult(issue, disabledResult);
      this.completeIssue(issue);
      return {
        success: true,
        healthGateResult: disabledResult,
      };
    }

    const healthResult = await this.runHealthGate(project.path, policy);

    if (!healthResult.passed) {
      log.warn('PostMerge health gate failed, issue not completed', {
        issueNumber: issue.number,
        command: healthResult.command,
        summary: healthResult.summary,
      });

      this.recordHealthGateResult(issue, healthResult);

      return {
        success: false,
        healthGateResult: healthResult,
        error: `Post-merge health gate failed: ${healthResult.summary}`,
      };
    }

    this.recordHealthGateResult(issue, healthResult);
    this.completeIssue(issue);
    return { success: true, healthGateResult: healthResult };
  }

  private async runHealthGate(projectPath: string, policy: HealthGatePolicy): Promise<HealthGateResult> {
    const startTime = Date.now();
    const { command, timeout } = policy;

    try {
      const { stdout, stderr } = await execFileAsync(command, [], {
        cwd: projectPath,
        timeout,
        maxBuffer: 10 * 1024 * 1024,
        shell: true,
      });

      const duration = Date.now() - startTime;

      return {
        passed: true,
        enabled: true,
        command,
        timeout,
        duration,
        timedOut: false,
        summary: 'Post-merge health gate passed',
        logExcerpt: truncateLog(stdout + '\n' + stderr, 5000),
      };
    } catch (err: any) {
      const duration = Date.now() - startTime;
      const isTimeout = err.killed === true;
      const stderr = err.stderr || '';
      const stdout = err.stdout || '';

      let exitCode = err.code;
      if (typeof exitCode !== 'number' && err.message) {
        const match = err.message.match(/exit code (\d+)/);
        if (match) exitCode = parseInt(match[1], 10);
      }

      const summary = this.formatErrorMessage(command, stderr, stdout, exitCode, isTimeout);

      return {
        passed: false,
        enabled: true,
        command,
        timeout,
        duration,
        exitCode: typeof exitCode === 'number' ? exitCode : undefined,
        timedOut: isTimeout,
        summary,
        logExcerpt: truncateLog([stdout, stderr, err.message].filter(Boolean).join('\n'), 5000),
      };
    }
  }

  private formatErrorMessage(
    command: string,
    stderr: string,
    stdout: string,
    exitCode: number | undefined,
    isTimeout: boolean,
  ): string {
    if (isTimeout) {
      return `${command} — 超时`;
    }

    const combined = [stdout, stderr].filter(Boolean).join('\n');
    const lines = combined.split('\n');
    const errorLines: string[] = [];
    const errorPatterns = [/error/i, /fail/i, /cannot find/i, /not found/i, /unexpected/i, /syntax error/i];

    for (const line of lines) {
      if (errorPatterns.some(p => p.test(line))) {
        errorLines.push(line);
      }
      if (errorLines.length >= 15) break;
    }

    if (errorLines.length === 0) {
      const tail = lines.filter(l => l.trim()).slice(-15);
      errorLines.push(...tail);
    }

    const keyErrors = errorLines.join('\n');
    const parts: string[] = [];

    if (typeof exitCode === 'number') {
      parts.push(`${command} 失败 (exit code ${exitCode})`);
    } else {
      parts.push(`${command} 失败`);
    }

    if (keyErrors) {
      const oneLine = keyErrors.split('\n').filter(l => l.trim()).slice(0, 3).join(' | ');
      if (oneLine.length > 200) {
        parts.push(oneLine.slice(0, 200) + '...');
      } else {
        parts.push(oneLine);
      }
    }

    return parts.join(' — ');
  }

  private completeIssue(issue: Issue): void {
    this.issueRepo.updateStage(issue.id, Stage.Done);
    this.issueRepo.updateStatus(issue.id, IssueStatus.Completed);
    this.issueRepo.clearApprovalState(issue.id);
    this.issueRepo.setMergeState(issue.id, MergeState.Merged);
    this.issueRepo.updateBlockedReason(issue.id, null);

    this.eventBus.emit('agent_completed', {
      issueId: issue.id,
      projectId: issue.projectId,
      issueNumber: issue.number,
    });
  }

  private recordHealthGateResult(issue: Issue, result: HealthGateResult): void {
    if (!this.stageExecutionRepo) return;

    const execution = this.stageExecutionRepo.findActiveByIssueId(issue.id)
      ?? this.stageExecutionRepo.findByIssueId(issue.id).filter(e => e.stage === Stage.Check).at(-1)
      ?? this.stageExecutionRepo.create(issue.id, Stage.Check);

    if (execution) {
      const status = result.passed ? 'pass' : 'fail';
      const healthGateCheckResult = {
        name: 'health:postMerge',
        status,
        message: result.summary,
        duration: result.duration,
        summary: result.summary,
        output: {
          kind: 'health-gate',
          stage: 'postMerge',
          command: result.command,
          timeout: result.timeout,
          duration: result.duration,
          enabled: result.enabled,
          exitCode: result.exitCode,
          timedOut: result.timedOut,
          summary: result.summary,
          logExcerpt: result.logExcerpt,
        },
      };

      const existing = execution.checkResults as Array<unknown>;
      this.stageExecutionRepo.updateCheckResults(execution.id, [...existing, healthGateCheckResult]);
      this.stageExecutionRepo.updateStatus(execution.id, result.passed ? 'passed' : 'failed');
    }
  }
}
