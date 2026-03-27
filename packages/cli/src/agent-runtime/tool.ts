import { z } from 'zod';
import { tool } from 'ai';
import type { ToolSet } from 'ai';

export interface ToolDefinition<P = unknown> {
  id: string;
  description: string;
  parameters: z.ZodType<P>;
  execute: (params: P) => Promise<string>;
}

export interface ToolInstance<P = unknown> {
  definition: ToolDefinition<P>;
  aiTool: ToolSet[string];
}

export namespace Tool {
  export function define<P>(
    id: string,
    def: {
      description: string;
      parameters: z.ZodType<P>;
      execute: (params: P) => Promise<string>;
    }
  ): ToolInstance<P> {
    const definition: ToolDefinition<P> = { id, ...def };

    const aiTool = tool({
      description: def.description,
      inputSchema: def.parameters as z.ZodType,
      execute: async (params) => {
        const result = def.parameters.safeParse(params);
        if (!result.success) {
          return `Validation error: ${result.error.issues.map((i) => i.message).join(', ')}`;
        }
        return def.execute(result.data as P);
      },
    });

    return { definition, aiTool };
  }
}

export class ToolRegistry {
  private tools = new Map<string, ToolInstance>();

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

  toToolSet(): ToolSet {
    const set: ToolSet = {};
    for (const instance of this.tools.values()) {
      (set as Record<string, unknown>)[instance.definition.id] = instance.aiTool;
    }
    return set;
  }
}
