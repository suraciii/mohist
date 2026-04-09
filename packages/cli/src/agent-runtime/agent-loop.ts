import { streamText, stepCountIs } from 'ai';
import type { ModelMessage } from 'ai';
import type { LanguageModelV3 } from '@ai-sdk/provider';
import type { Session } from './session';
import { SessionManager } from './session';
import { ToolRegistry } from './tool';
import type { EventBus } from '../services/event-bus';

export interface AgentLoopOptions {
  maxSteps?: number;
  system?: string;
  eventBus?: EventBus;
  eventContext?: { issueId: string; projectId: string };
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

  return {
    text,
    steps: allSteps.length,
    finishReason,
  };
}
