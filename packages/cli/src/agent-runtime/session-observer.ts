import type { SessionNotification } from '@agentclientprotocol/sdk';

export interface SessionObserver {
  onSessionStart?(ctx: SessionContext): void;
  onTextChunk?(ctx: SessionContext, text: string): void;
  onToolCall?(ctx: SessionContext, event: ToolCallEvent): void;
  onSessionEvent?(ctx: SessionContext, eventType: string, data: unknown): void;
  onStateChange?(ctx: SessionContext, from: SessionState, to: SessionState): void;
  onRawNotification?(ctx: SessionContext, notification: SessionNotification): void;
  writeMohistPrompt?(ctx: SessionContext, prompt: MohistPromptEvent): void;
}

export interface MohistPromptEvent {
  role: 'mohist';
  text: string;
  kind: string;
  sentAt: string;
  executionId?: string;
  stage?: string;
  title?: string;
  issueId?: string;
  acpSessionId?: string;
}

export interface SessionContext {
  readonly issueId: string;
  readonly issueNumber: number | undefined;
  readonly projectId: string;
  readonly executionId: string | undefined;
  readonly acpSessionId: string;
  readonly coderSessionId: string | undefined;
  readonly stage: string | undefined;
  readonly model: string | undefined;
  readonly processPid: number | undefined;
}

export type SessionState = 'initializing' | 'running' | 'completed' | 'failed' | 'timeout' | 'cancelled' | 'closed';

export interface ToolCallEvent {
  toolName: string;
  state: 'started' | 'completed';
  toolCallId: string;
  title?: string;
  rawInput?: unknown;
  rawOutput?: unknown;
}

export { WorkflowSessionObserver } from '../services/session-observers';
export type { WorkflowSessionObserverDeps } from '../services/session-observers';
export { createWorkflowSessionObservers } from '../services/session-observers';
