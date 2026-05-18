import {
  Epic,
  EpicProgress,
  EpicWithProgress,
  EpicDetail,
  LinkedIssue,
  IssueStatus,
  EpicStatus,
  Stage,
} from '../types';
import { EpicRepo, CreateEpicData } from '../db/epic-repo';
import { IssueRepo } from '../db/issue-repo';

export class DuplicateEpicMembershipError extends Error {
  constructor(
    public readonly issueId: string,
    public readonly existingEpicId: string,
    public readonly existingEpicTitle: string
  ) {
    super(`Issue already belongs to Epic "${existingEpicTitle}"`);
    this.name = 'DuplicateEpicMembershipError';
  }
}

export class CrossProjectEpicMembershipError extends Error {
  constructor(
    public readonly epicProjectId: string,
    public readonly issueProjectId: string,
    public readonly issueId: string
  ) {
    super('Issue belongs to a different project than this Epic');
    this.name = 'CrossProjectEpicMembershipError';
  }
}

function isUniqueIssueMembershipError(error: unknown): boolean {
  if (!(error instanceof Error)) {
    return false;
  }

  const message = error.message.toLowerCase();
  return message.includes('unique constraint failed') && message.includes('epic_issues.issue_id');
}

function isDeliveredIssue(issue: LinkedIssue): boolean {
  return issue.status === IssueStatus.Closed || issue.status === IssueStatus.Completed;
}

function isBlockedLikeIssue(issue: LinkedIssue): boolean {
  return issue.status === IssueStatus.Blocked || issue.status === IssueStatus.Interrupted;
}

function isActiveIssue(issue: LinkedIssue): boolean {
  return issue.status === IssueStatus.Active && issue.stage !== Stage.Backlog;
}

function isBacklogIssue(issue: LinkedIssue): boolean {
  return issue.status === IssueStatus.Active && issue.stage === Stage.Backlog;
}

export class EpicService {
  constructor(
    private epicRepo: EpicRepo,
    private issueRepo?: IssueRepo
  ) {}

  create(data: CreateEpicData): Epic {
    if (!data.title || data.title.trim().length === 0) {
      throw new Error('title is required');
    }
    if (!data.description) {
      throw new Error('description is required');
    }
    if (!data.priority || !['p0', 'p1', 'p2', 'p3', 'p4'].includes(data.priority)) {
      throw new Error('Invalid priority');
    }
    return this.epicRepo.create(data);
  }

  list(projectId: string): EpicWithProgress[] {
    const epics = this.epicRepo.findAll(projectId);
    return epics.map(epic => this.withProgress(epic));
  }

  getById(projectId: string, id: string): EpicDetail | null {
    const epic = this.epicRepo.findById(projectId, id);
    if (!epic) return null;
    return this.withDetail(epic);
  }

  addIssue(projectId: string, epicId: string, issueId: string): void {
    const epic = this.epicRepo.findById(projectId, epicId);
    if (!epic) {
      throw new Error('Epic not found');
    }

    const issue = this.issueRepo?.findById(issueId);
    if (this.issueRepo && !issue) {
      throw new Error('Issue not found');
    }

    if (issue && issue.projectId !== epic.projectId) {
      throw new CrossProjectEpicMembershipError(epic.projectId, issue.projectId, issueId);
    }

    const existingEpic = this.epicRepo.findEpicByIssueId(issueId);
    if (existingEpic) {
      throw new DuplicateEpicMembershipError(issueId, existingEpic.id, existingEpic.title);
    }

    try {
      this.epicRepo.addIssue(epicId, issueId);
    } catch (error) {
      if (isUniqueIssueMembershipError(error)) {
        const existing = this.epicRepo.findEpicByIssueId(issueId);
        if (existing) {
          throw new DuplicateEpicMembershipError(issueId, existing.id, existing.title);
        }
      }
      throw error;
    }
  }

  removeIssue(projectId: string, epicId: string, issueId: string): void {
    const epic = this.epicRepo.findById(projectId, epicId);
    if (!epic) {
      throw new Error('Epic not found');
    }

    const removed = this.epicRepo.removeIssue(epicId, issueId);
    if (!removed) {
      throw new Error('Issue is not linked to this Epic');
    }
  }

  getLinkedIssues(epicId: string): LinkedIssue[] {
    return this.epicRepo.getLinkedIssues(epicId);
  }

  getIssueEpic(projectId: string, issueId: string): { id: string; title: string; status: string; priority: string } | null {
    return this.epicRepo.getIssueEpicSummary(projectId, issueId);
  }

  markDone(projectId: string, id: string): Epic | null {
    const epic = this.epicRepo.findById(projectId, id);
    if (!epic) return null;
    if (epic.status !== EpicStatus.Active) {
      throw new Error('Only active Epics can be marked done');
    }
    return this.epicRepo.updateStatus(projectId, id, EpicStatus.Done);
  }

  close(projectId: string, id: string): Epic | null {
    const epic = this.epicRepo.findById(projectId, id);
    if (!epic) return null;
    if (epic.status === EpicStatus.Closed) {
      throw new Error('Epic is already closed');
    }
    return this.epicRepo.updateStatus(projectId, id, EpicStatus.Closed);
  }

  private withProgress(epic: Epic): EpicWithProgress {
    const progress = this.computeProgress(epic.id, this.epicRepo.getLinkedIssues(epic.id));
    return {
      ...epic,
      progress,
    };
  }

  private withDetail(epic: Epic): EpicDetail {
    const linkedIssues = this.epicRepo.getLinkedIssues(epic.id);
    const progress = this.computeProgress(epic.id, linkedIssues);
    return {
      ...epic,
      linkedIssues,
      progress,
    };
  }

  private computeProgress(_epicId: string, linkedIssues: LinkedIssue[]): EpicProgress {
    if (linkedIssues.length === 0) {
      return {
        deliveredCount: 0,
        totalIssueCount: 0,
        blockedIssues: [],
        activeIssues: [],
        nextIssue: null,
        readyToMarkDone: false,
      };
    }

    const deliveredCount = linkedIssues.filter(isDeliveredIssue).length;

    const blockedIssues = linkedIssues
      .filter(isBlockedLikeIssue)
      .map(i => i.id);

    const activeIssues = linkedIssues
      .filter(isActiveIssue)
      .map(i => i.id);

    let nextIssue: { id: string; number: number; title: string } | null = null;
    let readyToMarkDone = false;

    const blocked = linkedIssues.filter(isBlockedLikeIssue);
    if (blocked.length > 0) {
      nextIssue = { id: blocked[0].id, number: blocked[0].number, title: blocked[0].title };
    } else {
      const active = linkedIssues.filter(isActiveIssue);
      if (active.length > 0) {
        nextIssue = { id: active[0].id, number: active[0].number, title: active[0].title };
      } else {
        const backlog = linkedIssues.filter(isBacklogIssue);
        if (backlog.length > 0) {
          nextIssue = { id: backlog[0].id, number: backlog[0].number, title: backlog[0].title };
        } else {
          readyToMarkDone = deliveredCount === linkedIssues.length;
        }
      }
    }

    return {
      deliveredCount,
      totalIssueCount: linkedIssues.length,
      blockedIssues,
      activeIssues,
      nextIssue,
      readyToMarkDone,
    };
  }
}
