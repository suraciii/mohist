import { describe, it, expect } from 'vitest';
import type { AcpConnection, AcpSessionResult } from '../src/agent-runtime/acp-session';

describe('AcpConnection multi-round contract', () => {
  it('should support multiple prompt calls on a mock connection', async () => {
    const responses: string[] = [
      'proposal generated',
      'specs generated',
      'design generated',
      'tasks generated',
      'self-review complete',
    ];

    let callCount = 0;
    const connection: AcpConnection = {
      async prompt(text: string): Promise<AcpSessionResult> {
        const response = responses[callCount] ?? 'default';
        callCount++;
        return { text: response, success: true, acpSessionId: 'session-1' };
      },
      async close(): Promise<void> {},
    };

    const r1 = await connection.prompt('generate proposal');
    expect(r1.success).toBe(true);
    expect(r1.text).toBe('proposal generated');

    const r2 = await connection.prompt('generate specs');
    expect(r2.success).toBe(true);
    expect(r2.text).toBe('specs generated');

    const r3 = await connection.prompt('generate design');
    expect(r3.success).toBe(true);
    expect(r3.text).toBe('design generated');

    const r4 = await connection.prompt('generate tasks');
    expect(r4.success).toBe(true);
    expect(r4.text).toBe('tasks generated');

    const r5 = await connection.prompt('self-review');
    expect(r5.success).toBe(true);
    expect(r5.text).toBe('self-review complete');

    expect(callCount).toBe(5);
  });

  it('should return error after close is called', async () => {
    let closed = false;
    const connection: AcpConnection = {
      async prompt(text: string): Promise<AcpSessionResult> {
        if (closed) {
          return { text: '', success: false, error: 'Connection is closed' };
        }
        return { text: 'ok', success: true };
      },
      async close(): Promise<void> {
        closed = true;
      },
    };

    const r1 = await connection.prompt('first');
    expect(r1.success).toBe(true);

    await connection.close();

    const r2 = await connection.prompt('after close');
    expect(r2.success).toBe(false);
    expect(r2.error).toContain('closed');
  });

  it('should report per-round text (not cumulative)', async () => {
    const roundTexts: string[] = [];
    const connection: AcpConnection = {
      async prompt(text: string): Promise<AcpSessionResult> {
        roundTexts.push(`round-${roundTexts.length + 1}`);
        return {
          text: `round-${roundTexts.length}`,
          success: true,
          acpSessionId: 'session-1',
        };
      },
      async close(): Promise<void> {},
    };

    const r1 = await connection.prompt('round 1');
    const r2 = await connection.prompt('round 2');

    expect(r1.text).toBe('round-1');
    expect(r2.text).toBe('round-2');
  });
});

describe('AcpSessionResult interface', () => {
  it('should represent a successful result', () => {
    const result: AcpSessionResult = {
      text: 'done',
      success: true,
      acpSessionId: 'sess-123',
    };

    expect(result.success).toBe(true);
    expect(result.text).toBe('done');
  });

  it('should represent a failed result', () => {
    const result: AcpSessionResult = {
      text: '',
      success: false,
      error: 'Timed out',
    };

    expect(result.success).toBe(false);
    expect(result.error).toBe('Timed out');
  });
});
