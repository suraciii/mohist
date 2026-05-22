import type { IssueService, ProjectService } from '../services';
import { WorkflowRuntime } from '@mohist/workflow';
import { MOHIST_DEFAULT_WORKFLOW_DEFINITION } from '../workflow/runtime/definition';

type Issue = NonNullable<ReturnType<IssueService['getByNumber']>>;

export type IssueWorkflowResult<T = unknown> =
  | { ok: true; data: T }
  | { ok: false; error: string; data?: unknown };

export type IssueWorkflowServiceDeps = {
  issueService: IssueService;
  projectService: ProjectService;
  runtime: WorkflowRuntime;
};

export class IssueWorkflowService {
  constructor(private readonly deps: IssueWorkflowServiceDeps) {}

  async start(number: number): Promise<IssueWorkflowResult> {
    const found = this.requireIssue(number);
    if (!found.ok) return found;

    const { issue } = found;
    const project = this.deps.projectService.getById(found.projectId);
    if (!project) return this.fail('Project not found');

    const workflowRunId = `wr_${issue.projectId}_${issue.number}`;
    const runner = await this.deps.runtime.load(workflowRunId) ?? await this.deps.runtime.create({
      id: workflowRunId,
      definition: MOHIST_DEFAULT_WORKFLOW_DEFINITION,
    });

    await runner.start();

    return this.ok({
      workflowRunId: runner.id,
      status: runner.status,
      currentStage: runner.currentStage,
    });
  }

  async stop(number: number): Promise<IssueWorkflowResult> {
    const found = this.requireIssue(number);
    if (!found.ok) return found;

    const runner = await this.loadRunner(found.issue);
    if (!runner) return this.fail('No running workflow');

    await runner.pause();

    return this.ok({
      workflowRunId: runner.id,
      status: runner.status,
      currentStage: runner.currentStage,
    });
  }

  async resume(number: number): Promise<IssueWorkflowResult> {
    const found = this.requireIssue(number);
    if (!found.ok) return found;

    const runner = await this.loadRunner(found.issue);
    if (!runner) return this.fail('No workflow to resume');

    await runner.resume();

    return this.ok({
      workflowRunId: runner.id,
      status: runner.status,
      currentStage: runner.currentStage,
    });
  }

  async approve(number: number): Promise<IssueWorkflowResult> {
    const found = this.requireIssue(number);
    if (!found.ok) return found;

    const runner = await this.loadRunner(found.issue);
    if (!runner) return this.fail('No workflow awaiting approval');

    await runner.approve();

    return this.ok({
      workflowRunId: runner.id,
      status: runner.status,
      currentStage: runner.currentStage,
    });
  }

  async reject(number: number, reason?: string): Promise<IssueWorkflowResult> {
    const found = this.requireIssue(number);
    if (!found.ok) return found;

    const runner = await this.loadRunner(found.issue);
    if (!runner) return this.fail('No workflow to reject');

    await runner.reject(reason);

    return this.ok({
      workflowRunId: runner.id,
      status: runner.status,
      currentStage: runner.currentStage,
    });
  }

  async retry(number: number): Promise<IssueWorkflowResult> {
    const found = this.requireIssue(number);
    if (!found.ok) return found;

    const runner = await this.loadRunner(found.issue);
    if (!runner) return this.fail('No workflow to retry');

    await runner.retry();

    return this.ok({
      workflowRunId: runner.id,
      status: runner.status,
      currentStage: runner.currentStage,
    });
  }

  async rerun(number: number): Promise<IssueWorkflowResult> {
    const found = this.requireIssue(number);
    if (!found.ok) return found;

    const { issue } = found;
    const workflowRunId = `wr_${issue.projectId}_${issue.number}`;
    const runner = await this.deps.runtime.create({
      id: workflowRunId,
      definition: MOHIST_DEFAULT_WORKFLOW_DEFINITION,
    });

    await runner.start();

    return this.ok({
      workflowRunId: runner.id,
      status: runner.status,
      currentStage: runner.currentStage,
    });
  }

  async rebase(number: number): Promise<IssueWorkflowResult> {
    const found = this.requireIssue(number);
    if (!found.ok) return found;

    const runner = await this.loadRunner(found.issue);
    if (!runner) return this.fail('No workflow to rebase');

    await runner.resume();

    return this.ok({
      workflowRunId: runner.id,
      status: runner.status,
      currentStage: runner.currentStage,
    });
  }

  logs(_number: number, _eventType?: string): IssueWorkflowResult {
    return this.fail('Not implemented');
  }

  private async loadRunner(issue: Issue) {
    const workflowRunId = `wr_${issue.projectId}_${issue.number}`;
    return this.deps.runtime.load(workflowRunId);
  }

  private requireIssue(number: number):
    | { ok: true; projectId: string; issue: Issue }
    | { ok: false; error: string } {
    const projectId = this.deps.projectService.getCurrentId();
    if (!projectId) return { ok: false, error: 'No active project' };

    const issue = this.deps.issueService.getByNumber(projectId, number);
    if (!issue) return { ok: false, error: `Issue #${number} not found` };
    return { ok: true, projectId, issue };
  }

  private ok<T>(data: T): IssueWorkflowResult<T> {
    return { ok: true, data };
  }

  private fail(error: string, data?: unknown): IssueWorkflowResult<never> {
    return data === undefined ? { ok: false, error } : { ok: false, error, data };
  }
}
