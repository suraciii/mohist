import { z } from 'zod';
import { tool } from 'ai';
import type { ToolSet } from 'ai';
import { truncate } from '../services/truncate-service.js';

export interface ToolResult {
  output: string;
  metadata?: {
    truncated?: boolean;
    outputPath?: string;
    [key: string]: unknown;
  };
}

export interface ToolDefinition<P = unknown> {
  id: string;
  description: string;
  parameters: z.ZodType<P>;
  execute: (params: P) => Promise<string | ToolResult>;
}

export interface ToolInstance<P = unknown> {
  definition: ToolDefinition<P>;
  aiTool: ToolSet[string];
}

function normalizeResult(raw: string | ToolResult): ToolResult {
  return typeof raw === 'string' ? { output: raw } : raw;
}

export namespace Tool {
  export function define<P>(
    id: string,
    def: {
      description: string;
      parameters: z.ZodType<P>;
      execute: (params: P) => Promise<string | ToolResult>;
    }
  ): ToolInstance<P> {
    const definition: ToolDefinition<P> = { id, ...def };

    const aiTool = tool({
      description: def.description,
      inputSchema: def.parameters as z.ZodType,
      execute: async (params) => {
        const parsed = def.parameters.safeParse(params);
        if (!parsed.success) {
          return `Validation error: ${parsed.error.issues.map((i) => i.message).join(', ')}`;
        }

        const raw = await def.execute(parsed.data as P);
        const result = normalizeResult(raw);

        if (result.metadata?.truncated) {
          return result.output;
        }

        const truncated = await truncate(result.output);
        if (truncated.truncated) {
          return truncated.content;
        }

        return result.output;
      },
    });

    return { definition, aiTool };
  }
}

export class ToolRegistry {
  private tools = new Map<string, ToolInstance>();
  private currentExecutionId: string | null = null;

  register(instance: ToolInstance): void {
    this.tools.set(instance.definition.id, instance);
  }

  get(id: string): ToolInstance | undefined {
    return this.tools.get(id);
  }

  getAll(): ToolInstance[] {
    return Array.from(this.tools.values());
  }

  clear(): void {
    this.tools.clear();
  }

  setCurrentExecutionId(id: string): void {
    this.currentExecutionId = id;
  }

  getCurrentExecutionId(): string | null {
    return this.currentExecutionId;
  }

  clearCurrentExecutionId(): void {
    this.currentExecutionId = null;
  }

  toToolSet(): ToolSet {
    const set: ToolSet = {};
    for (const instance of this.tools.values()) {
      (set as Record<string, unknown>)[instance.definition.id] = instance.aiTool;
    }
    return set;
  }
}
