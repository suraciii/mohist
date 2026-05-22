import type { AgentSession, AgentSessionOptions } from '../../../agent-runtime';
import type { RequiredMarkerDefinition } from './agent-required-markers';

type StageContext = any;
type StageTaskResult = any;

export interface AgentSessionTaskInput {
  taskId: string;
  title: string;
  prompt: string;
  cwd: string;
  stage: string;
  attempt: number;
  agentSessionRef?: string;
  artifactVerification?: (artifacts: string[]) => string[];
  retryPromptFactory?: (ctx: StageContext, attempt: number) => string | null;
  requiredMarkers?: RequiredMarkerDefinition[];
}

export interface ServiceCallTaskInput {
  taskId: string;
  title: string;
  serviceFn: (ctx: StageContext) => Promise<unknown>;
  stage: string;
  attempt: number;
}

export type AgentSessionTaskHandler = (
  input: AgentSessionTaskInput,
  ctx: StageContext,
) => Promise<StageTaskResult>;

export type ServiceCallTaskHandler = (
  input: ServiceCallTaskInput,
  ctx: StageContext,
) => Promise<StageTaskResult>;

export type AgentSessionFactory = (options: AgentSessionOptions) => Promise<AgentSession>;
