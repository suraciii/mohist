import type { CheckContext } from './index';
import type { ResultContract, StructuredWorkflowResult, WorkflowVerification } from '../../types/workflow-results';

export interface MarkerFormatMetadata {
  repairedItemIds?: string[];
  verification?: WorkflowVerification[];
}

export interface MarkerFormatHandler {
  enrichStructuredResult?(structured: StructuredWorkflowResult, content: string): StructuredWorkflowResult;
  metadata?(contract: ResultContract, content: string | null): MarkerFormatMetadata;
  enrichOutput?(input: {
    ctx: CheckContext;
    content: string;
    output: Record<string, unknown>;
  }): Promise<Record<string, unknown>> | Record<string, unknown>;
}

const handlers = new Map<string, MarkerFormatHandler>();

export function registerMarkerFormat(format: string, handler: MarkerFormatHandler): void {
  handlers.set(format, handler);
}

export function getMarkerFormat(format: string | undefined): MarkerFormatHandler | undefined {
  return format ? handlers.get(format) : undefined;
}
