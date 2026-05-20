import type { StageContext } from '../stage-context';
import type { WorkflowDefinitionSnapshot } from './types';

export interface WorkflowTemplateContext {
  issue: {
    number: number;
    title: string;
  };
  worktreePath?: string;
  openspec: {
    changeDir: string;
  };
  artifacts?: Record<string, string>;
}

export function createWorkflowTemplateContextFromValues(input: {
  issueNumber: number;
  issueTitle: string;
  changeDir: string;
  worktreePath?: string;
  artifacts?: Record<string, string>;
}): WorkflowTemplateContext {
  const base: WorkflowTemplateContext = {
    issue: {
      number: input.issueNumber,
      title: input.issueTitle,
    },
    worktreePath: input.worktreePath,
    openspec: {
      changeDir: input.changeDir,
    },
  };
  const artifacts = renderWorkflowArtifacts(input.artifacts, base);
  return {
    ...base,
    artifacts,
  };
}

export function createWorkflowTemplateContext(input: {
  ctx: StageContext;
  worktreePath?: string;
  snapshot?: WorkflowDefinitionSnapshot | null;
}): WorkflowTemplateContext {
  const changeDir = input.ctx.artifactManager.getChangeDir(input.ctx.issue.number)
    || input.ctx.artifactManager.createChangeDir(input.ctx.issue.number, input.ctx.issue.title)
    || '';
  return createWorkflowTemplateContextFromValues({
    issueNumber: input.ctx.issue.number,
    issueTitle: input.ctx.issue.title,
    changeDir,
    worktreePath: input.worktreePath,
    artifacts: input.snapshot?.resolvedDefinition.artifacts,
  });
}

export function renderWorkflowTemplate(template: string, context: WorkflowTemplateContext): string {
  const values = flattenTemplateValues(context);
  return template.replace(/\{\{\s*([a-zA-Z0-9_.]+)\s*\}\}/g, (_match, key: string) => {
    if (!Object.prototype.hasOwnProperty.call(values, key)) {
      throw new Error(`Unknown workflow template variable '${key}'`);
    }
    return values[key] ?? '';
  });
}

function renderWorkflowArtifacts(
  artifacts: Record<string, string> | undefined,
  context: WorkflowTemplateContext,
): Record<string, string> | undefined {
  if (!artifacts) return undefined;
  const rendered: Record<string, string> = {};
  for (const [name, template] of Object.entries(artifacts)) {
    rendered[name] = renderWorkflowTemplate(template, { ...context, artifacts: rendered });
  }
  return rendered;
}

function flattenTemplateValues(context: WorkflowTemplateContext): Record<string, string> {
  const values: Record<string, string> = {
    'issue.number': String(context.issue.number),
    'issue.title': context.issue.title,
    'openspec.changeDir': context.openspec.changeDir,
  };
  if (context.worktreePath) values['worktree.path'] = context.worktreePath;
  for (const [name, value] of Object.entries(context.artifacts ?? {})) {
    values[`artifacts.${name}`] = value;
  }
  return values;
}
