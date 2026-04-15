import { streamText, stepCountIs } from 'ai';
import type { ModelMessage } from 'ai';
import type { LanguageModelV3 } from '@ai-sdk/provider';
import type { Session } from './session';
import { SessionManager } from './session';
import { ToolRegistry } from './tool';
import type { EventBus } from '../services/event-bus';
import type { AgentSessionMessageRepo } from '../db/agent-session-message-repo';

export interface AgentLoopOptions {
  maxSteps?: number;
  system?: string;
  eventBus?: EventBus;
  eventContext?: { issueId: string; projectId: string };
  agentSessionMessageRepo?: AgentSessionMessageRepo;
}

export interface AgentLoopResult {
  text: string;
  steps: number;
  finishReason: string;
}

export async function runAgentLoop(
  session: Session,
  sessionManager: SessionManager,
  toolRegistry: ToolRegistry,
  model: LanguageModelV3,
  options?: AgentLoopOptions,
): Promise<AgentLoopResult> {
  const maxSteps = options?.maxSteps ?? 20;
  if (session.messages.length === 0) {
    sessionManager.appendMessage(session.id, {
      role: 'user',
      content:
        'Start working on the current issue. Begin by reading the workflow configuration using read_workflow.',
    });
  }
  const messages = session.messages;
  const tools = toolRegistry.toToolSet();
  const eventBus = options?.eventBus;
  const eventContext = options?.eventContext;

  const result = streamText({
    model,
    system: options?.system,
    messages,
    tools,
    stopWhen: stepCountIs(maxSteps),
  });

  let stepIndex = 0;
  const toolStartTimes = new Map<string, number>();

  for await (const part of result.fullStream) {
    if (part.type === 'text-delta') {
      if (eventBus && eventContext) {
        eventBus.emit('agent_text_chunk', {
          issueId: eventContext.issueId,
          projectId: eventContext.projectId,
          text: part.text,
          stepIndex,
        });
      }
    } else if (part.type === 'tool-call') {
      const executionId = crypto.randomUUID();
      toolRegistry.setCurrentExecutionId(executionId);
      toolStartTimes.set(executionId, Date.now());
      stepIndex++;
      if (eventBus && eventContext) {
        eventBus.emit('main_tool_call', {
          issueId: eventContext.issueId,
          projectId: eventContext.projectId,
          executionId,
          toolName: part.toolName,
          state: 'started',
          args: JSON.stringify(part.input),
          stepIndex,
        });
      }
    } else if (part.type === 'tool-result') {
      const executionId = toolRegistry.getCurrentExecutionId() ?? crypto.randomUUID();
      const startTime = toolStartTimes.get(executionId);
      const duration = startTime ? Date.now() - startTime : undefined;
      if (eventBus && eventContext) {
        eventBus.emit('main_tool_call', {
          issueId: eventContext.issueId,
          projectId: eventContext.projectId,
          executionId,
          toolName: part.toolName,
          state: 'completed',
          result: part.output,
          duration,
          stepIndex,
        });
      }
      toolStartTimes.delete(executionId);
      toolRegistry.clearCurrentExecutionId();
    } else if (part.type === 'tool-error') {
      const executionId = toolRegistry.getCurrentExecutionId() ?? crypto.randomUUID();
      const startTime = toolStartTimes.get(executionId);
      const duration = startTime ? Date.now() - startTime : undefined;
      if (eventBus && eventContext) {
        eventBus.emit('main_tool_call', {
          issueId: eventContext.issueId,
          projectId: eventContext.projectId,
          executionId,
          toolName: (part as { toolName?: string }).toolName ?? 'unknown',
          state: 'failed',
          error: (part as { error?: string }).error ?? 'Unknown error',
          duration,
          stepIndex,
        });
      }
      toolStartTimes.delete(executionId);
      toolRegistry.clearCurrentExecutionId();
    }
  }

  const allSteps = await result.steps;
  const text = await result.text;
  const finishReason = await result.finishReason;

  for (const step of allSteps) {
    for (const msg of step.response.messages) {
      sessionManager.appendMessage(session.id, msg as ModelMessage);
    }
  }

  const repo = options?.agentSessionMessageRepo;
  if (repo && eventContext) {
    const issueId = eventContext.issueId;
    const sessionId = session.id;
    for (let stepIdx = 0; stepIdx < allSteps.length; stepIdx++) {
      const stepMessages = allSteps[stepIdx].response.messages;
      for (let msgIdx = 0; msgIdx < stepMessages.length; msgIdx++) {
        const msg = stepMessages[msgIdx] as ModelMessage;
        persistMessage(repo, issueId, sessionId, msg, stepIdx, msgIdx);
      }
    }
  }

  return {
    text,
    steps: allSteps.length,
    finishReason,
  };
}

function persistMessage(
  repo: AgentSessionMessageRepo,
  issueId: string,
  sessionId: string,
  msg: ModelMessage,
  stepIndex: number,
  messageIndex: number,
): void {
  const role = msg.role;

  if (role === 'assistant') {
    const content = msg.content;
    let textContent: string | null = null;
    let toolCallsJson: string | null = null;

    if (typeof content === 'string') {
      textContent = content;
    } else if (Array.isArray(content)) {
      const textParts: string[] = [];
      const calls: Array<{ toolCallId: string; toolName: string; args: unknown }> = [];
      for (const part of content) {
        if (part.type === 'text') {
          textParts.push(part.text);
        } else if (part.type === 'tool-call') {
          calls.push({ toolCallId: part.toolCallId, toolName: part.toolName, args: part.input });
        }
      }
      textContent = textParts.length > 0 ? textParts.join('') : null;
      if (calls.length > 0) {
        toolCallsJson = JSON.stringify(calls);
      }
    }

    repo.insert({
      issueId,
      sessionId,
      role,
      content: textContent,
      toolCalls: toolCallsJson,
      stepIndex,
      messageIndex,
    });
  } else if (role === 'tool') {
    const content = msg.content;
    if (Array.isArray(content)) {
      for (const part of content) {
        if (part.type === 'tool-result') {
          repo.insert({
            issueId,
            sessionId,
            role,
            content: null,
            toolCallId: part.toolCallId,
            toolName: part.toolName,
            toolResult: typeof part.output === 'string' ? part.output : JSON.stringify(part.output),
            stepIndex,
            messageIndex,
          });
        }
      }
    }
  } else {
    repo.insert({
      issueId,
      sessionId,
      role,
      content: typeof msg.content === 'string' ? msg.content : JSON.stringify(msg.content),
      stepIndex,
      messageIndex,
    });
  }
}
