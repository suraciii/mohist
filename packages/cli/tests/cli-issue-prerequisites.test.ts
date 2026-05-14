import { describe, it, expect, vi, beforeEach } from 'vitest';
import { Command } from 'commander';
vi.mock('../src/cli/api-client', () => ({ apiClient: vi.fn() }));
vi.mock('../src/cli/server-check', () => ({ requireServer: vi.fn().mockResolvedValue(undefined) }));

import { setupIssueCommands } from '../src/cli/commands/issue';
import { apiClient } from '../src/cli/api-client';

describe('CLI Issue Prerequisites', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  describe('mo issue list - waiting-for-delivery rendering', () => {
    it('should render waiting reason when issue has waiting prerequisites', async () => {
      const mockedApiClient = vi.mocked(apiClient);
      mockedApiClient.mockResolvedValueOnce({
        success: true,
        data: [
          {
            number: 201,
            title: 'Issue #201',
            priority: 'p2',
            stage: 'backlog',
            status: 'active',
            projectName: 'test',
            baseBranch: 'main',
            labels: [],
            comments: [],
            startEligibility: {
              startable: false,
              reason: 'waiting-for-delivery',
              waitingForDelivery: [
                {
                  number: 200,
                  title: 'Issue #200',
                  delivered: false,
                  stage: 'backlog',
                  status: 'active',
                },
              ],
            },
          },
        ],
      } as any);

      const logSpy = vi.spyOn(console, 'log').mockImplementation(() => {});
      const program = new Command();
      setupIssueCommands(program);

      await program.parseAsync(['node', 'test', 'issue', 'list']);

      const output = logSpy.mock.calls.map(call => call.join(' ')).join('\n');
      expect(output).toContain('Waiting for #200');
    });

    it('should not render waiting reason when issue is startable', async () => {
      const mockedApiClient = vi.mocked(apiClient);
      mockedApiClient.mockResolvedValueOnce({
        success: true,
        data: [
          {
            number: 201,
            title: 'Issue #201',
            priority: 'p2',
            stage: 'backlog',
            status: 'active',
            projectName: 'test',
            baseBranch: 'main',
            labels: [],
            comments: [],
            startEligibility: {
              startable: true,
              reason: 'ready',
              waitingForDelivery: [],
            },
          },
        ],
      } as any);

      const logSpy = vi.spyOn(console, 'log').mockImplementation(() => {});
      const program = new Command();
      setupIssueCommands(program);

      await program.parseAsync(['node', 'test', 'issue', 'list']);

      const output = logSpy.mock.calls.map(call => call.join(' ')).join('\n');
      expect(output).not.toContain('Waiting for');
    });

    it('should render multiple waiting prerequisites', async () => {
      const mockedApiClient = vi.mocked(apiClient);
      mockedApiClient.mockResolvedValueOnce({
        success: true,
        data: [
          {
            number: 201,
            title: 'Issue #201',
            priority: 'p2',
            stage: 'backlog',
            status: 'active',
            projectName: 'test',
            baseBranch: 'main',
            labels: [],
            comments: [],
            startEligibility: {
              startable: false,
              reason: 'waiting-for-delivery',
              waitingForDelivery: [
                {
                  number: 199,
                  title: 'Issue #199',
                  delivered: false,
                  stage: 'backlog',
                  status: 'active',
                },
                {
                  number: 200,
                  title: 'Issue #200',
                  delivered: false,
                  stage: 'backlog',
                  status: 'active',
                },
              ],
            },
          },
        ],
      } as any);

      const logSpy = vi.spyOn(console, 'log').mockImplementation(() => {});
      const program = new Command();
      setupIssueCommands(program);

      await program.parseAsync(['node', 'test', 'issue', 'list']);

      const output = logSpy.mock.calls.map(call => call.join(' ')).join('\n');
      expect(output).toContain('#199');
      expect(output).toContain('Waiting for #199');
    });
  });

  describe('mo issue show - prerequisite display', () => {
    it('should display prerequisite issues and their delivery state', async () => {
      const mockedApiClient = vi.mocked(apiClient);
      mockedApiClient
        .mockResolvedValueOnce({
          success: true,
          data: {
            number: 201,
            title: 'Issue #201',
            priority: 'p2',
            stage: 'backlog',
            status: 'active',
            projectName: 'test',
            baseBranch: 'main',
            labels: [],
            comments: [],
            prerequisites: [
              {
                number: 200,
                title: 'Issue #200',
                delivered: false,
                stage: 'backlog',
                status: 'active',
              },
              {
                number: 199,
                title: 'Issue #199',
                delivered: true,
                stage: 'done',
                status: 'completed',
                mergeState: 'merged',
              },
            ],
            startEligibility: {
              startable: false,
              reason: 'waiting-for-delivery',
              waitingForDelivery: [
                {
                  number: 200,
                  title: 'Issue #200',
                  delivered: false,
                  stage: 'backlog',
                  status: 'active',
                },
              ],
            },
          },
        } as any)
        .mockResolvedValueOnce({ success: true, data: [] } as any)
        .mockResolvedValueOnce({ success: true, data: [] } as any);

      const logSpy = vi.spyOn(console, 'log').mockImplementation(() => {});
      const program = new Command();
      setupIssueCommands(program);

      await program.parseAsync(['node', 'test', 'issue', 'show', '201']);

      const output = logSpy.mock.calls.map(call => call.join(' ')).join('\n');
      expect(output).toContain('Start Prerequisites');
      expect(output).toContain('#200');
      expect(output).toContain('not delivered');
      expect(output).toContain('#199');
      expect(output).toContain('delivered');
    });

    it('should show waiting reason in issue show', async () => {
      const mockedApiClient = vi.mocked(apiClient);
      mockedApiClient
        .mockResolvedValueOnce({
          success: true,
          data: {
            number: 201,
            title: 'Issue #201',
            priority: 'p2',
            stage: 'backlog',
            status: 'active',
            projectName: 'test',
            baseBranch: 'main',
            labels: [],
            comments: [],
            startEligibility: {
              startable: false,
              reason: 'waiting-for-delivery',
              message: 'Issue #201 is waiting for prerequisite #200 to be delivered.',
              waitingForDelivery: [
                {
                  number: 200,
                  title: 'Issue #200',
                  delivered: false,
                  stage: 'backlog',
                  status: 'active',
                },
              ],
            },
          },
        } as any)
        .mockResolvedValueOnce({ success: true, data: [] } as any)
        .mockResolvedValueOnce({ success: true, data: [] } as any);

      const logSpy = vi.spyOn(console, 'log').mockImplementation(() => {});
      const errorSpy = vi.spyOn(console, 'error').mockImplementation(() => {});
      const program = new Command();
      setupIssueCommands(program);

      await program.parseAsync(['node', 'test', 'issue', 'show', '201']);

      const output = [...logSpy.mock.calls, ...errorSpy.mock.calls].map(call => call.join(' ')).join('\n');
      expect(output).toContain('waiting for prerequisite #200');
    });
  });

  describe('mo issue start - rejection messaging', () => {
    it('should print server-provided waiting-for-delivery message and exit non-zero', async () => {
      const mockedApiClient = vi.mocked(apiClient);
      mockedApiClient.mockResolvedValueOnce({
        success: false,
        error: 'Issue #201 is waiting for prerequisite #200 to be delivered.',
        data: {
          startEligibility: {
            startable: false,
            reason: 'waiting-for-delivery',
            waitingForDelivery: [
              {
                number: 200,
                title: 'Issue #200',
                delivered: false,
                stage: 'backlog',
                status: 'active',
              },
            ],
          },
        },
      } as any);

      const logSpy = vi.spyOn(console, 'log').mockImplementation(() => {});
      const errorSpy = vi.spyOn(console, 'error').mockImplementation(() => {});
      const exitSpy = vi.spyOn(process, 'exit').mockImplementation(() => { throw new Error('exit'); });
      const program = new Command();
      setupIssueCommands(program);

      await expect(program.parseAsync(['node', 'test', 'issue', 'start', '201'])).rejects.toThrow('exit');

      expect(errorSpy).toHaveBeenCalledWith(expect.stringContaining('waiting for prerequisite'));
      expect(exitSpy).toHaveBeenCalledWith(1);
    });

    it('should not make additional requests after rejection', async () => {
      const mockedApiClient = vi.mocked(apiClient);
      mockedApiClient.mockResolvedValueOnce({
        success: false,
        error: 'Issue #201 is waiting for prerequisite #200 to be delivered.',
        data: {
          startEligibility: {
            startable: false,
            reason: 'waiting-for-delivery',
            waitingForDelivery: [],
          },
        },
      } as any);

      vi.spyOn(console, 'error').mockImplementation(() => {});
      vi.spyOn(process, 'exit').mockImplementation(() => { throw new Error('exit'); });
      const program = new Command();
      setupIssueCommands(program);

      try {
        await program.parseAsync(['node', 'test', 'issue', 'start', '201']);
      } catch {}

      expect(mockedApiClient).toHaveBeenCalledTimes(1);
    });
  });

  describe('mo issue list - no body text parsing', () => {
    it('should not infer prerequisites from issue body text', async () => {
      const mockedApiClient = vi.mocked(apiClient);
      mockedApiClient.mockResolvedValueOnce({
        success: true,
        data: [
          {
            number: 201,
            title: 'Issue #201',
            body: 'This depends on #200 and #199',
            priority: 'p2',
            stage: 'backlog',
            status: 'active',
            projectName: 'test',
            baseBranch: 'main',
            labels: [],
            comments: [],
            startEligibility: {
              startable: false,
              reason: 'waiting-for-delivery',
              waitingForDelivery: [
                {
                  number: 200,
                  title: 'Issue #200',
                  delivered: false,
                  stage: 'backlog',
                  status: 'active',
                },
              ],
            },
          },
        ],
      } as any);

      const logSpy = vi.spyOn(console, 'log').mockImplementation(() => {});
      const program = new Command();
      setupIssueCommands(program);

      await program.parseAsync(['node', 'test', 'issue', 'list']);

      const output = logSpy.mock.calls.map(call => call.join(' ')).join('\n');
      expect(output).toContain('Waiting for #200');
    });
  });

  describe('CLI declares prerequisite through API', () => {
    it('should call POST /api/issues/:number/prerequisites with structured request', async () => {
      const mockedApiClient = vi.mocked(apiClient);
      mockedApiClient
        .mockResolvedValueOnce({
          success: true,
          data: {
            message: 'Issue #201 now requires Issue #200 to be delivered before start.',
            issue: {
              number: 201,
              title: 'Issue #201',
              priority: 'p2',
              stage: 'backlog',
              status: 'active',
              labels: [],
              prerequisites: [
                {
                  number: 200,
                  title: 'Issue #200',
                  delivered: false,
                  stage: 'backlog',
                  status: 'active',
                },
              ],
              startEligibility: {
                startable: false,
                reason: 'waiting-for-delivery',
                message: 'Issue #201 is waiting for prerequisite #200 to be delivered.',
                waitingForDelivery: [{ number: 200, title: 'Issue #200', delivered: false, stage: 'backlog', status: 'active' }],
              },
            },
          },
        } as any);

      const logSpy = vi.spyOn(console, 'log').mockImplementation(() => {});
      const program = new Command();
      setupIssueCommands(program);

      await program.parseAsync(['node', 'test', 'issue', 'add-prerequisite', '201', '200']);

      const postCall = mockedApiClient.mock.calls.find(call => call[0] === 'POST' && call[1].includes('/prerequisites'));
      expect(postCall).toBeDefined();
      expect(postCall![2]).toEqual({ prerequisiteNumber: 200 });
      expect(logSpy).toHaveBeenCalledWith(expect.stringContaining('requires Issue #200'));
    });

    it('should surface circular declaration error from server', async () => {
      const mockedApiClient = vi.mocked(apiClient);
      mockedApiClient.mockResolvedValueOnce({
        success: false,
        error: 'Circular prerequisite declaration',
        data: { reason: 'circular-prerequisite' },
      } as any);

      vi.spyOn(console, 'error').mockImplementation(() => {});
      vi.spyOn(process, 'exit').mockImplementation(() => { throw new Error('exit'); });
      const program = new Command();
      setupIssueCommands(program);

      await expect(program.parseAsync(['node', 'test', 'issue', 'add-prerequisite', '200', '201'])).rejects.toThrow('exit');

      expect(mockedApiClient.mock.calls.some(call => call[0] === 'POST')).toBe(true);
    });
  });

  describe('task-level tasks.json dependsOn separation', () => {
    it('should not interpret tasks.json dependsOn as issue-level start prerequisite', async () => {
      const mockedApiClient = vi.mocked(apiClient);
      mockedApiClient.mockResolvedValueOnce({
        success: true,
        data: [
          {
            number: 201,
            title: 'Issue #201 with tasks.json dependsOn',
            body: '{"tasks":[{"id":"T-001","dependsOn":["T-002"]}]}',
            priority: 'p2',
            stage: 'backlog',
            status: 'active',
            projectName: 'test',
            baseBranch: 'main',
            labels: [],
            comments: [],
            startEligibility: {
              startable: true,
              reason: 'ready',
              waitingForDelivery: [],
            },
          },
        ],
      } as any);

      const logSpy = vi.spyOn(console, 'log').mockImplementation(() => {});
      const program = new Command();
      setupIssueCommands(program);

      await program.parseAsync(['node', 'test', 'issue', 'list']);

      const output = logSpy.mock.calls.map(call => call.join(' ')).join('\n');
      expect(output).not.toContain('Waiting for');
    });
  });
});
