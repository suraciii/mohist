import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { formatEvent } from '../src/cli/event-formatter';

describe('formatEvent', () => {
  beforeEach(() => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date(2026, 3, 6, 12, 34, 56));
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it('should format agent_started event', () => {
    const result = formatEvent('agent_started', JSON.stringify({ issueId: '3' }));
    expect(result).toContain('[12:34:56]');
    expect(result).toContain('>>');
    expect(result).toContain('agent started');
    expect(result).toContain('issue #3');
  });

  it('should format agent_completed event', () => {
    const result = formatEvent('agent_completed', JSON.stringify({ issueId: '3' }));
    expect(result).toContain('[12:34:56]');
    expect(result).toContain('ok');
    expect(result).toContain('agent completed');
    expect(result).toContain('issue #3');
  });

  it('should format agent_paused event', () => {
    const result = formatEvent('agent_paused', JSON.stringify({ issueId: '3' }));
    expect(result).toContain('[12:34:56]');
    expect(result).toContain('||');
    expect(result).toContain('agent paused');
    expect(result).toContain('issue #3');
  });

  it('should format agent_error event with error message', () => {
    const result = formatEvent(
      'agent_error',
      JSON.stringify({ issueId: '3', error: 'API call failed' })
    );
    expect(result).toContain('[12:34:56]');
    expect(result).toContain('!!');
    expect(result).toContain('agent error');
    expect(result).toContain('issue #3: API call failed');
  });

  it('should format stage_changed event with from/to', () => {
    const result = formatEvent(
      'stage_changed',
      JSON.stringify({ issueId: '3', from: 'plan', to: 'build' })
    );
    expect(result).toContain('[12:34:56]');
    expect(result).toContain('->');
    expect(result).toContain('stage changed');
    expect(result).toContain('issue #3: plan -> build');
  });

  it('should format comment_added event with body', () => {
    const result = formatEvent(
      'comment_added',
      JSON.stringify({ issueId: '3', body: 'Plan complete, starting build...' })
    );
    expect(result).toContain('[12:34:56]');
    expect(result).toContain('##');
    expect(result).toContain('comment added');
    expect(result).toContain('issue #3: "Plan complete, starting build..."');
  });

  it('should format approval_requested event with body', () => {
    const result = formatEvent(
      'approval_requested',
      JSON.stringify({ issueId: '3', body: 'Review design.md' })
    );
    expect(result).toContain('[12:34:56]');
    expect(result).toContain('??');
    expect(result).toContain('approval requested');
    expect(result).toContain('issue #3: "Review design.md"');
  });

  it('should handle unknown event types by printing raw data', () => {
    const result = formatEvent('custom_event', JSON.stringify({ foo: 'bar' }));
    expect(result).toContain('[12:34:56]');
    expect(result).toContain('custom_event');
    expect(result).toContain('{"foo":"bar"}');
  });

  it('should handle invalid JSON data gracefully', () => {
    const result = formatEvent('agent_started', 'not valid json');
    expect(result).toContain('[12:34:56]');
    expect(result).toContain('agent_started');
    expect(result).toContain('not valid json');
  });

  it('should use local time for timestamp', () => {
    vi.setSystemTime(new Date(2026, 0, 1, 1, 2, 3));
    const result = formatEvent('agent_started', JSON.stringify({ issueId: '1' }));
    expect(result).toContain('[01:02:03]');
  });

  it('should pad timestamp components with zeros', () => {
    vi.setSystemTime(new Date(2026, 0, 1, 0, 5, 9));
    const result = formatEvent('agent_started', JSON.stringify({ issueId: '1' }));
    expect(result).toContain('[00:05:09]');
  });

  it('should handle event with stage field', () => {
    const result = formatEvent(
      'agent_paused',
      JSON.stringify({ issueId: '3', stage: 'waiting for input' })
    );
    expect(result).toContain('issue #3: waiting for input');
  });

  it('should handle event without issueId', () => {
    const result = formatEvent('agent_started', JSON.stringify({}));
    expect(result).toContain('[12:34:56]');
    expect(result).toContain('>>');
    expect(result).toContain('agent started');
    expect(result).not.toContain('issue');
  });
});
