import { Octokit } from '@octokit/rest';
import { Issue, PullRequest, Stage, IssueStatus } from '../types';
import { parseStageFromLabel, parseStatusFromLabel } from '../workflow/issue-workflow';

export type OctokitLike = Pick<Octokit, 'issues' | 'pulls' | 'repos' | 'projects'>;

export class GitHubClient {
  private octokit: OctokitLike;
  private owner: string;
  private repo: string;

  constructor(token: string, owner: string, repo: string, octokit?: OctokitLike) {
    this.octokit = octokit ?? new Octokit({ auth: token });
    this.owner = owner;
    this.repo = repo;
  }

  getRepoUrl(): string {
    return `https://github.com/${this.owner}/${this.repo}`;
  }

  async getIssues(labels?: string[]): Promise<Issue[]> {
    try {
      const response = await this.octokit.issues.listForRepo({
        owner: this.owner,
        repo: this.repo,
        labels: labels?.join(','),
        state: 'open'
      });

      return response.data.map(issue => this.parseIssue(issue));
    } catch (error) {
      console.error('Failed to fetch issues:', error);
      throw error;
    }
  }

  async getIssue(number: number): Promise<Issue | null> {
    try {
      const response = await this.octokit.issues.get({
        owner: this.owner,
        repo: this.repo,
        issue_number: number
      });

      return this.parseIssue(response.data);
    } catch (error) {
      console.error(`Failed to fetch issue #${number}:`, error);
      return null;
    }
  }

  async addLabel(issueNumber: number, label: string): Promise<void> {
    try {
      await this.octokit.issues.addLabels({
        owner: this.owner,
        repo: this.repo,
        issue_number: issueNumber,
        labels: [label]
      });
      console.log(`Added label "${label}" to issue #${issueNumber}`);
    } catch (error) {
      console.error('Failed to add label:', error);
      throw error;
    }
  }

  async removeLabel(issueNumber: number, label: string): Promise<void> {
    try {
      await this.octokit.issues.removeLabel({
        owner: this.owner,
        repo: this.repo,
        issue_number: issueNumber,
        name: label
      });
      console.log(`Removed label "${label}" from issue #${issueNumber}`);
    } catch (error) {
      console.error('Failed to remove label:', error);
      throw error;
    }
  }

  async hasLabel(issueNumber: number, label: string): Promise<boolean> {
    try {
      const issue = await this.getIssue(issueNumber);
      return issue?.labels.includes(label) || false;
    } catch (error) {
      console.error('Failed to check label:', error);
      return false;
    }
  }

  async transitionStage(issueNumber: number, newStage: Stage): Promise<void> {
    try {
      const issue = await this.getIssue(issueNumber);
      if (!issue) {
        throw new Error(`Issue #${issueNumber} not found`);
      }

      const stageLabelPrefix = 'crawlph:stage/';
      const stageLabels = issue.labels.filter(l => l.startsWith(stageLabelPrefix));
      
      for (const label of stageLabels) {
        await this.removeLabel(issueNumber, label);
      }
      
      await this.addLabel(issueNumber, `${stageLabelPrefix}${newStage}`);
    } catch (error) {
      console.error('Failed to transition stage:', error);
      throw error;
    }
  }

  async setStatus(issueNumber: number, status: IssueStatus): Promise<void> {
    try {
      const issue = await this.getIssue(issueNumber);
      if (!issue) {
        throw new Error(`Issue #${issueNumber} not found`);
      }

      const statusLabelPrefix = 'crawlph:status/';
      const statusLabels = issue.labels.filter(l => l.startsWith(statusLabelPrefix));
      
      for (const label of statusLabels) {
        await this.removeLabel(issueNumber, label);
      }
      
      await this.addLabel(issueNumber, `${statusLabelPrefix}${status}`);
    } catch (error) {
      console.error('Failed to set status:', error);
      throw error;
    }
  }

  async getPullRequest(number: number): Promise<PullRequest | null> {
    try {
      const response = await this.octokit.pulls.get({
        owner: this.owner,
        repo: this.repo,
        pull_number: number
      });

      const reviews = await this.octokit.pulls.listReviews({
        owner: this.owner,
        repo: this.repo,
        pull_number: number
      });

      const approved = reviews.data.some(review => review.state === 'APPROVED');

      const issueNumber = this.extractIssueNumberFromBody(response.data.body);

      return {
        number: response.data.number,
        title: response.data.title,
        state: response.data.state as 'open' | 'closed',
        draft: response.data.draft || false,
        mergeable: response.data.mergeable || undefined,
        merged: response.data.merged || false,
        approved,
        headBranch: response.data.head.ref,
        baseBranch: response.data.base.ref,
        url: response.data.html_url,
        issueNumber,
        createdAt: response.data.created_at,
        updatedAt: response.data.updated_at
      };
    } catch (error) {
      console.error(`Failed to fetch PR #${number}:`, error);
      return null;
    }
  }

  async createPullRequest(
    title: string,
    head: string,
    base: string,
    body?: string,
    issueNumber?: number
  ): Promise<PullRequest | null> {
    try {
      let prBody = body || '';
      if (issueNumber) {
        prBody = `Closes #${issueNumber}\n\n${prBody}`;
      }

      const response = await this.octokit.pulls.create({
        owner: this.owner,
        repo: this.repo,
        title,
        head,
        base,
        body: prBody
      });

      return {
        number: response.data.number,
        title: response.data.title,
        state: response.data.state as 'open' | 'closed',
        draft: response.data.draft || false,
        merged: false,
        approved: false,
        headBranch: response.data.head.ref,
        baseBranch: response.data.base.ref,
        url: response.data.html_url,
        issueNumber,
        createdAt: response.data.created_at,
        updatedAt: response.data.updated_at
      };
    } catch (error) {
      console.error('Failed to create PR:', error);
      throw error;
    }
  }

  async mergePR(number: number, commitMessage?: string): Promise<boolean> {
    try {
      await this.octokit.pulls.merge({
        owner: this.owner,
        repo: this.repo,
        pull_number: number,
        commit_message: commitMessage || `Merge PR #${number}`,
        merge_method: 'squash'
      });
      console.log(`Merged PR #${number}`);
      return true;
    } catch (error) {
      console.error('Failed to merge PR:', error);
      throw error;
    }
  }

  async approvePR(number: number, message?: string): Promise<void> {
    try {
      await this.octokit.pulls.createReview({
        owner: this.owner,
        repo: this.repo,
        pull_number: number,
        event: 'APPROVE',
        body: message || 'Approved'
      });
      console.log(`Approved PR #${number}`);
    } catch (error) {
      console.error('Failed to approve PR:', error);
      throw error;
    }
  }

  private parseIssue(data: any): Issue {
    const labels: string[] = data.labels.map((l: any) => l.name);
    
    let stage: Stage = Stage.Draft;
    let status: IssueStatus = IssueStatus.Active;
    
    for (const label of labels) {
      const parsedStage = parseStageFromLabel(label);
      if (parsedStage) {
        stage = parsedStage;
      }
      
      const parsedStatus = parseStatusFromLabel(label);
      if (parsedStatus) {
        status = parsedStatus;
      }
    }

    const prNumber = this.extractPRNumberFromIssue(data.body);

    return {
      number: data.number,
      title: data.title,
      body: data.body || undefined,
      stage,
      status,
      labels,
      projectId: `${this.owner}/${this.repo}`,
      prNumber,
      url: data.html_url,
      createdAt: data.created_at,
      updatedAt: data.updated_at
    };
  }

  private extractIssueNumberFromBody(body?: string | null): number | undefined {
    if (!body) return undefined;
    const match = body.match(/(?:Closes|Fixes|Resolves)\s*#(\d+)/i);
    return match ? parseInt(match[1], 10) : undefined;
  }

  private extractPRNumberFromIssue(body?: string | null): number | undefined {
    if (!body) return undefined;
    const match = body.match(/PR:\s*#?(\d+)/i);
    return match ? parseInt(match[1], 10) : undefined;
  }
}
