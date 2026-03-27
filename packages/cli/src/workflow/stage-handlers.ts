import { Stage, Issue, Task } from '../types';
import { AgentRunner } from '../agent/runner';

export interface StageHandlerContext {
  worktreePath: string;
  projectName: string;
}

export interface StageHandler {
  execute(issue: Issue, task: Task, context: StageHandlerContext): Promise<void>;
}

class DesigningHandler implements StageHandler {
  constructor(private runner: AgentRunner) {}

  async execute(issue: Issue, task: Task, context: StageHandlerContext): Promise<void> {
    await this.runner.runDesignerAgent(issue, task, context.worktreePath, context.projectName);
  }
}

class ImplementingHandler implements StageHandler {
  constructor(private runner: AgentRunner) {}

  async execute(issue: Issue, task: Task, context: StageHandlerContext): Promise<void> {
    const designPath = `openspec/changes/issue-${issue.number}/design.md`;
    await this.runner.runImplementerAgent(issue, task, designPath, context.worktreePath, context.projectName);
  }
}

function nonExecutableStage(stage: Stage): string {
  if (stage === Stage.WaitingDesignReview || stage === Stage.WaitingReview) {
    return `${stage} requires user approval`;
  }
  if (stage === Stage.Done) {
    return 'done is a terminal state';
  }
  if (stage === Stage.Draft) {
    return 'draft must be started first';
  }
  return `${stage} is not an agent-executable stage`;
}

export function getStageHandler(
  stage: Stage,
  runner?: AgentRunner
): StageHandler {
  if (stage === Stage.Designing) {
    if (!runner) throw new Error('AgentRunner required for designing stage');
    return new DesigningHandler(runner);
  }
  if (stage === Stage.Implementing) {
    if (!runner) throw new Error('AgentRunner required for implementing stage');
    return new ImplementingHandler(runner);
  }
  return {
    async execute(): Promise<void> {
      throw new Error(`Cannot execute ${nonExecutableStage(stage)}`);
    }
  };
}
