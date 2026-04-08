import { z } from 'zod';
import { Tool, type ToolInstance } from '../agent-runtime/tool';
import { loadWorkflowWithDetection, type WorkflowConfigWithDetection } from '../workflow/workflow-loader';

export interface ReadWorkflowContext {
  cwd: string;
  issueNumber: number;
}

function formatWorkflow(config: WorkflowConfigWithDetection): string {
  const lines = [`# Workflow (source: ${config.source})`, ''];

  for (const stage of config.stages) {
    lines.push(`## ${stage.stage}`);
    lines.push(`- prompt: ${stage.prompt}`);
    if (stage.approval) lines.push('- approval: true');
    if (stage.timeout != null) lines.push(`- timeout: ${stage.timeout}s`);
    lines.push('');
  }

  lines.push('## OpenSpec Detection');
  if (config.openspec.mode === 'openspec') {
    lines.push(`- **Change detected**: YES`);
    lines.push(`- **Change path**: ${config.openspec.changePath}`);
    lines.push(`- **PRD**: ${config.openspec.prdPath}`);
    lines.push(`- **Execution mode**: Ralph-style task loop (use \`run_ralph_loop\` in build stage)`);
  } else if (config.openspec.detected) {
    lines.push(`- **Change directory detected**: YES (but no prd.json yet)`);
    lines.push(`- **Change path**: ${config.openspec.changePath}`);
    lines.push(`- **Execution mode**: Traditional (plan stage in progress)`);
  } else {
    lines.push(`- **Change detected**: NO`);
    lines.push(`- **Execution mode**: Traditional (use \`spawn_coder\` for all stages)`);
  }

  return lines.join('\n');
}

// eslint-disable-next-line @typescript-eslint/no-explicit-any
export function createReadWorkflowTool(context: ReadWorkflowContext): ToolInstance<any> {
  return Tool.define('read_workflow', {
    description:
      'Read the workflow configuration. Returns the workflow stages, prompt templates, settings, ' +
      'and OpenSpec Change detection result. Call this first to understand the available stages, ' +
      'whether to use run_ralph_loop or spawn_coder, and the current execution mode.',
    parameters: z.object({}).strict(),
    execute: async () => {
      const result = loadWorkflowWithDetection(context.cwd, context.issueNumber);

      if (typeof result === 'string') {
        return result;
      }

      return formatWorkflow(result);
    },
  });
}
