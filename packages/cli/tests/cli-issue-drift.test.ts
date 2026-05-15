import { describe, it, expect, vi, beforeEach } from 'vitest';
import { Command } from 'commander';
vi.mock('../src/cli/api-client', () => ({ apiClient: vi.fn() }));
vi.mock('../src/cli/server-check', () => ({ requireServer: vi.fn().mockResolvedValue(undefined) }));

import { setupIssueCommands } from '../src/cli/commands/issue';
import { apiClient } from '../src/cli/api-client';

describe('CLI Issue Drift Rendering', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  describe('issue show renders drift state', () => {
    it('shows behind-base status and rebase decision for a drifted issue', async () => {
      const mockedApiClient = vi.mocked(apiClient);
      mockedApiClient
        .mockResolvedValueOnce({
          success: true,
          data: {
            number: 1,
            title: 'Test Issue',
            priority: 'p2',
            stage: 'check',
            status: 'active',
            projectName: 'demo',
            baseBranch: 'main',
            labels: [],
            comments: [],
            drifted: true,
            decision: 'suggest',
            safeWindow: true,
            deferReason: null,
            staleEvidence: null,
            conflicts: null,
            nextAction: 'Rebase recommended; run "mo issue rebase main" when ready.',
            observedBaseSha: 'abc123',
            currentBaseSha: 'def456',
            candidateHeadSha: 'head-sha',
            mergeBaseSha: 'merge-base-sha',
          },
        } as any)
        .mockResolvedValueOnce({ success: true, data: [] } as any)
        .mockResolvedValueOnce({ success: true, data: [] } as any);

      const logSpy = vi.spyOn(console, 'log').mockImplementation(() => {});
      const program = new Command();
      setupIssueCommands(program);

      await program.parseAsync(['node', 'test', 'issue', 'show', '1']);

      const output = logSpy.mock.calls.map(call => call.join(' ')).join('\n');
      expect(output).toContain('Base Drift Detected');
      expect(output).toContain('Rebase recommended');
    });

    it('shows defer reason when rebase is deferred', async () => {
      const mockedApiClient = vi.mocked(apiClient);
      mockedApiClient
        .mockResolvedValueOnce({
          success: true,
          data: {
            number: 2,
            title: 'Deferred Drift Issue',
            priority: 'p1',
            stage: 'build',
            status: 'active',
            projectName: 'demo',
            baseBranch: 'main',
            labels: [],
            comments: [],
            drifted: true,
            decision: 'defer',
            safeWindow: false,
            deferReason: 'task-running',
            staleEvidence: null,
            conflicts: null,
            nextAction: 'Rebase deferred until safe window (task-running).',
            observedBaseSha: 'old-sha',
            currentBaseSha: 'new-sha',
            candidateHeadSha: 'head-sha',
            mergeBaseSha: 'merge-base-sha',
          },
        } as any)
        .mockResolvedValueOnce({ success: true, data: [] } as any)
        .mockResolvedValueOnce({ success: true, data: [] } as any);

      const logSpy = vi.spyOn(console, 'log').mockImplementation(() => {});
      const program = new Command();
      setupIssueCommands(program);

      await program.parseAsync(['node', 'test', 'issue', 'show', '2']);

      const output = logSpy.mock.calls.map(call => call.join(' ')).join('\n');
      expect(output).toContain('Base Drift Detected');
      expect(output).toContain('Rebase deferred');
      expect(output).toContain('task-running');
    });

    it('shows conflict files when rebase had conflicts', async () => {
      const mockedApiClient = vi.mocked(apiClient);
      mockedApiClient
        .mockResolvedValueOnce({
          success: true,
          data: {
            number: 3,
            title: 'Conflicted Issue',
            priority: 'p1',
            stage: 'check',
            status: 'active',
            projectName: 'demo',
            baseBranch: 'main',
            labels: [],
            comments: [],
            drifted: true,
            decision: 'needs-attention',
            safeWindow: true,
            deferReason: null,
            staleEvidence: { review: false, mergeReady: true, approval: true },
            conflicts: ['src/foo.ts', 'src/bar.ts'],
            nextAction: 'Stale approval detected. Rebase or rerun checks before approving.',
            observedBaseSha: 'old-sha',
            currentBaseSha: 'new-sha',
            candidateHeadSha: 'head-sha',
            mergeBaseSha: 'merge-base-sha',
          },
        } as any)
        .mockResolvedValueOnce({ success: true, data: [] } as any)
        .mockResolvedValueOnce({ success: true, data: [] } as any);

      const logSpy = vi.spyOn(console, 'log').mockImplementation(() => {});
      const program = new Command();
      setupIssueCommands(program);

      await program.parseAsync(['node', 'test', 'issue', 'show', '3']);

      const output = logSpy.mock.calls.map(call => call.join(' ')).join('\n');
      expect(output).toContain('Base Drift Detected');
      expect(output).toContain('src/foo.ts');
      expect(output).toContain('src/bar.ts');
      expect(output).toContain('Stale approval detected');
    });

    it('shows needs-attention decision with stale approval guidance', async () => {
      const mockedApiClient = vi.mocked(apiClient);
      mockedApiClient
        .mockResolvedValueOnce({
          success: true,
          data: {
            number: 4,
            title: 'Needs Attention Issue',
            priority: 'p0',
            stage: 'check',
            status: 'active',
            projectName: 'demo',
            baseBranch: 'main',
            labels: [],
            comments: [],
            drifted: true,
            decision: 'needs-attention',
            safeWindow: true,
            deferReason: null,
            staleEvidence: { review: true, mergeReady: true, approval: true },
            conflicts: null,
            nextAction: 'Stale approval detected. Rebase or rerun checks before approving.',
            observedBaseSha: 'old-sha',
            currentBaseSha: 'new-sha',
            candidateHeadSha: 'head-sha',
            mergeBaseSha: 'merge-base-sha',
          },
        } as any)
        .mockResolvedValueOnce({ success: true, data: [] } as any)
        .mockResolvedValueOnce({ success: true, data: [] } as any);

      const logSpy = vi.spyOn(console, 'log').mockImplementation(() => {});
      const program = new Command();
      setupIssueCommands(program);

      await program.parseAsync(['node', 'test', 'issue', 'show', '4']);

      const output = logSpy.mock.calls.map(call => call.join(' ')).join('\n');
      expect(output).toContain('Needs attention');
      expect(output).toContain('Stale approval detected');
    });
  });

  describe('issue show does not render stale approval as actionable', () => {
    it('marks approval as STALE and suppresses self-review notes when stale', async () => {
      const mockedApiClient = vi.mocked(apiClient);
      mockedApiClient
        .mockResolvedValueOnce({
          success: true,
          data: {
            number: 5,
            title: 'Stale Approval Issue',
            priority: 'p2',
            stage: 'check',
            status: 'active',
            projectName: 'demo',
            baseBranch: 'main',
            labels: [],
            comments: [],
            approvalState: {
              status: 'awaiting',
              stage: 'check',
              requestedAt: '2026-05-01T00:00:00Z',
              respondedAt: null,
              output: 'All checks passed, ready for approval',
            },
            drifted: true,
            decision: 'suggest',
            safeWindow: true,
            deferReason: null,
            staleEvidence: { review: false, mergeReady: true, approval: true },
            conflicts: null,
            nextAction: 'Stale approval detected. Rebase or rerun checks before approving.',
            observedBaseSha: 'abc',
            currentBaseSha: 'def',
            candidateHeadSha: 'head',
            mergeBaseSha: 'mb',
          },
        } as any)
        .mockResolvedValueOnce({ success: true, data: [] } as any)
        .mockResolvedValueOnce({ success: true, data: [] } as any);

      const logSpy = vi.spyOn(console, 'log').mockImplementation(() => {});
      const program = new Command();
      setupIssueCommands(program);

      await program.parseAsync(['node', 'test', 'issue', 'show', '5']);

      const output = logSpy.mock.calls.map(call => call.join(' ')).join('\n');
      expect(output).toContain('STALE');
      expect(output).toContain('Approval evidence is stale');
      expect(output).not.toContain('Self-review notes:');
      expect(output).not.toContain('All checks passed');
    });

    it('renders non-stale approval normally', async () => {
      const mockedApiClient = vi.mocked(apiClient);
      mockedApiClient
        .mockResolvedValueOnce({
          success: true,
          data: {
            number: 6,
            title: 'Fresh Approval Issue',
            priority: 'p2',
            stage: 'check',
            status: 'active',
            projectName: 'demo',
            baseBranch: 'main',
            labels: [],
            comments: [],
            approvalState: {
              status: 'awaiting',
              stage: 'check',
              requestedAt: '2026-05-01T00:00:00Z',
              respondedAt: null,
              output: 'All checks passed, ready for approval',
            },
            drifted: false,
            decision: null,
            safeWindow: null,
            deferReason: null,
            staleEvidence: null,
            conflicts: null,
            nextAction: null,
            observedBaseSha: null,
            currentBaseSha: null,
            candidateHeadSha: null,
            mergeBaseSha: null,
          },
        } as any)
        .mockResolvedValueOnce({ success: true, data: [] } as any)
        .mockResolvedValueOnce({ success: true, data: [] } as any);

      const logSpy = vi.spyOn(console, 'log').mockImplementation(() => {});
      const program = new Command();
      setupIssueCommands(program);

      await program.parseAsync(['node', 'test', 'issue', 'show', '6']);

      const output = logSpy.mock.calls.map(call => call.join(' ')).join('\n');
      expect(output).toContain('Approval: awaiting');
      expect(output).toContain('Self-review notes:');
    });
  });

  describe('issue show skip decision', () => {
    it('shows aligned status when not drifted', async () => {
      const mockedApiClient = vi.mocked(apiClient);
      mockedApiClient
        .mockResolvedValueOnce({
          success: true,
          data: {
            number: 7,
            title: 'Aligned Issue',
            priority: 'p2',
            stage: 'build',
            status: 'active',
            projectName: 'demo',
            baseBranch: 'main',
            labels: [],
            comments: [],
            drifted: false,
            decision: 'skip',
            safeWindow: true,
            deferReason: null,
            staleEvidence: null,
            conflicts: null,
            nextAction: 'Candidate is aligned with current base.',
            observedBaseSha: 'abc',
            currentBaseSha: 'abc',
            candidateHeadSha: 'head',
            mergeBaseSha: 'mb',
          },
        } as any)
        .mockResolvedValueOnce({ success: true, data: [] } as any)
        .mockResolvedValueOnce({ success: true, data: [] } as any);

      const logSpy = vi.spyOn(console, 'log').mockImplementation(() => {});
      const program = new Command();
      setupIssueCommands(program);

      await program.parseAsync(['node', 'test', 'issue', 'show', '7']);

      const output = logSpy.mock.calls.map(call => call.join(' ')).join('\n');
      expect(output).not.toContain('Base Drift Detected');
      expect(output).not.toContain('Aligned with base');
    });
  });

  describe('issue show defer reason labels', () => {
    it('renders all defer reason types', async () => {
      const deferReasons: Array<{ reason: string; label: string }> = [
        { reason: 'agent-running', label: 'Agent is currently running' },
        { reason: 'task-running', label: 'A task is currently executing' },
        { reason: 'waiting-for-task-boundary', label: 'Waiting for task boundary' },
        { reason: 'rebase-already-pending', label: 'Rebase is already pending' },
      ];

      for (const { reason, label } of deferReasons) {
        vi.clearAllMocks();
        const mockedApiClient = vi.mocked(apiClient);
        mockedApiClient
          .mockResolvedValueOnce({
            success: true,
            data: {
              number: 10,
              title: 'Defer Reason Test',
              priority: 'p2',
              stage: 'build',
              status: 'active',
              projectName: 'demo',
              baseBranch: 'main',
              labels: [],
              comments: [],
              drifted: true,
              decision: 'defer',
              safeWindow: false,
              deferReason: reason,
              staleEvidence: null,
              conflicts: null,
              nextAction: `Rebase deferred until safe window (${reason}).`,
              observedBaseSha: 'old',
              currentBaseSha: 'new',
              candidateHeadSha: 'head',
              mergeBaseSha: 'mb',
            },
          } as any)
          .mockResolvedValueOnce({ success: true, data: [] } as any)
          .mockResolvedValueOnce({ success: true, data: [] } as any);

        const logSpy = vi.spyOn(console, 'log').mockImplementation(() => {});
        const program = new Command();
        setupIssueCommands(program);

        await program.parseAsync(['node', 'test', 'issue', 'show', '10']);

        const output = logSpy.mock.calls.map(call => call.join(' ')).join('\n');
        expect(output).toContain(label);
      }
    });
  });
});