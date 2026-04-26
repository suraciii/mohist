import { describe, it, expect, vi, beforeEach } from 'vitest';

vi.mock('../src/config/config-loader', () => ({
  resolveOpencodeBinPath: () => '/mock/opencode',
}));

vi.mock('child_process', () => {
  const { EventEmitter } = require('events');
  const { Writable, Readable } = require('stream');
  return {
    spawn: vi.fn(() => {
      const proc = new EventEmitter();
      (proc as any).stdin = new Writable({
        write: (_c: any, _e: any, cb: any) => cb(),
      });
      (proc as any).stdout = new Readable({ read() {} });
      (proc as any).kill = vi.fn();
      return proc;
    }),
  };
});

vi.mock('@agentclientprotocol/sdk', () => ({
  ClientSideConnection: vi.fn().mockImplementation(() => ({
    initialize: vi.fn().mockRejectedValue(new Error('test failure')),
  })),
  ndJsonStream: vi.fn().mockReturnValue({
    readable: { cancel: vi.fn().mockResolvedValue(undefined) },
    writable: { abort: vi.fn().mockResolvedValue(undefined) },
  }),
  PROTOCOL_VERSION: '0.1',
}));

import { runAcpSession } from '../src/agent-runtime/acp-session';

describe('ACP session taskId logging', () => {
  let workflowLogInsert: ReturnType<typeof vi.fn>;

  beforeEach(() => {
    vi.clearAllMocks();
    workflowLogInsert = vi.fn();
  });

  function getSessionStartData(): Record<string, unknown> | undefined {
    const call = workflowLogInsert.mock.calls.find(
      (c: unknown[]) => (c as unknown[])[2] === 'acp_session_start',
    );
    return (call as unknown[] | undefined)?.[3] as
      | Record<string, unknown>
      | undefined;
  }

  it('should log taskId in acp_session_start when provided', async () => {
    const result = await runAcpSession({
      cwd: '/tmp/test',
      task: 'some task prompt text',
      taskId: 'T-001',
      workflowLogRepo: { insert: workflowLogInsert } as any,
      issueId: 'issue-123',
    });

    expect(result.success).toBe(false);

    const data = getSessionStartData();
    expect(data).toBeDefined();
    expect(data!.taskId).toBe('T-001');
  });

  it('should have undefined taskId when not provided', async () => {
    const result = await runAcpSession({
      cwd: '/tmp/test',
      task: 'some task prompt text',
      workflowLogRepo: { insert: workflowLogInsert } as any,
      issueId: 'issue-123',
    });

    expect(result.success).toBe(false);

    const data = getSessionStartData();
    expect(data).toBeDefined();
    expect(data!.taskId).toBeUndefined();
  });

  it('should include promptPreview truncated to 100 characters', async () => {
    const longTask = 'x'.repeat(200);

    await runAcpSession({
      cwd: '/tmp/test',
      task: longTask,
      taskId: 'T-002',
      workflowLogRepo: { insert: workflowLogInsert } as any,
      issueId: 'issue-123',
    });

    const data = getSessionStartData();
    expect(data).toBeDefined();
    expect(data!.promptPreview).toBe('x'.repeat(100));
    expect((data!.promptPreview as string).length).toBe(100);
  });
});
