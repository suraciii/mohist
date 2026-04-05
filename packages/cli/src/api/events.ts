import { Hono } from 'hono';
import { streamSSE } from 'hono/streaming';
import type { SSEStreamingApi } from 'hono/streaming';
import { EventBus, type EventName } from '../services';

const ALL_EVENT_TYPES: EventName[] = [
  'stage_changed',
  'comment_added',
  'agent_started',
  'agent_completed',
  'agent_error',
  'approval_requested',
];

export function createEventRoutes(eventBus: EventBus): Hono {
  const app = new Hono();

  app.get('/', async (c) => {
    const projectId = c.req.query('projectId');

    return streamSSE(c, async (stream: SSEStreamingApi) => {
      type Handler = (data: any) => void;

      const createHandler = (eventName: EventName): Handler => {
        return (data: any) => {
          if (projectId) {
            const d = data as { projectId?: string };
            if (d.projectId && d.projectId !== projectId) {
              return;
            }
          }
          stream.writeSSE({
            event: eventName,
            data: JSON.stringify(data),
          });
        };
      };

      const handlers = new Map<EventName, Handler>();
      for (const eventType of ALL_EVENT_TYPES) {
        const handler = createHandler(eventType);
        handlers.set(eventType, handler);
        eventBus.on(eventType, handler);
      }

      const abortHandler = () => {
        for (const [eventType, handler] of handlers) {
          eventBus.off(eventType, handler);
        }
      };
      c.req.raw.signal.addEventListener('abort', abortHandler);

      // Keep connection alive for max 30 minutes to prevent resource leaks
      const MAX_CONNECTION_DURATION = 30 * 60 * 1000; // 30 minutes
      await stream.sleep(MAX_CONNECTION_DURATION);

      // Clean up on normal completion
      c.req.raw.signal.removeEventListener('abort', abortHandler);
      for (const [eventType, handler] of handlers) {
        eventBus.off(eventType, handler);
      }
    });
  });

  return app;
}
