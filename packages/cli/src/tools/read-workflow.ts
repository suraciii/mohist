import { z } from 'zod';
import { Tool, type ToolInstance } from '../agent-runtime/tool';
import { loadWorkflow, type WorkflowConfig } from '../workflow/workflow-loader';

export interface ReadWorkflowContext {
  cwd: string;
}

function formatWorkflow(config: WorkflowConfig): string {
  const lines = [`# Workflow (source: ${config.source})`, ''];

  for (const stage of config.stages) {
    lines.push(`## ${stage.stage}`);
    lines.push(`- prompt: ${stage.prompt}`);
    if (stage.approval) lines.push('- approval: true');
    if (stage.timeout != null) lines.push(`- timeout: ${stage.timeout}s`);
    lines.push('');
  }

  return lines.join('\n');
}

// eslint-disable-next-line @typescript-eslint/no-explicit-any
export function createReadWorkflowTool(context: ReadWorkflowContext): ToolInstance<any> {
  return Tool.define('read_workflow', {
    description:
      'Read the workflow configuration. Returns the workflow stages, prompt templates, and settings. ' +
      'Call this first to understand the available stages and their prompt templates before using spawn_coder.',
    parameters: z.object({}).strict(),
    execute: async () => {
      const result = loadWorkflow(context.cwd);

      if (typeof result === 'string') {
        return result;
      }

      return formatWorkflow(result);
    },
  });
}
