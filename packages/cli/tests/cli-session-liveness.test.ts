import { describe, it, expect } from 'vitest';
import { formatSessionState, type CoderSessionResponse } from '../src/cli/commands/issue';

describe('CLI session liveness display', () => {
  const baseSession: CoderSessionResponse = {
    id: 'session-1',
    acpSessionId: 'acp-1',
    status: 'running',
    createdAt: new Date().toISOString(),
    lastDataAt: null,
    probeSentAt: null,
    probeDeadlineAt: null,
    failureReason: null,
  };

  it('displays "Running" for running sessions', () => {
    const result = formatSessionState({ ...baseSession, status: 'running' });
    expect(result).toContain('Running');
  });

  it('displays "No active session" when no session is provided', () => {
    const result = formatSessionState(null);
    expect(result).toContain('No active session');
  });

  it('displays "No active session" when no active session call exists (completed session)', () => {
    const result = formatSessionState({ ...baseSession, status: 'completed' });
    expect(result).toContain('No active session');
  });

  it('displays "Checking session" for probing sessions', () => {
    const result = formatSessionState({ ...baseSession, status: 'probing' });
    expect(result).toContain('Checking session');
  });

  it('includes probe timing when available for probing sessions', () => {
    const futureDeadline = new Date(Date.now() + 30000).toISOString();
    const result = formatSessionState({
      ...baseSession,
      status: 'probing',
      probeSentAt: new Date().toISOString(),
      probeDeadlineAt: futureDeadline,
    });
    expect(result).toContain('Checking session');
    expect(result).toContain('remaining');
  });

  it('does not include probe timing when deadline has passed', () => {
    const pastDeadline = new Date(Date.now() - 10000).toISOString();
    const result = formatSessionState({
      ...baseSession,
      status: 'probing',
      probeSentAt: new Date().toISOString(),
      probeDeadlineAt: pastDeadline,
    });
    expect(result).toContain('Checking session');
    expect(result).not.toContain('remaining');
  });

  it('displays "Session failed" for failed sessions', () => {
    const result = formatSessionState({ ...baseSession, status: 'failed' });
    expect(result).toContain('Session failed');
  });

  it('includes failureReason when available for failed sessions', () => {
    const result = formatSessionState({
      ...baseSession,
      status: 'failed',
      failureReason: 'Probe timeout',
    });
    expect(result).toContain('Session failed');
    expect(result).toContain('Probe timeout');
  });

  it('displays failed session without failureReason when not available', () => {
    const result = formatSessionState({
      ...baseSession,
      status: 'failed',
      failureReason: null,
    });
    expect(result).toContain('Session failed');
    expect(result).not.toContain(':');
  });
});
