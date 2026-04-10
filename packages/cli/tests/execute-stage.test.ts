import { describe, it, expect, beforeEach, afterEach } from 'vitest';
import * as fs from 'fs';
import * as path from 'path';
import * as os from 'os';
import { createExecuteStageTool, type ExecuteStageContext } from '../src/tools/execute-stage';
import type { WorkflowController, Issue, Stage } from '../src/types';

describe('createExecuteStageTool', () => {
  let tmpDir: string;
  let mockIssue: Issue;

  beforeEach(() => {
    tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-test-'));
    mockIssue = {
      id: 1,
      number: 1,
      title: 'Test Issue',
      description: 'Test description',
      status: 'in_progress',
    } as Issue;
  });

  afterEach(() => {
    fs.rmSync(tmpDir, { recursive: true, force: true });
  });

  function createMockWorkflowController(executeStageResult: any): WorkflowController {
    return {
      executeStage: async (issue: Issue, stage: Stage) => executeStageResult,
    } as unknown as WorkflowController;
  }

  async function executeTool(context: ExecuteStageContext, params: { stage: string }) {
    const tool = createExecuteStageTool(context);
    const parsed = tool.definition.parameters.safeParse(params);
    if (!parsed.success) {
      return `Validation error: ${parsed.error.issues.map((i) => i.message).join(', ')}`;
    }
    return tool.definition.execute(parsed.data);
  }

  describe('stage execution', () => {
    it('should execute build stage successfully', async () => {
      const mockController = createMockWorkflowController({
        success: true,
        requiresApproval: false,
        output: { tasks: [] },
        message: 'Build complete',
      });
      const context: ExecuteStageContext = {
        workflowController: mockController,
        issue: mockIssue,
      };
      const result = await executeTool(context, { stage: 'build' });
      expect(result).toContain('"success": true');
      expect(result).toContain('"requiresApproval": false');
    });

    it('should return requiresApproval when stage requires user approval', async () => {
      const mockController = createMockWorkflowController({
        success: true,
        requiresApproval: true,
        output: { summary: 'Plan summary' },
        message: 'Plan complete, awaiting approval',
      });
      const context: ExecuteStageContext = {
        workflowController: mockController,
        issue: mockIssue,
        issueRepo: { setApprovalState: () => {} },
      };
      const result = await executeTool(context, { stage: 'plan' });
      expect(result).toContain('"requiresApproval": true');
      expect(result).toContain('"status": "awaiting_approval"');
    });

    it('should handle stage execution errors', async () => {
      const mockController = createMockWorkflowController({
        success: false,
        requiresApproval: false,
        output: null,
        message: 'Stage failed',
      });
      const context: ExecuteStageContext = {
        workflowController: mockController,
        issue: mockIssue,
      };
      const result = await executeTool(context, { stage: 'review' });
      expect(result).toContain('"success": false');
    });
  });

  describe('parameter validation', () => {
    it('should accept valid stage values', () => {
      const mockController = createMockWorkflowController({ success: true, requiresApproval: false });
      const context: ExecuteStageContext = {
        workflowController: mockController,
        issue: mockIssue,
      };
      const tool = createExecuteStageTool(context);
      const stages = ['explore', 'plan', 'build', 'review', 'done'];
      for (const stage of stages) {
        const result = tool.definition.parameters.safeParse({ stage });
        expect(result.success).toBe(true);
      }
    });

    it('should reject invalid stage value', () => {
      const mockController = createMockWorkflowController({ success: true, requiresApproval: false });
      const context: ExecuteStageContext = {
        workflowController: mockController,
        issue: mockIssue,
      };
      const tool = createExecuteStageTool(context);
      const result = tool.definition.parameters.safeParse({ stage: 'invalid' });
      expect(result.success).toBe(false);
    });

    it('should reject extra parameters', () => {
      const mockController = createMockWorkflowController({ success: true, requiresApproval: false });
      const context: ExecuteStageContext = {
        workflowController: mockController,
        issue: mockIssue,
      };
      const tool = createExecuteStageTool(context);
      const result = tool.definition.parameters.safeParse({
        stage: 'build',
        extra: 'not allowed',
      });
      expect(result.success).toBe(false);
    });
  });
});