import type { Issue } from '../types';
import { createAcpConnection, type AcpConnection, type AcpConnectionOptions } from '../agent-runtime/acp-session';
import { Log } from '../util/log';
import type { CheckpointManager } from './checkpoint-manager';

const log = Log.create({ service: 'acp-round-runner' });

export interface RoundConfig {
  type: string;
  label: string;
  outputPath: string;
  verifyArtifact: () => boolean;
  buildPrompt: (issue: Issue, changeDir: string) => string;
}

export interface AcpRoundRunnerOptions {
  issue: Issue;
  changeDir: string;
  rounds: RoundConfig[];
  acpOptions: AcpConnectionOptions;
  stage?: string;
  projectId?: string;
  eventBus?: import('../services/event-bus').EventBus;
  checkpointManager?: CheckpointManager;
}

export interface AcpRoundRunnerResult {
  success: boolean;
  output?: unknown;
  message?: string;
  completedRounds?: string[];
}

export class AcpRoundRunner {
  private issue: Issue;
  private changeDir: string;
  private rounds: RoundConfig[];
  private acpOptions: AcpConnectionOptions;
  private stage: string;
  private projectId?: string;
  private eventBus?: import('../services/event-bus').EventBus;
  private checkpointManager?: CheckpointManager;

  constructor(options: AcpRoundRunnerOptions) {
    this.issue = options.issue;
    this.changeDir = options.changeDir;
    this.rounds = options.rounds;
    this.acpOptions = options.acpOptions;
    this.stage = options.stage ?? 'plan';
    this.projectId = options.projectId;
    this.eventBus = options.eventBus;
    this.checkpointManager = options.checkpointManager;
  }

  async execute(): Promise<AcpRoundRunnerResult> {
    const completedSteps: string[] = this.checkpointManager
      ? this.checkpointManager.getResumeSteps(this.issue.number, this.stage)
      : [];

    const isResuming = completedSteps.length > 0;

    if (isResuming) {
      log.info('AcpRoundRunner resuming from checkpoint', {
        issueNumber: this.issue.number,
        stage: this.stage,
        completedSteps,
      });
    }

    const roundState = { type: '', index: 0 };

    const connectionOptions: AcpConnectionOptions = {
      ...this.acpOptions,
      executionId: `${this.stage}-${this.issue.number}`,
      onSessionUpdate: (_notification) => {
        if (!this.eventBus) return;
        try {
          this.eventBus.emit('plan_session_update', {
            issueId: String(this.acpOptions.issueNumber ?? this.acpOptions.issueId ?? ''),
            projectId: this.projectId ?? this.issue.projectId,
            roundType: roundState.type,
            roundIndex: roundState.index,
            sessionUpdate: _notification.update.sessionUpdate,
            data: _notification.update as unknown,
          });
        } catch (e) {
          log.warn('eventBus.emit failed for plan_session_update', {
            error: e instanceof Error ? e.message : String(e),
          });
        }
      },
    };

    let conn: AcpConnection | undefined;

    try {
      conn = await createAcpConnection(connectionOptions);

      const executedRounds: string[] = [];

      for (const [index, round] of this.rounds.entries()) {
        roundState.type = round.type;
        roundState.index = index;

        if (completedSteps.includes(round.type)) {
          if (round.verifyArtifact()) {
            log.info('AcpRoundRunner round skipped (checkpoint + artifact exists)', {
              artifact: round.type,
              issueNumber: this.issue.number,
            });
            continue;
          }
          log.info('AcpRoundRunner round in checkpoint but artifact missing, re-running', {
            artifact: round.type,
            issueNumber: this.issue.number,
          });
          const idx = completedSteps.indexOf(round.type);
          completedSteps.splice(idx);
        } else if (!completedSteps.includes(round.type) && round.verifyArtifact()) {
          log.info('AcpRoundRunner artifact exists but not in checkpoint, marking complete', {
            artifact: round.type,
            issueNumber: this.issue.number,
          });
          completedSteps.push(round.type);
          this.checkpointManager?.markStepComplete(
            this.issue.number,
            this.stage,
            round.type,
            this.rounds[index + 1]?.type ?? null,
          );
          continue;
        }

        log.info('AcpRoundRunner round', { artifact: round.type, issueNumber: this.issue.number });

        this.emitRoundStart(round.type, index);

        const prompt = round.buildPrompt(this.issue, this.changeDir);
        const result = await conn.prompt(prompt);

        if (!result.success) {
          log.error('AcpRoundRunner round failed', { artifact: round.type, error: result.error });
          await conn.close();
          return {
            success: false,
            message: `Round "${round.label}" failed: ${result.error ?? 'unknown error'}`,
          };
        }

        if (!round.verifyArtifact()) {
          log.warn('AcpRoundRunner artifact not found after round, sending retry', {
            artifact: round.label,
            roundIndex: index,
          });

          const retryPrompt = [
            `The artifact file ${round.outputPath} was not found. You MUST create it now.`,
            '',
            `Use the write_file tool to write the ${round.type} artifact to:`,
            round.outputPath,
            '',
            'This is a retry. The pipeline cannot continue without this file.',
          ].join('\n');

          log.info('AcpRoundRunner retry prompt sent', { artifact: round.type, roundIndex: index });

          const retryResult = await conn.prompt(retryPrompt);

          if (!retryResult.success) {
            log.error('AcpRoundRunner retry prompt failed', { artifact: round.type, error: retryResult.error });
            await conn.close();
            return {
              success: false,
              message: `Round "${round.label}" retry failed: ${retryResult.error ?? 'unknown error'}`,
            };
          }

          if (!round.verifyArtifact()) {
            log.error('AcpRoundRunner artifact still missing after retry', { artifact: round.label });
            await conn.close();
            return {
              success: false,
              message: `Artifact "${round.label}" not found after retry`,
            };
          }

          log.info('AcpRoundRunner retry succeeded', { artifact: round.label });
        }

        completedSteps.push(round.type);
        this.checkpointManager?.markStepComplete(
          this.issue.number,
          this.stage,
          round.type,
          this.rounds[index + 1]?.type ?? null,
        );
        executedRounds.push(round.type);
      }

      await conn.close();

      return {
        success: true,
        completedRounds: executedRounds,
      };
    } catch (err) {
      if (conn) {
        try {
          await conn.close();
        } catch {
          // ignore cleanup errors
        }
      }
      return {
        success: false,
        message: `AcpRoundRunner error: ${err instanceof Error ? err.message : String(err)}`,
      };
    }
  }

  private emitRoundStart(roundType: string, roundIndex: number): void {
    if (!this.eventBus) return;
    try {
      this.eventBus.emit('plan_round_start', {
        issueId: String(this.acpOptions.issueNumber ?? this.acpOptions.issueId ?? ''),
        projectId: this.projectId ?? this.issue.projectId,
        roundType,
        roundLabel: roundType,
        roundIndex,
      });
    } catch (e) {
      log.warn('eventBus.emit failed for plan_round_start', {
        roundType,
        error: e instanceof Error ? e.message : String(e),
      });
    }
  }
}
