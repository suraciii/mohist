import * as fs from 'fs';
import * as path from 'path';
import { runAcpSession, type AcpSessionOptions, type AcpSessionResult } from '../agent-runtime/acp-session';
import { buildExplorePrompt } from '../agents/artifact-prompt';
import { ChangeArtifactsManager } from '../artifacts/change-artifacts-manager';
import { IssueService } from './issue-service';
import type { Issue } from '../types';
import { loadAgentConfig } from '../workflow/workflow-loader';
import { Log } from '../util/log';

const log = Log.create({ service: 'explore-acp' });

export interface ExploreAcpServiceOptions {
  worktreePath: string;
  issueService: IssueService;
  artifactManager: ChangeArtifactsManager;
}

export interface ExploreResult {
  success: boolean;
  issueNumber: number;
  proposalPath: string | null;
  error?: string;
}

type ExploreAcpOptions = Omit<AcpSessionOptions, 'cwd' | 'task'>;

export class ExploreAcpService {
  private worktreePath: string;
  private issueService: IssueService;
  private artifactManager: ChangeArtifactsManager;

  constructor(options: ExploreAcpServiceOptions) {
    this.worktreePath = options.worktreePath;
    this.issueService = options.issueService;
    this.artifactManager = options.artifactManager;
  }

  async run(
    issueTitle: string,
    projectId: string,
    acpOptions?: ExploreAcpOptions,
  ): Promise<ExploreResult> {
    const issue = this.issueService.create({
      projectId,
      title: issueTitle,
    });

    log.info('Created issue for exploration', { issueNumber: issue.number, title: issueTitle });

    const changeDir = this.artifactManager.createChangeDir(issue.number, issueTitle);

    const prompt = buildExplorePrompt(
      { title: issueTitle, number: issue.number },
      changeDir,
      null,
      loadAgentConfig(this.worktreePath),
    );

    const result = await runAcpSession({
      ...acpOptions,
      cwd: this.worktreePath,
      task: prompt,
      issueId: issue.id,
      projectId,
    });

    return this.buildResult(result, issue.number, changeDir);
  }

  async runOnIssue(
    issue: Issue,
    acpOptions?: ExploreAcpOptions,
  ): Promise<ExploreResult> {
    let changeDir = this.artifactManager.getChangeDir(issue.number);
    if (!changeDir) {
      changeDir = this.artifactManager.createChangeDir(issue.number, issue.title);
    }

    const existingProposal = this.artifactManager.readProposal(issue.number);

    log.info('Running exploration on existing issue', { issueNumber: issue.number });

    const prompt = buildExplorePrompt(
      { title: issue.title, body: issue.body, number: issue.number },
      changeDir,
      existingProposal,
      loadAgentConfig(this.worktreePath),
    );

    const result = await runAcpSession({
      ...acpOptions,
      cwd: this.worktreePath,
      task: prompt,
      issueId: issue.id,
      projectId: issue.projectId,
    });

    return this.buildResult(result, issue.number, changeDir);
  }

  private buildResult(
    result: AcpSessionResult,
    issueNumber: number,
    changeDir: string,
  ): ExploreResult {
    const proposalPath = path.join(changeDir, 'proposal.md');

    if (!result.success) {
      return {
        success: false,
        issueNumber,
        proposalPath: null,
        error: result.error ?? 'ACP session failed',
      };
    }

    if (!fs.existsSync(proposalPath)) {
      return {
        success: false,
        issueNumber,
        proposalPath: null,
        error: 'Agent did not create proposal.md',
      };
    }

    return {
      success: true,
      issueNumber,
      proposalPath,
    };
  }
}
