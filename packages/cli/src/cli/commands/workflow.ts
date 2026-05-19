import { Command } from 'commander';
import chalk from 'chalk';
import {
  explainWorkflowItem,
  resolveWorkflowDefinition,
  validateWorkflowDefinition,
  type ExplainedWorkflowItem,
  type ResolvedWorkflowDefinition,
  type WorkflowDiagnostic,
} from '../../workflow/workflow-inspector';

export interface CliOutput {
  write(line?: string): void;
  error(line: string): void;
}

const defaultOutput: CliOutput = {
  write(line = ''): void {
    process.stdout.write(`${line}\n`);
  },
  error(line: string): void {
    process.stderr.write(`${line}\n`);
  },
};

export function setupWorkflowCommands(program: Command, output: CliOutput = defaultOutput): void {
  const workflowCmd = program
    .command('workflow')
    .description('Inspect workflow definitions');

  workflowCmd
    .command('show')
    .description('Show the resolved workflow definition')
    .action(() => {
      renderWorkflowShow(resolveWorkflowDefinition(process.cwd()), output);
    });

  workflowCmd
    .command('validate')
    .description('Validate the resolved workflow definition')
    .action(() => {
      const resolved = resolveWorkflowDefinition(process.cwd());
      const diagnostics = validateWorkflowDefinition(resolved);
      renderWorkflowValidation(diagnostics, output);
      if (diagnostics.some(diagnostic => diagnostic.severity === 'error')) {
        process.exitCode = 1;
      }
    });

  workflowCmd
    .command('explain')
    .description('Explain why a workflow task or check exists')
    .argument('<task-or-check>', 'Task id or check name')
    .action((itemId: string) => {
      const resolved = resolveWorkflowDefinition(process.cwd());
      const item = explainWorkflowItem(itemId, resolved);
      if (!item) {
        output.error(chalk.red(`Workflow item not found: ${itemId}`));
        process.exitCode = 1;
        return;
      }
      renderWorkflowExplanation(item, output);
    });
}

export function renderWorkflowShow(resolved: ResolvedWorkflowDefinition, output: CliOutput = defaultOutput): void {
  const source = resolved.sourceChain.join(' + ');
  output.write(chalk.bold(`Workflow: ${source}`));
  output.write();

  for (const stage of resolved.snapshot.compiledStageDefinitions) {
    output.write(chalk.bold(stage.stage[0].toUpperCase() + stage.stage.slice(1)));
    for (const task of stage.tasks) {
      const policy = stage.taskExecutionPolicies?.find(candidate => candidate.taskId === task.id)
        ?? stage.taskExecutionPolicies?.find(candidate => candidate.taskId === '*');
      output.write(`  Task   ${task.id.padEnd(28)} uses: ${(policy?.kind ?? 'agent-session').padEnd(18)} source: ${task.source ?? 'builtin'}`);
    }
    for (const check of stage.checks) {
      output.write(`  Check  ${check.name.padEnd(28)} uses: ${(check.uses ?? inferCheckUses(check.name)).padEnd(18)} source: ${check.source ?? 'builtin'}`);
    }
    const approvalCheck = stage.approvalPolicy?.checkName ?? stage.approvalCheckName;
    if (approvalCheck) {
      output.write(`  Gate   ${approvalCheck.padEnd(28)} source: builtin`);
    }
    output.write();
  }
}

export function renderWorkflowValidation(diagnostics: WorkflowDiagnostic[], output: CliOutput = defaultOutput): void {
  if (diagnostics.length === 0) {
    output.write(chalk.green('Workflow is valid'));
    output.write('Source: mohist/default');
    return;
  }

  for (const diagnostic of diagnostics) {
    const label = diagnostic.severity === 'error' ? chalk.red('error') : chalk.yellow('warning');
    output.write(`${label} ${diagnostic.path}: ${diagnostic.message}`);
    if (diagnostic.suggestion) {
      output.write(`  suggestion: ${diagnostic.suggestion}`);
    }
  }
}

export function renderWorkflowExplanation(item: ExplainedWorkflowItem, output: CliOutput = defaultOutput): void {
  output.write(chalk.bold(`${item.kind === 'task' ? 'Task' : 'Check'}: ${item.id}`));
  output.write(`Stage: ${item.stage}`);
  output.write(`Title: ${item.title}`);
  output.write(`Source: ${item.source}`);
  output.write(`Uses: ${item.uses}`);

  if (item.kind === 'task') {
    output.write(`Depends on: ${item.dependsOn.length > 0 ? item.dependsOn.join(', ') : 'none'}`);
    if (item.resultContract) output.write(`Result contract: ${item.resultContract}`);
    if (item.selfRepair) output.write('Self repair: enabled');
  } else {
    output.write(`Phase: ${item.phase}`);
    output.write(`Blocking: ${item.blocking ? 'yes' : 'no'}`);
    if (item.reaction) {
      output.write(`Reaction: ${item.reaction.fixTaskId} (${item.reaction.maxAttempts} attempt${item.reaction.maxAttempts === 1 ? '' : 's'})`);
    }
  }
}

function inferCheckUses(checkName: string): string {
  if (checkName.startsWith('health:')) return 'mohist/health-gate';
  if (checkName === 'review-passed' || checkName === 'self-review-passed') return 'mohist/verdict';
  if (checkName === 'merge-ready') return 'mohist/merge-ready';
  return 'mohist/check';
}
