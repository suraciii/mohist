import { describe, it, expect, vi, beforeEach } from 'vitest';
import http from 'http';
import { connectSSE } from '../src/cli/sse-client';

vi.mock('http', () => {
  const mockReq = {
    on: vi.fn(),
    destroy: vi.fn(),
  };
  const mockRes = {
    on: vi.fn(),
    statusCode: 200,
    headers: {},
  };
  return {
    default: {
      get: vi.fn((_url: string, cb: (res: unknown) => void) => {
        cb(mockRes);
        return mockReq;
      }),
    },
  };
});

function getCallback(mockOn: ReturnType<typeof vi.fn>, event: string): Function | undefined {
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  const call = mockOn.mock.calls.find((c: any[]) => c[0] === event);
  return call?.[1];
}

describe('connectSSE', () => {
  let capturedRes: { on: ReturnType<typeof vi.fn> };

  beforeEach(() => {
    vi.clearAllMocks();
    capturedRes = { on: vi.fn() };
    (http.get as ReturnType<typeof vi.fn>).mockImplementation(
      (_url: string, cb: (res: unknown) => void) => {
        cb(capturedRes);
        return { on: vi.fn(), destroy: vi.fn() };
      }
    );
  });

  it('should call onEvent for a single event', () => {
    const onEvent = vi.fn();
    const onError = vi.fn();
    const onClose = vi.fn();

    connectSSE('http://localhost:3000/api/events', { onEvent, onError, onClose });

    const dataCallback = getCallback(capturedRes.on, 'data') as (chunk: Buffer) => void;
    dataCallback(Buffer.from('event: agent_started\ndata: {"issueId":"1"}\n\n'));

    expect(onEvent).toHaveBeenCalledWith('agent_started', '{"issueId":"1"}');
  });

  it('should parse multiple events from a single chunk', () => {
    const onEvent = vi.fn();

    connectSSE('http://localhost:3000/api/events', {
      onEvent,
      onError: vi.fn(),
      onClose: vi.fn(),
    });

    const dataCallback = getCallback(capturedRes.on, 'data') as (chunk: Buffer) => void;

    dataCallback(
      Buffer.from(
        'event: agent_started\ndata: {"issueId":"1"}\n\nevent: stage_changed\ndata: {"from":"draft","to":"designing"}\n\n'
      )
    );

    expect(onEvent).toHaveBeenCalledTimes(2);
    expect(onEvent).toHaveBeenNthCalledWith(1, 'agent_started', '{"issueId":"1"}');
    expect(onEvent).toHaveBeenNthCalledWith(
      2,
      'stage_changed',
      '{"from":"draft","to":"designing"}'
    );
  });

  it('should handle multi-line data by concatenating with newlines', () => {
    const onEvent = vi.fn();

    connectSSE('http://localhost:3000/api/events', {
      onEvent,
      onError: vi.fn(),
      onClose: vi.fn(),
    });

    const dataCallback = getCallback(capturedRes.on, 'data') as (chunk: Buffer) => void;

    dataCallback(
      Buffer.from('event: comment_added\ndata: line1\ndata: line2\ndata: line3\n\n')
    );

    expect(onEvent).toHaveBeenCalledWith('comment_added', 'line1\nline2\nline3');
  });

  it('should default to "message" event type when no event: line is present', () => {
    const onEvent = vi.fn();

    connectSSE('http://localhost:3000/api/events', {
      onEvent,
      onError: vi.fn(),
      onClose: vi.fn(),
    });

    const dataCallback = getCallback(capturedRes.on, 'data') as (chunk: Buffer) => void;
    dataCallback(Buffer.from('data: some data\n\n'));

    expect(onEvent).toHaveBeenCalledWith('message', 'some data');
  });

  it('should call onClose when response ends', () => {
    const onClose = vi.fn();

    connectSSE('http://localhost:3000/api/events', {
      onEvent: vi.fn(),
      onError: vi.fn(),
      onClose,
    });

    const endCallback = getCallback(capturedRes.on, 'end') as () => void;
    endCallback();

    expect(onClose).toHaveBeenCalled();
  });

  it('should flush remaining data on response end', () => {
    const onEvent = vi.fn();

    connectSSE('http://localhost:3000/api/events', {
      onEvent,
      onError: vi.fn(),
      onClose: vi.fn(),
    });

    const dataCallback = getCallback(capturedRes.on, 'data') as (chunk: Buffer) => void;
    dataCallback(Buffer.from('event: agent_started\ndata: {"issueId":"1"}'));

    expect(onEvent).not.toHaveBeenCalled();

    const endCallback = getCallback(capturedRes.on, 'end') as () => void;
    endCallback();

    expect(onEvent).toHaveBeenCalledWith('agent_started', '{"issueId":"1"}');
  });

  it('should call onError when request fails', () => {
    const onError = vi.fn();

    (http.get as ReturnType<typeof vi.fn>).mockImplementation(
      (_url: string, _cb: (res: unknown) => void) => {
        const req = { on: vi.fn(), destroy: vi.fn() };
        setTimeout(() => {
          const errorCallback = getCallback(req.on, 'error') as (err: Error) => void;
          errorCallback(new Error('Connection refused'));
        }, 0);
        return req;
      }
    );

    connectSSE('http://localhost:3000/api/events', {
      onEvent: vi.fn(),
      onError,
      onClose: vi.fn(),
    });

    return new Promise<void>((resolve) => {
      setTimeout(() => {
        expect(onError).toHaveBeenCalledWith(expect.any(Error));
        expect(onError.mock.calls[0][0].message).toBe('Connection refused');
        resolve();
      }, 10);
    });
  });

  it('should call onError when response emits error', () => {
    const onError = vi.fn();

    connectSSE('http://localhost:3000/api/events', {
      onEvent: vi.fn(),
      onError,
      onClose: vi.fn(),
    });

    const errorCallback = getCallback(capturedRes.on, 'error') as (err: Error) => void;
    errorCallback(new Error('Stream error'));

    expect(onError).toHaveBeenCalledWith(expect.any(Error));
    expect(onError.mock.calls[0][0].message).toBe('Stream error');
  });

  it('should handle event spanning multiple chunks', () => {
    const onEvent = vi.fn();

    connectSSE('http://localhost:3000/api/events', {
      onEvent,
      onError: vi.fn(),
      onClose: vi.fn(),
    });

    const dataCallback = getCallback(capturedRes.on, 'data') as (chunk: Buffer) => void;
    dataCallback(Buffer.from('event: agent_started\ndata: {"issu'));
    dataCallback(Buffer.from('eId":"1"}\n\n'));

    expect(onEvent).toHaveBeenCalledWith('agent_started', '{"issueId":"1"}');
  });

  it('should ignore empty data sections', () => {
    const onEvent = vi.fn();

    connectSSE('http://localhost:3000/api/events', {
      onEvent,
      onError: vi.fn(),
      onClose: vi.fn(),
    });

    const dataCallback = getCallback(capturedRes.on, 'data') as (chunk: Buffer) => void;
    dataCallback(Buffer.from('\n\n'));

    expect(onEvent).not.toHaveBeenCalled();
  });
});
