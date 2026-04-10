import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import * as fs from 'fs';
import * as path from 'path';
import * as os from 'os';
import { createSubmitApprovalTool, type SubmitApprovalContext } from '../src/tools/submit-approval';
import type { IssueRepo, Issue, ApprovalState } from '../src/types';

describe('createSubmitApprovalTool', () => {
  let tmpDir: string;
  let mockIssueRepo: any;

  beforeEach(() => {
    tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-test-'));
    mockIssueRepo = {
      findById: vi.fn(),
      setApprovalState: vi.fn(),
    };
  });

  afterEach(() => {
    fs.rmSync(tmpDir, { recursive: true, force: true });
    vi.restoreAllMocks();
  });

  async function executeTool(context: SubmitApprovalContext, params: { decision: 'approve' | 'request_changes' | 'abort'; comment?: string }) {
    const tool = createSubmitApprovalTool(context);
    const parsed = tool.definition.parameters.safeParse(params);
    if (!parsed.success) {
      return `Validation error: ${parsed.error.issues.map((i) => i.message).join(', ')}`;
    }
    return tool.definition.execute(parsed.data);
  }

  function parseResult(result: string) {
    return JSON.parse(result);
  }

  describe('approval state transitions', () => {
    it('should return error when issue not found', async () => {
      mockIssueRepo.findById.mockReturnValue(null);
      const context: SubmitApprovalContext = {
        issueRepo: mockIssueRepo,
        issue: { id: 999, number: 999, title: 'Test', description: 'Test', status: 'in_progress' } as Issue,
      };
      const result = await executeTool(context, { decision: 'approve' });
      expect(result).toContain('Issue not found');
    });

    it('should return error when no pending approval', async () => {
      mockIssueRepo.findById.mockReturnValue({
        id: 1,
        number: 1,
        title: 'Test',
        description: 'Test',
        status: 'in_progress',
        approvalState: undefined,
      });
      const context: SubmitApprovalContext = {
        issueRepo: mockIssueRepo,
        issue: { id: 1, number: 1, title: 'Test', description: 'Test', status: 'in_progress' } as Issue,
      };
      const result = await executeTool(context, { decision: 'approve' });
      expect(result).toContain('No pending approval');
    });

    it('should return error when approval status is not awaiting', async () => {
      mockIssueRepo.findById.mockReturnValue({
        id: 1,
        number: 1,
        title: 'Test',
        description: 'Test',
        status: 'in_progress',
        approvalState: {
          stage: 'plan',
          status: 'approved' as const,
          output: {},
          requestedAt: new Date().toISOString(),
        },
      });
      const context: SubmitApprovalContext = {
        issueRepo: mockIssueRepo,
        issue: { id: 1, number: 1, title: 'Test', description: 'Test', status: 'in_progress' } as Issue,
      };
      const result = await executeTool(context, { decision: 'approve' });
      expect(result).toContain('No pending approval');
    });
  });

  describe('approve decision', () => {
    it('should return success with advance_stage action', async () => {
      mockIssueRepo.findById.mockReturnValue({
        id: 1,
        number: 1,
        title: 'Test',
        description: 'Test',
        status: 'in_progress',
        approvalState: {
          stage: 'plan',
          status: 'awaiting' as const,
          output: { summary: 'Plan output' },
          requestedAt: new Date().toISOString(),
        },
      });
      const context: SubmitApprovalContext = {
        issueRepo: mockIssueRepo,
        issue: { id: 1, number: 1, title: 'Test', description: 'Test', status: 'in_progress' } as Issue,
      };
      const result = await executeTool(context, { decision: 'approve' });
      const parsed = parseResult(result);
      expect(parsed.success).toBe(true);
      expect(parsed.decision).toBe('approved');
      expect(parsed.nextAction).toBe('advance_stage');
    });

    it('should include optional comment', async () => {
      mockIssueRepo.findById.mockReturnValue({
        id: 1,
        number: 1,
        title: 'Test',
        description: 'Test',
        status: 'in_progress',
        approvalState: {
          stage: 'plan',
          status: 'awaiting' as const,
          output: { summary: 'Plan output' },
          requestedAt: new Date().toISOString(),
        },
      });
      const context: SubmitApprovalContext = {
        issueRepo: mockIssueRepo,
        issue: { id: 1, number: 1, title: 'Test', description: 'Test', status: 'in_progress' } as Issue,
      };
      const result = await executeTool(context, { decision: 'approve', comment: 'Looks good!' });
      const parsed = parseResult(result);
      expect(parsed.success).toBe(true);
    });

    it('should set approval state to approved', async () => {
      mockIssueRepo.findById.mockReturnValue({
        id: 1,
        number: 1,
        title: 'Test',
        description: 'Test',
        status: 'in_progress',
        approvalState: {
          stage: 'plan',
          status: 'awaiting' as const,
          output: { summary: 'Plan output' },
          requestedAt: new Date().toISOString(),
        },
      });
      const context: SubmitApprovalContext = {
        issueRepo: mockIssueRepo,
        issue: { id: 1, number: 1, title: 'Test', description: 'Test', status: 'in_progress' } as Issue,
      };
      await executeTool(context, { decision: 'approve' });
      expect(mockIssueRepo.setApprovalState).toHaveBeenCalledWith(1, expect.objectContaining({
        status: 'approved',
        respondedAt: expect.any(String),
      }));
    });
  });

  describe('request_changes decision', () => {
    it('should return success with retry_stage action', async () => {
      mockIssueRepo.findById.mockReturnValue({
        id: 1,
        number: 1,
        title: 'Test',
        description: 'Test',
        status: 'in_progress',
        approvalState: {
          stage: 'plan',
          status: 'awaiting' as const,
          output: { summary: 'Plan output' },
          requestedAt: new Date().toISOString(),
        },
      });
      const context: SubmitApprovalContext = {
        issueRepo: mockIssueRepo,
        issue: { id: 1, number: 1, title: 'Test', description: 'Test', status: 'in_progress' } as Issue,
      };
      const result = await executeTool(context, { decision: 'request_changes' });
      const parsed = parseResult(result);
      expect(parsed.success).toBe(true);
      expect(parsed.decision).toBe('changes_requested');
      expect(parsed.nextAction).toBe('retry_stage');
    });

    it('should set approval state to pending for retry', async () => {
      mockIssueRepo.findById.mockReturnValue({
        id: 1,
        number: 1,
        title: 'Test',
        description: 'Test',
        status: 'in_progress',
        approvalState: {
          stage: 'plan',
          status: 'awaiting' as const,
          output: { summary: 'Plan output' },
          requestedAt: new Date().toISOString(),
        },
      });
      const context: SubmitApprovalContext = {
        issueRepo: mockIssueRepo,
        issue: { id: 1, number: 1, title: 'Test', description: 'Test', status: 'in_progress' } as Issue,
      };
      await executeTool(context, { decision: 'request_changes' });
      expect(mockIssueRepo.setApprovalState).toHaveBeenCalledWith(1, expect.objectContaining({
        status: 'pending',
        respondedAt: expect.any(String),
      }));
    });
  });

  describe('abort decision', () => {
    it('should return success with abort action', async () => {
      mockIssueRepo.findById.mockReturnValue({
        id: 1,
        number: 1,
        title: 'Test',
        description: 'Test',
        status: 'in_progress',
        approvalState: {
          stage: 'plan',
          status: 'awaiting' as const,
          output: { summary: 'Plan output' },
          requestedAt: new Date().toISOString(),
        },
      });
      const context: SubmitApprovalContext = {
        issueRepo: mockIssueRepo,
        issue: { id: 1, number: 1, title: 'Test', description: 'Test', status: 'in_progress' } as Issue,
      };
      const result = await executeTool(context, { decision: 'abort' });
      const parsed = parseResult(result);
      expect(parsed.success).toBe(true);
      expect(parsed.decision).toBe('aborted');
      expect(parsed.nextAction).toBe('abort');
    });

    it('should set approval state to rejected', async () => {
      mockIssueRepo.findById.mockReturnValue({
        id: 1,
        number: 1,
        title: 'Test',
        description: 'Test',
        status: 'in_progress',
        approvalState: {
          stage: 'plan',
          status: 'awaiting' as const,
          output: { summary: 'Plan output' },
          requestedAt: new Date().toISOString(),
        },
      });
      const context: SubmitApprovalContext = {
        issueRepo: mockIssueRepo,
        issue: { id: 1, number: 1, title: 'Test', description: 'Test', status: 'in_progress' } as Issue,
      };
      await executeTool(context, { decision: 'abort' });
      expect(mockIssueRepo.setApprovalState).toHaveBeenCalledWith(1, expect.objectContaining({
        status: 'rejected',
        respondedAt: expect.any(String),
      }));
    });
  });

  describe('parameter validation', () => {
    it('should reject invalid decision value', () => {
      const context: SubmitApprovalContext = {
        issueRepo: mockIssueRepo,
        issue: { id: 1, number: 1, title: 'Test', description: 'Test', status: 'in_progress' } as Issue,
      };
      const tool = createSubmitApprovalTool(context);
      const result = tool.definition.parameters.safeParse({
        decision: 'invalid_decision',
      });
      expect(result.success).toBe(false);
    });

    it('should reject extra parameters', () => {
      const context: SubmitApprovalContext = {
        issueRepo: mockIssueRepo,
        issue: { id: 1, number: 1, title: 'Test', description: 'Test', status: 'in_progress' } as Issue,
      };
      const tool = createSubmitApprovalTool(context);
      const result = tool.definition.parameters.safeParse({
        decision: 'approve',
        extra: 'not allowed',
      });
      expect(result.success).toBe(false);
    });

    it('should accept all valid decision values', () => {
      const context: SubmitApprovalContext = {
        issueRepo: mockIssueRepo,
        issue: { id: 1, number: 1, title: 'Test', description: 'Test', status: 'in_progress' } as Issue,
      };
      const tool = createSubmitApprovalTool(context);
      const decisions = ['approve', 'request_changes', 'abort'] as const;
      for (const decision of decisions) {
        const result = tool.definition.parameters.safeParse({ decision });
        expect(result.success).toBe(true);
      }
    });
  });
});