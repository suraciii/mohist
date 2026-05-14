import { Issue, Stage, IssueStatus, MergeState, IssueStartPrerequisite } from '../types';
import { IssueRepo, IssueStartPrerequisiteRepo } from '../db';

export interface IssuePrerequisiteSummary {
  issueId: string;
  number: number;
  title: string;
  delivered: boolean;
  stage: Stage;
  status: IssueStatus;
  mergeState?: MergeState | null;
}

export interface IssueStartEligibility {
  startable: boolean;
  reason: 'ready' | 'not-startable-lifecycle' | 'waiting-for-delivery';
  message?: string;
  waitingForDelivery: IssuePrerequisiteSummary[];
}

export interface IssuePrerequisiteView {
  prerequisites: IssuePrerequisiteSummary[];
  startEligibility: IssueStartEligibility;
}

export class IssuePrerequisiteService {
  constructor(
    private issueRepo: IssueRepo,
    private prerequisiteRepo: IssueStartPrerequisiteRepo,
  ) {}

  declarePrerequisite(projectId: string, issueNumber: number, prerequisiteNumber: number): IssuePrerequisiteView | { error: string; reason: 'circular-prerequisite' | 'not-found' | 'same-issue' } {
    const issue = this.issueRepo.findByNumber(projectId, issueNumber);
    if (!issue) {
      return { error: `Issue #${issueNumber} not found`, reason: 'not-found' };
    }

    const prerequisiteIssue = this.issueRepo.findByNumber(projectId, prerequisiteNumber);
    if (!prerequisiteIssue) {
      return { error: `Prerequisite issue #${prerequisiteNumber} not found`, reason: 'not-found' };
    }

    if (issue.id === prerequisiteIssue.id) {
      return { error: 'An issue cannot be a prerequisite of itself', reason: 'same-issue' };
    }

    if (this.wouldCreateCycle(issue.id, prerequisiteIssue.id)) {
      return { error: 'Circular prerequisite declaration', reason: 'circular-prerequisite' };
    }

    this.prerequisiteRepo.create(issue.id, prerequisiteIssue.id);

    return this.getPrerequisiteView(projectId, issue);
  }

  removePrerequisite(projectId: string, issueNumber: number, prerequisiteNumber: number): boolean {
    const issue = this.issueRepo.findByNumber(projectId, issueNumber);
    if (!issue) return false;

    const prerequisiteIssue = this.issueRepo.findByNumber(projectId, prerequisiteNumber);
    if (!prerequisiteIssue) return false;

    return this.prerequisiteRepo.delete(issue.id, prerequisiteIssue.id);
  }

  getPrerequisiteView(_projectId: string, issue: Issue): IssuePrerequisiteView {
    const prerequisites = this.getPrerequisiteSummaries(issue.id);
    const startEligibility = this.evaluateStartEligibility(issue, prerequisites);
    return { prerequisites, startEligibility };
  }

  getPrerequisiteViews(_projectId: string, issues: Issue[]): Map<string, IssuePrerequisiteView> {
    const result = new Map<string, IssuePrerequisiteView>();

    if (issues.length === 0) return result;

    const issueIds = issues.map(i => i.id);
    const storedPrerequisites = this.prerequisiteRepo.findAllByIssues(issueIds);

    const prereqIssueIds = [...new Set(storedPrerequisites.map(p => p.prerequisiteIssueId))];
    const prereqIssues = new Map<string, Issue>();
    for (const pid of prereqIssueIds) {
      const pi = this.issueRepo.findById(pid);
      if (pi) prereqIssues.set(pid, pi);
    }

    const prereqsByIssue = new Map<string, IssueStartPrerequisite[]>();
    for (const sp of storedPrerequisites) {
      if (!prereqsByIssue.has(sp.issueId)) {
        prereqsByIssue.set(sp.issueId, []);
      }
      prereqsByIssue.get(sp.issueId)!.push(sp);
    }

    for (const issue of issues) {
      const prereqs = prereqsByIssue.get(issue.id) || [];
      const summaries: IssuePrerequisiteSummary[] = prereqs
        .map(p => {
          const prereqIssue = prereqIssues.get(p.prerequisiteIssueId);
          if (!prereqIssue) return null;
          return this.buildPrerequisiteSummary(prereqIssue);
        })
        .filter((s): s is IssuePrerequisiteSummary => s !== null);

      const startEligibility = this.evaluateStartEligibility(issue, summaries);
      result.set(issue.id, { prerequisites: summaries, startEligibility });
    }

    return result;
  }

  evaluateStartEligibility(issue: Issue, prerequisites?: IssuePrerequisiteSummary[]): IssueStartEligibility {
    if (!prerequisites) {
      const stored = this.prerequisiteRepo.findByIssue(issue.id);
      const prereqIssues = stored.map(p => this.issueRepo.findById(p.prerequisiteIssueId)).filter((i): i is Issue => i !== null);
      prerequisites = prereqIssues.map(i => this.buildPrerequisiteSummary(i));
    }

    const waitingForDelivery = prerequisites.filter(p => !p.delivered);
    const lifecycleRejection = this.getLifecycleStartRejection(issue, waitingForDelivery);
    if (lifecycleRejection) {
      return lifecycleRejection;
    }

    if (waitingForDelivery.length > 0) {
      const firstWaiting = waitingForDelivery[0];
      return {
        startable: false,
        reason: 'waiting-for-delivery',
        message: `Issue #${issue.number} is waiting for prerequisite #${firstWaiting.number} to be delivered.`,
        waitingForDelivery,
      };
    }

    return {
      startable: true,
      reason: 'ready',
      waitingForDelivery: [],
    };
  }

  assertStartEligible(_projectId: string, issue: Issue): IssueStartEligibility {
    const view = this.getPrerequisiteView(_projectId, issue);
    return view.startEligibility;
  }

  private getPrerequisiteSummaries(issueId: string): IssuePrerequisiteSummary[] {
    const stored = this.prerequisiteRepo.findByIssue(issueId);
    const summaries: IssuePrerequisiteSummary[] = [];

    for (const s of stored) {
      const prereqIssue = this.issueRepo.findById(s.prerequisiteIssueId);
      if (prereqIssue) {
        summaries.push(this.buildPrerequisiteSummary(prereqIssue));
      }
    }

    return summaries;
  }

  private buildPrerequisiteSummary(issue: Issue): IssuePrerequisiteSummary {
    return {
      issueId: issue.id,
      number: issue.number,
      title: issue.title,
      delivered: this.isDelivered(issue),
      stage: issue.stage,
      status: issue.status,
      mergeState: issue.mergeState,
    };
  }

  private isDelivered(issue: Issue): boolean {
    return (
      issue.stage === Stage.Done &&
      issue.status === IssueStatus.Completed &&
      issue.mergeState === MergeState.Merged
    );
  }

  private getLifecycleStartRejection(issue: Issue, waitingForDelivery: IssuePrerequisiteSummary[]): IssueStartEligibility | null {
    let message: string | null = null;

    if (issue.status === IssueStatus.Blocked) {
      message = `Issue #${issue.number} is blocked. Use: mo issue retry ${issue.number} or mo issue rerun ${issue.number}`;
    } else if (issue.status === IssueStatus.Closed) {
      message = `Issue #${issue.number} is closed. Run: mo issue reopen ${issue.number}`;
    } else if (issue.status === IssueStatus.Paused) {
      message = `Issue #${issue.number} is paused. Run: mo issue approve ${issue.number} to resume`;
    } else if (issue.stage !== Stage.Backlog) {
      message = `Issue #${issue.number} is not in a startable stage (current: ${issue.stage}). Only backlog issues can be started.`;
    }

    if (!message) return null;
    return {
      startable: false,
      reason: 'not-startable-lifecycle',
      message,
      waitingForDelivery,
    };
  }

  private wouldCreateCycle(issueId: string, prerequisiteIssueId: string): boolean {
    if (issueId === prerequisiteIssueId) return true;

    const visited = new Set<string>();
    const stack = [prerequisiteIssueId];

    while (stack.length > 0) {
      const current = stack.pop()!;
      if (current === issueId) return true;
      if (visited.has(current)) continue;
      visited.add(current);

      const prerequisites = this.prerequisiteRepo.findByIssue(current);
      for (const p of prerequisites) {
        stack.push(p.prerequisiteIssueId);
      }
    }

    return false;
  }
}
