import { Stage, isValidTransition, type Issue } from '../types';

export interface PlanResult {
  success: boolean;
  artifacts: {
    proposal: string;
    design: string;
    specs: Array<{ name: string; content: string }>;
    prd: unknown;
  };
  selfReviewNotes?: string;
  iterations: number;
}

export interface ReviewResult {
  passed: boolean;
  dimensions: Array<{
    name: string;
    passed: boolean;
    reasoning: string;
    issues?: Array<{
      severity: 'error' | 'warning';
      location: string;
      message: string;
    }>;
  }>;
  overallReasoning: string;
  fixSuggestions?: string[];
}

export interface PlannerAgent {
  plan(options: {
    issue: Issue;
    worktreePath: string;
    prompt?: string;
  }): Promise<PlanResult>;
}

export interface ReviewerAgent {
  review(options: {
    issue: Issue;
    worktreePath: string;
    prompt?: string;
  }): Promise<ReviewResult>;
}

export interface ChangeArtifactsManager {
  getChangeDir(issueNumber: number): string | null;
  readArtifact(changeDir: string, artifactPath: string): string | null;
  writeArtifact(changeDir: string, artifactPath: string, content: string): boolean;
  exists(changeDir: string): boolean;
}

export interface StageResult {
  success: boolean;
  requiresApproval: boolean;
  output: unknown;
  message?: string;
}

export interface WorkflowControllerOptions {
  plannerAgent: PlannerAgent;
  reviewerAgent: ReviewerAgent;
  artifactManager: ChangeArtifactsManager;
  worktreePath: string;
}

export class WorkflowController {
  private plannerAgent: PlannerAgent;
  private reviewerAgent: ReviewerAgent;
  private artifactManager: ChangeArtifactsManager;
  private worktreePath: string;

  constructor(options: WorkflowControllerOptions) {
    this.plannerAgent = options.plannerAgent;
    this.reviewerAgent = options.reviewerAgent;
    this.artifactManager = options.artifactManager;
    this.worktreePath = options.worktreePath;
  }

  validateTransition(from: Stage, to: Stage): boolean {
    return isValidTransition(from, to);
  }

  async executeStage(issue: Issue, stage: Stage): Promise<StageResult> {
    switch (stage) {
      case Stage.Plan:
        return this.executePlanStage(issue);
      case Stage.Build:
        return this.executeBuildStage(issue);
      case Stage.Review:
        return this.executeReviewStage(issue);
      case Stage.Explore:
        return this.executeExploreStage(issue);
      case Stage.Done:
        return this.executeDoneStage(issue);
      default:
        return {
          success: false,
          requiresApproval: false,
          output: null,
          message: `Unknown stage: ${stage}`,
        };
    }
  }

  private async executeExploreStage(issue: Issue): Promise<StageResult> {
    const changeDir = this.artifactManager.getChangeDir(issue.number);
    const hasExistingChange = changeDir && this.artifactManager.exists(changeDir);

    return {
      success: true,
      requiresApproval: false,
      output: {
        stage: Stage.Explore,
        issueNumber: issue.number,
        existingChangeDir: hasExistingChange ? changeDir : null,
      },
      message: 'Explore stage executed',
    };
  }

  private async executePlanStage(issue: Issue): Promise<StageResult> {
    try {
      const result = await this.plannerAgent.plan({
        issue,
        worktreePath: this.worktreePath,
      });

      return {
        success: result.success,
        requiresApproval: true,
        output: result,
        message: result.success
          ? 'Plan completed, awaiting user approval'
          : 'Plan failed',
      };
    } catch (error) {
      return {
        success: false,
        requiresApproval: false,
        output: null,
        message: error instanceof Error ? error.message : 'Plan execution failed',
      };
    }
  }

  private async executeBuildStage(issue: Issue): Promise<StageResult> {
    return {
      success: true,
      requiresApproval: false,
      output: { stage: Stage.Build, issueNumber: issue.number },
      message: 'Build stage framework ready - Coder Agent will be invoked via spawn_coder',
    };
  }

  private async executeReviewStage(issue: Issue): Promise<StageResult> {
    try {
      const result = await this.reviewerAgent.review({
        issue,
        worktreePath: this.worktreePath,
      });

      return {
        success: result.passed,
        requiresApproval: true,
        output: result,
        message: result.passed
          ? 'Review passed, awaiting user approval'
          : 'Review found issues',
      };
    } catch (error) {
      return {
        success: false,
        requiresApproval: false,
        output: null,
        message: error instanceof Error ? error.message : 'Review execution failed',
      };
    }
  }

  private async executeDoneStage(issue: Issue): Promise<StageResult> {
    return {
      success: true,
      requiresApproval: false,
      output: { stage: Stage.Done, issueNumber: issue.number },
      message: 'Issue completed',
    };
  }
}

export function createWorkflowController(options: WorkflowControllerOptions): WorkflowController {
  return new WorkflowController(options);
}