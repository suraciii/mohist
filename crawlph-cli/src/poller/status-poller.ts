import { GitHubClient } from '../github/client';
import { Stage } from '../types';

export class StatusPoller {
  private githubClient: GitHubClient;
  private interval: number;
  private timer?: NodeJS.Timeout;
  private isRunning: boolean = false;

  constructor(
    githubClient: GitHubClient,
    _taskQueue: unknown,
    interval: number = 60000
  ) {
    this.githubClient = githubClient;
    this.interval = interval;
  }

  start(): void {
    if (this.isRunning) {
      console.log('Poller is already running');
      return;
    }

    this.isRunning = true;
    console.log(`Starting status poller (interval: ${this.interval}ms)`);

    this.poll();
    
    this.timer = setInterval(() => {
      this.poll();
    }, this.interval);
  }

  stop(): void {
    if (this.timer) {
      clearInterval(this.timer);
      this.timer = undefined;
    }
    this.isRunning = false;
    console.log('Status poller stopped');
  }

  private async poll(): Promise<void> {
    try {
      console.log('Polling GitHub for updates...');

      await this.checkForNewDraftIssues();
      await this.checkPullRequestStatuses();
      
      console.log('Poll complete');
    } catch (error) {
      console.error('Poll error:', error);
    }
  }

  private async checkForNewDraftIssues(): Promise<void> {
    try {
      const issues = await this.githubClient.getIssues(['crawlph:stage/draft']);
      
      for (const issue of issues) {
        console.log(`Found draft issue #${issue.number}: ${issue.title}`);
      }
    } catch (error) {
      console.error('Failed to check for draft issues:', error);
    }
  }

  private async checkPullRequestStatuses(): Promise<void> {
    try {
      const issues = await this.githubClient.getIssues();
      
      const prIssues = issues.filter(
        issue => 
          issue.stage === Stage.WaitingReview || 
          issue.stage === Stage.Merging
      );

      for (const issue of prIssues) {
        if (issue.prNumber) {
          const pr = await this.githubClient.getPullRequest(issue.prNumber);
          
          if (pr) {
            if (pr.merged) {
              console.log(`PR #${pr.number} for issue #${issue.number} has been merged`);
            } else if (pr.approved) {
              console.log(`PR #${pr.number} for issue #${issue.number} has been approved`);
            }
          }
        }
      }
    } catch (error) {
      console.error('Failed to check PR statuses:', error);
    }
  }

  getStats(): { isRunning: boolean; interval: number } {
    return {
      isRunning: this.isRunning,
      interval: this.interval
    };
  }
}
