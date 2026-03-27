import { streamText, stepCountIs } from 'ai';
import type { ModelMessage } from 'ai';
import type { LanguageModelV3 } from '@ai-sdk/provider';
import type { Session } from './session';
import { SessionManager } from './session';
import { ToolRegistry } from './tool';

export interface AgentLoopOptions {
  maxSteps?: number;
  system?: string;
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

  const result = streamText({
    model,
    system: options?.system,
    messages,
    tools,
    stopWhen: stepCountIs(maxSteps),
  });

  await result.consumeStream();

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
