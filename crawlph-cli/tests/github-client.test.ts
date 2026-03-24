import { describe, it, expect, beforeEach, vi } from 'vitest';
import { GitHubClient, OctokitLike } from '../src/github/client';
import { createMockOctokit, createMockGitHubIssue, createMockGitHubPR, createMockReview } from './__mocks__/octokit';
import { Stage, IssueStatus } from '../src/types';

describe('GitHubClient', () => {
  let client: GitHubClient;
  let mockOctokit: ReturnType<typeof createMockOctokit>;

  beforeEach(() => {
    vi.clearAllMocks();
    mockOctokit = createMockOctokit();
    client = new GitHubClient('test-token', 'testowner', 'testrepo', mockOctokit as unknown as OctokitLike);
  });

  describe('getRepoUrl', () => {
    it('should return correct repo URL', () => {
      expect(client.getRepoUrl()).toBe('https://github.com/testowner/testrepo');
    });
  });

  describe('getIssues', () => {
    it('should fetch issues with labels', async () => {
      const mockIssues = [
        createMockGitHubIssue({ number: 1, title: 'First Issue' }),
        createMockGitHubIssue({ number: 2, title: 'Second Issue' })
      ];

      mockOctokit.issues.listForRepo.mockResolvedValue({ data: mockIssues });

      const issues = await client.getIssues(['crawlph:stage/draft']);

      expect(mockOctokit.issues.listForRepo).toHaveBeenCalledWith({
        owner: 'testowner',
        repo: 'testrepo',
        labels: 'crawlph:stage/draft',
        state: 'open'
      });
      expect(issues).toHaveLength(2);
      expect(issues[0].number).toBe(1);
      expect(issues[0].stage).toBe(Stage.Draft);
      expect(issues[0].status).toBe(IssueStatus.Active);
    });

    it('should fetch all issues without label filter', async () => {
      mockOctokit.issues.listForRepo.mockResolvedValue({ data: [] });

      await client.getIssues();

      expect(mockOctokit.issues.listForRepo).toHaveBeenCalledWith({
        owner: 'testowner',
        repo: 'testrepo',
        labels: undefined,
        state: 'open'
      });
    });

    it('should throw on API error', async () => {
      mockOctokit.issues.listForRepo.mockRejectedValue(new Error('API Error'));

      await expect(client.getIssues()).rejects.toThrow('API Error');
    });
  });

  describe('getIssue', () => {
    it('should fetch single issue', async () => {
      const mockIssue = createMockGitHubIssue({ number: 1, title: 'Test Issue' });
      mockOctokit.issues.get.mockResolvedValue({ data: mockIssue });

      const issue = await client.getIssue(1);

      expect(mockOctokit.issues.get).toHaveBeenCalledWith({
        owner: 'testowner',
        repo: 'testrepo',
        issue_number: 1
      });
      expect(issue?.number).toBe(1);
      expect(issue?.title).toBe('Test Issue');
      expect(issue?.url).toBe('https://github.com/testowner/testrepo/issues/1');
    });

    it('should return null on API error', async () => {
      mockOctokit.issues.get.mockRejectedValue(new Error('Not Found'));

      const issue = await client.getIssue(999);

      expect(issue).toBeNull();
    });
  });

  describe('addLabel', () => {
    it('should add label to issue', async () => {
      mockOctokit.issues.addLabels.mockResolvedValue({});

      await client.addLabel(1, 'test-label');

      expect(mockOctokit.issues.addLabels).toHaveBeenCalledWith({
        owner: 'testowner',
        repo: 'testrepo',
        issue_number: 1,
        labels: ['test-label']
      });
    });

    it('should throw on API error', async () => {
      mockOctokit.issues.addLabels.mockRejectedValue(new Error('Forbidden'));

      await expect(client.addLabel(1, 'test-label')).rejects.toThrow('Forbidden');
    });
  });

  describe('removeLabel', () => {
    it('should remove label from issue', async () => {
      mockOctokit.issues.removeLabel.mockResolvedValue({});

      await client.removeLabel(1, 'test-label');

      expect(mockOctokit.issues.removeLabel).toHaveBeenCalledWith({
        owner: 'testowner',
        repo: 'testrepo',
        issue_number: 1,
        name: 'test-label'
      });
    });

    it('should throw on API error', async () => {
      mockOctokit.issues.removeLabel.mockRejectedValue(new Error('Not Found'));

      await expect(client.removeLabel(1, 'nonexistent')).rejects.toThrow('Not Found');
    });
  });

  describe('hasLabel', () => {
    it('should return true if issue has label', async () => {
      const mockIssue = createMockGitHubIssue({
        labels: [{ name: 'crawlph:stage/draft' }, { name: 'custom-label' }]
      });
      mockOctokit.issues.get.mockResolvedValue({ data: mockIssue });

      const result = await client.hasLabel(1, 'custom-label');

      expect(result).toBe(true);
    });

    it('should return false if issue does not have label', async () => {
      const mockIssue = createMockGitHubIssue({
        labels: [{ name: 'crawlph:stage/draft' }]
      });
      mockOctokit.issues.get.mockResolvedValue({ data: mockIssue });

      const result = await client.hasLabel(1, 'nonexistent');

      expect(result).toBe(false);
    });
  });

  describe('transitionStage', () => {
    it('should transition issue to new stage', async () => {
      const mockIssue = createMockGitHubIssue({
        labels: [{ name: 'crawlph:stage/draft' }]
      });
      mockOctokit.issues.get.mockResolvedValue({ data: mockIssue });
      mockOctokit.issues.removeLabel.mockResolvedValue({});
      mockOctokit.issues.addLabels.mockResolvedValue({});

      await client.transitionStage(1, Stage.Designing);

      expect(mockOctokit.issues.removeLabel).toHaveBeenCalledWith({
        owner: 'testowner',
        repo: 'testrepo',
        issue_number: 1,
        name: 'crawlph:stage/draft'
      });
      expect(mockOctokit.issues.addLabels).toHaveBeenCalledWith({
        owner: 'testowner',
        repo: 'testrepo',
        issue_number: 1,
        labels: ['crawlph:stage/designing']
      });
    });
  });

  describe('setStatus', () => {
    it('should set issue status', async () => {
      const mockIssue = createMockGitHubIssue({
        labels: [{ name: 'crawlph:status/active' }]
      });
      mockOctokit.issues.get.mockResolvedValue({ data: mockIssue });
      mockOctokit.issues.removeLabel.mockResolvedValue({});
      mockOctokit.issues.addLabels.mockResolvedValue({});

      await client.setStatus(1, IssueStatus.Paused);

      expect(mockOctokit.issues.addLabels).toHaveBeenCalledWith({
        owner: 'testowner',
        repo: 'testrepo',
        issue_number: 1,
        labels: ['crawlph:status/paused']
      });
    });
  });

  describe('getPullRequest', () => {
    it('should fetch pull request with reviews', async () => {
      const mockPR = createMockGitHubPR({ number: 1 });
      const mockReviews = [createMockReview({ state: 'APPROVED' })];

      mockOctokit.pulls.get.mockResolvedValue({ data: mockPR });
      mockOctokit.pulls.listReviews.mockResolvedValue({ data: mockReviews });

      const pr = await client.getPullRequest(1);

      expect(mockOctokit.pulls.get).toHaveBeenCalledWith({
        owner: 'testowner',
        repo: 'testrepo',
        pull_number: 1
      });
      expect(pr?.number).toBe(1);
      expect(pr?.approved).toBe(true);
      expect(pr?.issueNumber).toBe(1);
    });

    it('should return null on API error', async () => {
      mockOctokit.pulls.get.mockRejectedValue(new Error('Not Found'));

      const pr = await client.getPullRequest(999);

      expect(pr).toBeNull();
    });
  });

  describe('createPullRequest', () => {
    it('should create pull request with issue reference', async () => {
      const mockPR = createMockGitHubPR({ number: 2, body: 'Closes #1\n\nTest body' });
      mockOctokit.pulls.create.mockResolvedValue({ data: mockPR });

      const pr = await client.createPullRequest('Test PR', 'feature', 'main', 'Test body', 1);

      expect(mockOctokit.pulls.create).toHaveBeenCalledWith({
        owner: 'testowner',
        repo: 'testrepo',
        title: 'Test PR',
        head: 'feature',
        base: 'main',
        body: 'Closes #1\n\nTest body'
      });
      expect(pr?.number).toBe(2);
      expect(pr?.issueNumber).toBe(1);
    });

    it('should create pull request without issue reference', async () => {
      const mockPR = createMockGitHubPR({ number: 1, body: 'Test body' });
      mockOctokit.pulls.create.mockResolvedValue({ data: mockPR });

      const pr = await client.createPullRequest('Test PR', 'feature', 'main', 'Test body');

      expect(mockOctokit.pulls.create).toHaveBeenCalledWith({
        owner: 'testowner',
        repo: 'testrepo',
        title: 'Test PR',
        head: 'feature',
        base: 'main',
        body: 'Test body'
      });
    });
  });

  describe('mergePR', () => {
    it('should merge pull request', async () => {
      mockOctokit.pulls.merge.mockResolvedValue({});

      const result = await client.mergePR(1, 'Merge message');

      expect(mockOctokit.pulls.merge).toHaveBeenCalledWith({
        owner: 'testowner',
        repo: 'testrepo',
        pull_number: 1,
        commit_message: 'Merge message',
        merge_method: 'squash'
      });
      expect(result).toBe(true);
    });

    it('should use default commit message', async () => {
      mockOctokit.pulls.merge.mockResolvedValue({});

      await client.mergePR(1);

      expect(mockOctokit.pulls.merge).toHaveBeenCalledWith(
        expect.objectContaining({
          commit_message: 'Merge PR #1'
        })
      );
    });
  });

  describe('approvePR', () => {
    it('should approve pull request', async () => {
      mockOctokit.pulls.createReview.mockResolvedValue({});

      await client.approvePR(1, 'LGTM');

      expect(mockOctokit.pulls.createReview).toHaveBeenCalledWith({
        owner: 'testowner',
        repo: 'testrepo',
        pull_number: 1,
        event: 'APPROVE',
        body: 'LGTM'
      });
    });

    it('should use default approval message', async () => {
      mockOctokit.pulls.createReview.mockResolvedValue({});

      await client.approvePR(1);

      expect(mockOctokit.pulls.createReview).toHaveBeenCalledWith(
        expect.objectContaining({
          body: 'Approved'
        })
      );
    });
  });

  describe('label parsing', () => {
    it('should parse stage from labels', async () => {
      const mockIssue = createMockGitHubIssue({
        labels: [{ name: 'crawlph:stage/implementing' }]
      });
      mockOctokit.issues.get.mockResolvedValue({ data: mockIssue });

      const issue = await client.getIssue(1);

      expect(issue?.stage).toBe(Stage.Implementing);
    });

    it('should default to Draft stage when no stage label', async () => {
      const mockIssue = createMockGitHubIssue({
        labels: []
      });
      mockOctokit.issues.get.mockResolvedValue({ data: mockIssue });

      const issue = await client.getIssue(1);

      expect(issue?.stage).toBe(Stage.Draft);
    });

    it('should parse PR number from issue body', async () => {
      const mockIssue = createMockGitHubIssue({
        body: 'PR: #42\n\nSome description'
      });
      mockOctokit.issues.get.mockResolvedValue({ data: mockIssue });

      const issue = await client.getIssue(1);

      expect(issue?.prNumber).toBe(42);
    });
  });
});
