export type EventMap = {
  stage_changed: { issueId: string; projectId: string; from: string; to: string };
  comment_added: { issueId: string; projectId: string; commentId: string; body: string; createdAt: string };
  agent_started: { issueId: string; projectId: string };
  agent_completed: { issueId: string; projectId: string };
  agent_paused: { issueId: string; projectId: string; issueNumber: number };
  agent_error: { issueId: string; projectId: string; error: string };
  approval_requested: { issueId: string; projectId: string; stage: string };
  tool_call: { issueId: string; projectId: string; toolName: string; status: string; locations?: string[] };
  question_asked: { issueId: string; projectId: string; questionId: string; question: string };
  question_answered: { issueId: string; projectId: string; questionId: string; answer: string };
  explore_crystallized: { sessionId: string; issueId: string; projectId: string };
  agent_text_chunk: { issueId: string; projectId: string; text: string; stepIndex: number };
  main_tool_call: { issueId: string; projectId: string; executionId: string; toolName: string; state: 'started' | 'completed' | 'failed'; args?: string; result?: string; error?: string; duration?: number };
  coder_text_chunk: { issueId: string; projectId: string; executionId: string; acpSessionId: string; text: string };
  coder_tool_call: { issueId: string; projectId: string; executionId: string; acpSessionId: string; toolName: string; state: 'started' | 'completed' };
  ralph_task_update: { issueId: string; projectId: string; executionId: string; taskId: string; taskIndex: number; totalTasks: number; status: 'started' | 'completed' | 'failed' | 'retrying'; attempt?: number; error?: string };
  ralph_loop_progress: { issueId: string; projectId: string; executionId: string; completed: number; failed: number; total: number };
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
