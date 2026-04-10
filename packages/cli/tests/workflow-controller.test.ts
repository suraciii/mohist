import { describe, it, expect, beforeEach, vi } from 'vitest';
import { Stage, Issue, IssueStatus } from '../src/types';
import {
  WorkflowController,
  type PlannerAgent,
  type ReviewerAgent,
  type ChangeArtifactsManager,
  type PlanResult,
  type ReviewResult,
} from '../src/workflow/workflow-controller';

function createMockIssue(overrides: Partial<Issue> = {}): Issue {
  return {
    id: 'issue-1',
    number: 42,
    title: 'Test Issue',
    body: 'Test description',
    stage: Stage.Explore,
    status: IssueStatus.Active,
    projectId: 'project-1',
    labels: [],
    createdAt: new Date().toISOString(),
    updatedAt: new Date().toISOString(),
    ...overrides,
  };
}

function createMockPlannerAgent(planResult: PlanResult): PlannerAgent {
  return {
    plan: vi.fn().mockResolvedValue(planResult),
  };
}

function createMockReviewerAgent(reviewResult: ReviewResult): ReviewerAgent {
  return {
    review: vi.fn().mockResolvedValue(reviewResult),
  };
}

function createMockArtifactManager(): ChangeArtifactsManager {
  return {
    getChangeDir: vi.fn().mockReturnValue('/tmp/change-dir'),
    readArtifact: vi.fn().mockReturnValue(null),
    writeArtifact: vi.fn().mockReturnValue(true),
    exists: vi.fn().mockReturnValue(true),
    readPrd: vi.fn().mockReturnValue({ tasks: [] }),
    updateTaskStatus: vi.fn().mockReturnValue(true),
  };
}

describe('WorkflowController', () => {
  describe('validateTransition', () => {
    it('should return true for valid transition Explore -> Plan', () => {
      const controller = new WorkflowController({
        plannerAgent: createMockPlannerAgent({ success: true, artifacts: { proposal: '', design: '', specs: [], prd: null }, iterations: 1 }),
        reviewerAgent: createMockReviewerAgent({ passed: true, dimensions: [], overallReasoning: '' }),
        artifactManager: createMockArtifactManager(),
        worktreePath: '/tmp',
      });

      expect(controller.validateTransition(Stage.Explore, Stage.Plan)).toBe(true);
    });

    it('should return true for valid transition Plan -> Build', () => {
      const controller = new WorkflowController({
        plannerAgent: createMockPlannerAgent({ success: true, artifacts: { proposal: '', design: '', specs: [], prd: null }, iterations: 1 }),
        reviewerAgent: createMockReviewerAgent({ passed: true, dimensions: [], overallReasoning: '' }),
        artifactManager: createMockArtifactManager(),
        worktreePath: '/tmp',
      });

      expect(controller.validateTransition(Stage.Plan, Stage.Build)).toBe(true);
    });

    it('should return true for valid transition Build -> Review', () => {
      const controller = new WorkflowController({
        plannerAgent: createMockPlannerAgent({ success: true, artifacts: { proposal: '', design: '', specs: [], prd: null }, iterations: 1 }),
        reviewerAgent: createMockReviewerAgent({ passed: true, dimensions: [], overallReasoning: '' }),
        artifactManager: createMockArtifactManager(),
        worktreePath: '/tmp',
      });

      expect(controller.validateTransition(Stage.Build, Stage.Review)).toBe(true);
    });

    it('should return true for valid transition Review -> Done', () => {
      const controller = new WorkflowController({
        plannerAgent: createMockPlannerAgent({ success: true, artifacts: { proposal: '', design: '', specs: [], prd: null }, iterations: 1 }),
        reviewerAgent: createMockReviewerAgent({ passed: true, dimensions: [], overallReasoning: '' }),
        artifactManager: createMockArtifactManager(),
        worktreePath: '/tmp',
      });

      expect(controller.validateTransition(Stage.Review, Stage.Done)).toBe(true);
    });

    it('should return true for valid transition Review -> Build (regression)', () => {
      const controller = new WorkflowController({
        plannerAgent: createMockPlannerAgent({ success: true, artifacts: { proposal: '', design: '', specs: [], prd: null }, iterations: 1 }),
        reviewerAgent: createMockReviewerAgent({ passed: true, dimensions: [], overallReasoning: '' }),
        artifactManager: createMockArtifactManager(),
        worktreePath: '/tmp',
      });

      expect(controller.validateTransition(Stage.Review, Stage.Build)).toBe(true);
    });

    it('should return false for invalid transition Build -> Explore', () => {
      const controller = new WorkflowController({
        plannerAgent: createMockPlannerAgent({ success: true, artifacts: { proposal: '', design: '', specs: [], prd: null }, iterations: 1 }),
        reviewerAgent: createMockReviewerAgent({ passed: true, dimensions: [], overallReasoning: '' }),
        artifactManager: createMockArtifactManager(),
        worktreePath: '/tmp',
      });

      expect(controller.validateTransition(Stage.Build, Stage.Explore)).toBe(false);
    });

    it('should return false for invalid transition Done -> any', () => {
      const controller = new WorkflowController({
        plannerAgent: createMockPlannerAgent({ success: true, artifacts: { proposal: '', design: '', specs: [], prd: null }, iterations: 1 }),
        reviewerAgent: createMockReviewerAgent({ passed: true, dimensions: [], overallReasoning: '' }),
        artifactManager: createMockArtifactManager(),
        worktreePath: '/tmp',
      });

      expect(controller.validateTransition(Stage.Done, Stage.Explore)).toBe(false);
      expect(controller.validateTransition(Stage.Done, Stage.Plan)).toBe(false);
      expect(controller.validateTransition(Stage.Done, Stage.Build)).toBe(false);
      expect(controller.validateTransition(Stage.Done, Stage.Review)).toBe(false);
    });
  });

  describe('executeStage', () => {
    describe('Explore stage', () => {
      it('should execute Explore stage successfully', async () => {
        const controller = new WorkflowController({
          plannerAgent: createMockPlannerAgent({ success: true, artifacts: { proposal: '', design: '', specs: [], prd: null }, iterations: 1 }),
          reviewerAgent: createMockReviewerAgent({ passed: true, dimensions: [], overallReasoning: '' }),
          artifactManager: createMockArtifactManager(),
          worktreePath: '/tmp',
        });

        const issue = createMockIssue({ stage: Stage.Explore });
        const result = await controller.executeStage(issue, Stage.Explore);

        expect(result.success).toBe(true);
        expect(result.requiresApproval).toBe(false);
        expect(result.output).toMatchObject({
          stage: Stage.Explore,
          issueNumber: 42,
          existingChangeDir: '/tmp/change-dir',
        });
      });
    });

    describe('Plan stage', () => {
      it('should execute Plan stage and require approval on success', async () => {
        const planResult: PlanResult = {
          success: true,
          artifacts: {
            proposal: '# Proposal',
            design: '# Design',
            specs: [{ name: 'test', content: 'spec content' }],
            prd: { tasks: [] },
          },
          iterations: 1,
        };

        const controller = new WorkflowController({
          plannerAgent: createMockPlannerAgent(planResult),
          reviewerAgent: createMockReviewerAgent({ passed: true, dimensions: [], overallReasoning: '' }),
          artifactManager: createMockArtifactManager(),
          worktreePath: '/tmp',
        });

        const issue = createMockIssue({ stage: Stage.Plan });
        const result = await controller.executeStage(issue, Stage.Plan);

        expect(result.success).toBe(true);
        expect(result.requiresApproval).toBe(true);
        expect(result.output).toEqual(planResult);
        expect(result.message).toBe('Plan completed, awaiting user approval');
      });

      it('should execute Plan stage and handle failure', async () => {
        const planResult: PlanResult = {
          success: false,
          artifacts: { proposal: '', design: '', specs: [], prd: null },
          iterations: 1,
        };

        const controller = new WorkflowController({
          plannerAgent: createMockPlannerAgent(planResult),
          reviewerAgent: createMockReviewerAgent({ passed: true, dimensions: [], overallReasoning: '' }),
          artifactManager: createMockArtifactManager(),
          worktreePath: '/tmp',
        });

        const issue = createMockIssue({ stage: Stage.Plan });
        const result = await controller.executeStage(issue, Stage.Plan);

        expect(result.success).toBe(false);
        expect(result.requiresApproval).toBe(true);
      });

      it('should handle Plan stage exception', async () => {
        const failingPlannerAgent: PlannerAgent = {
          plan: vi.fn().mockRejectedValue(new Error('Planner failed')),
        };

        const controller = new WorkflowController({
          plannerAgent: failingPlannerAgent,
          reviewerAgent: createMockReviewerAgent({ passed: true, dimensions: [], overallReasoning: '' }),
          artifactManager: createMockArtifactManager(),
          worktreePath: '/tmp',
        });

        const issue = createMockIssue({ stage: Stage.Plan });
        const result = await controller.executeStage(issue, Stage.Plan);

        expect(result.success).toBe(false);
        expect(result.requiresApproval).toBe(false);
        expect(result.message).toBe('Planner failed');
      });
    });

    describe('Build stage', () => {
      it('should execute Build stage successfully', async () => {
        const controller = new WorkflowController({
          plannerAgent: createMockPlannerAgent({ success: true, artifacts: { proposal: '', design: '', specs: [], prd: null }, iterations: 1 }),
          reviewerAgent: createMockReviewerAgent({ passed: true, dimensions: [], overallReasoning: '' }),
          artifactManager: createMockArtifactManager(),
          worktreePath: '/tmp',
        });

        const issue = createMockIssue({ stage: Stage.Build });
        const result = await controller.executeStage(issue, Stage.Build);

        expect(result.success).toBe(true);
        expect(result.requiresApproval).toBe(false);
        expect(result.message).toContain('Build phase completed');
      });
    });

    describe('Review stage', () => {
      it('should execute Review stage and require approval on pass', async () => {
        const reviewResult: ReviewResult = {
          passed: true,
          dimensions: [
            { name: 'correctness', passed: true, reasoning: 'Looks good' },
          ],
          overallReasoning: 'All checks passed',
        };

        const controller = new WorkflowController({
          plannerAgent: createMockPlannerAgent({ success: true, artifacts: { proposal: '', design: '', specs: [], prd: null }, iterations: 1 }),
          reviewerAgent: createMockReviewerAgent(reviewResult),
          artifactManager: createMockArtifactManager(),
          worktreePath: '/tmp',
        });

        const issue = createMockIssue({ stage: Stage.Review });
        const result = await controller.executeStage(issue, Stage.Review);

        expect(result.success).toBe(true);
        expect(result.requiresApproval).toBe(true);
        expect(result.output).toEqual(reviewResult);
        expect(result.message).toBe('Review passed, awaiting user approval');
      });

      it('should execute Review stage and indicate failure', async () => {
        const reviewResult: ReviewResult = {
          passed: false,
          dimensions: [
            { name: 'correctness', passed: false, reasoning: 'Found issues', issues: [{ severity: 'error', location: 'src/foo.ts', message: 'Type error' }] },
          ],
          overallReasoning: 'Found issues that need fixing',
          fixSuggestions: ['Fix the type error in src/foo.ts'],
        };

        const controller = new WorkflowController({
          plannerAgent: createMockPlannerAgent({ success: true, artifacts: { proposal: '', design: '', specs: [], prd: null }, iterations: 1 }),
          reviewerAgent: createMockReviewerAgent(reviewResult),
          artifactManager: createMockArtifactManager(),
          worktreePath: '/tmp',
        });

        const issue = createMockIssue({ stage: Stage.Review });
        const result = await controller.executeStage(issue, Stage.Review);

        expect(result.success).toBe(false);
        expect(result.requiresApproval).toBe(true);
        expect(result.message).toBe('Review found issues');
      });

      it('should handle Review stage exception', async () => {
        const failingReviewerAgent: ReviewerAgent = {
          review: vi.fn().mockRejectedValue(new Error('Reviewer failed')),
        };

        const controller = new WorkflowController({
          plannerAgent: createMockPlannerAgent({ success: true, artifacts: { proposal: '', design: '', specs: [], prd: null }, iterations: 1 }),
          reviewerAgent: failingReviewerAgent,
          artifactManager: createMockArtifactManager(),
          worktreePath: '/tmp',
        });

        const issue = createMockIssue({ stage: Stage.Review });
        const result = await controller.executeStage(issue, Stage.Review);

        expect(result.success).toBe(false);
        expect(result.requiresApproval).toBe(false);
        expect(result.message).toBe('Reviewer failed');
      });
    });

    describe('Done stage', () => {
      it('should execute Done stage successfully', async () => {
        const controller = new WorkflowController({
          plannerAgent: createMockPlannerAgent({ success: true, artifacts: { proposal: '', design: '', specs: [], prd: null }, iterations: 1 }),
          reviewerAgent: createMockReviewerAgent({ passed: true, dimensions: [], overallReasoning: '' }),
          artifactManager: createMockArtifactManager(),
          worktreePath: '/tmp',
        });

        const issue = createMockIssue({ stage: Stage.Done });
        const result = await controller.executeStage(issue, Stage.Done);

        expect(result.success).toBe(true);
        expect(result.requiresApproval).toBe(false);
        expect(result.output).toEqual({ stage: Stage.Done, issueNumber: 42 });
      });
    });

    describe('unknown stage', () => {
      it('should return error for unknown stage', async () => {
        const controller = new WorkflowController({
          plannerAgent: createMockPlannerAgent({ success: true, artifacts: { proposal: '', design: '', specs: [], prd: null }, iterations: 1 }),
          reviewerAgent: createMockReviewerAgent({ passed: true, dimensions: [], overallReasoning: '' }),
          artifactManager: createMockArtifactManager(),
          worktreePath: '/tmp',
        });

        const issue = createMockIssue();
        const result = await controller.executeStage(issue, 'unknown' as Stage);

        expect(result.success).toBe(false);
        expect(result.requiresApproval).toBe(false);
        expect(result.message).toBe('Unknown stage: unknown');
      });
    });
  });
});