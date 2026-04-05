export type EventMap = {
  stage_changed: { issueId: string; projectId: string; from: string; to: string };
  comment_added: { issueId: string; projectId: string; commentId: string; body: string; createdAt: string };
  agent_started: { issueId: string; projectId: string };
  agent_completed: { issueId: string; projectId: string };
  agent_error: { issueId: string; projectId: string; error: string };
  approval_requested: { issueId: string; projectId: string; stage: string };
};

export type EventName = keyof EventMap;
export type EventListener<T extends EventName = EventName> = (data: EventMap[T]) => void;

type ListenerEntry = {
  listener: EventListener;
};

export class EventBus {
  private listeners = new Map<string, Set<ListenerEntry>>();

  on<T extends EventName>(event: T, listener: EventListener<T>): void {
    if (!this.listeners.has(event)) {
      this.listeners.set(event, new Set());
    }
    const entry: ListenerEntry = { listener: listener as EventListener };
    this.listeners.get(event)!.add(entry);
  }

  off<T extends EventName>(event: T, listener: EventListener<T>): void {
    const set = this.listeners.get(event);
    if (!set) return;
    for (const entry of set) {
      if (entry.listener === listener) {
        set.delete(entry);
        break;
      }
    }
    if (set.size === 0) {
      this.listeners.delete(event);
    }
  }

  emit<T extends EventName>(event: T, data: EventMap[T]): void {
    const set = this.listeners.get(event);
    if (!set) return;
    for (const entry of set) {
      try {
        entry.listener(data);
      } catch {
        // swallow listener errors to avoid disrupting other listeners
      }
    }
  }

  removeAllListeners(event?: EventName): void {
    if (event) {
      this.listeners.delete(event);
    } else {
      this.listeners.clear();
    }
  }
}

export const eventBus = new EventBus();
