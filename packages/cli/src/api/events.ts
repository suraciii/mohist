import { Hono } from 'hono';
import { streamSSE } from 'hono/streaming';
import type { SSEStreamingApi } from 'hono/streaming';
import { EventBus, type EventName } from '../services';

const ALL_EVENT_TYPES: EventName[] = [
  'stage_changed',
  'comment_added',
  'agent_started',
  'agent_completed',
  'agent_paused',
  'agent_error',
  'approval_requested',
  'tool_call',
  'question_asked',
  'question_answered',
  'explore_crystallized',
  'agent_text_chunk',
  'main_tool_call',
  'coder_text_chunk',
  'coder_tool_call',
  'ralph_task_update',
  'ralph_loop_progress',
  'plan_round_start',
  'plan_session_update',
  'merge_queued',
  'merge_started',
  'merge_completed',
  'merge_failed',
];

export function createEventRoutes(eventBus: EventBus): Hono {
  const app = new Hono();

  app.get('/', async (c) => {
    const projectId = c.req.query('projectId');

    return streamSSE(c, async (stream: SSEStreamingApi) => {
      type Handler = (data: any) => void;
      let cleanedUp = false;
      let heartbeatTimer: ReturnType<typeof setInterval> | undefined;
      const handlers = new Map<EventName, Handler>();

      const cleanup = () => {
        if (cleanedUp) return;
        cleanedUp = true;
        if (heartbeatTimer) clearInterval(heartbeatTimer);
        c.req.raw.signal.removeEventListener('abort', onAbort);
        for (const [eventType, handler] of handlers) {
          eventBus.off(eventType, handler);
        }
      };

      const safeWriteSSE = (message: Parameters<typeof stream.writeSSE>[0]) => {
        stream.writeSSE(message).catch(() => cleanup());
      };

      const createHandler = (eventName: EventName): Handler => {
        return (data: any) => {
          if (cleanedUp) return;
          if (projectId) {
            const d = data as { projectId?: string };
            if (d.projectId && d.projectId !== projectId) {
              return;
            }
          }
          safeWriteSSE({
            event: eventName,
            data: JSON.stringify(data),
          });
        };
      };

      for (const eventType of ALL_EVENT_TYPES) {
        const handler = createHandler(eventType);
        handlers.set(eventType, handler);
        eventBus.on(eventType, handler);
      }

      const onAbort = () => cleanup();
      c.req.raw.signal.addEventListener('abort', onAbort);

      const HEARTBEAT_INTERVAL = 30 * 1000;
      heartbeatTimer = setInterval(() => {
        stream.writeln(': heartbeat').catch(() => cleanup());
      }, HEARTBEAT_INTERVAL);

      const MAX_CONNECTION_DURATION = 30 * 60 * 1000;
      await stream.sleep(MAX_CONNECTION_DURATION);
      cleanup();
    });
  });

  return app;
}
