import { vi } from 'vitest';

export function createMockOctokit() {
  return {
    issues: {
      listForRepo: vi.fn(),
      get: vi.fn(),
      addLabels: vi.fn(),
      removeLabel: vi.fn(),
      create: vi.fn(),
      update: vi.fn()
    },
    pulls: {
      get: vi.fn(),
      list: vi.fn(),
      create: vi.fn(),
      merge: vi.fn(),
      listReviews: vi.fn(),
      createReview: vi.fn(),
      requestReviewers: vi.fn()
    },
    repos: {
      get: vi.fn(),
      listForOrg: vi.fn(),
      listForUser: vi.fn()
    },
    projects: {
      listForRepo: vi.fn(),
      createForRepo: vi.fn(),
      get: vi.fn(),
      delete: vi.fn()
    }
  };
}

export function createMockGitHubIssue(overrides: Partial<any> = {}) {
  return {
    number: 1,
    title: 'Test Issue',
    body: 'Test body',
    state: 'open',
    labels: [{ name: 'crawlph:stage/draft' }, { name: 'crawlph:status/active' }],
    html_url: 'https://github.com/testowner/testrepo/issues/1',
    created_at: '2024-01-01T00:00:00Z',
    updated_at: '2024-01-01T00:00:00Z',
    ...overrides
  };
}

export function createMockGitHubPR(overrides: Partial<any> = {}) {
  return {
    number: 1,
    title: 'Test PR',
    body: 'Closes #1',
    state: 'open',
    draft: false,
    merged: false,
    mergeable: true,
    head: { ref: 'feature-branch' },
    base: { ref: 'main' },
    html_url: 'https://github.com/testowner/testrepo/pull/1',
    created_at: '2024-01-01T00:00:00Z',
    updated_at: '2024-01-01T00:00:00Z',
    ...overrides
  };
}

export function createMockReview(overrides: Partial<any> = {}) {
  return {
    id: 1,
    user: { login: 'reviewer' },
    state: 'APPROVED',
    body: 'LGTM',
    submitted_at: '2024-01-01T00:00:00Z',
    ...overrides
  };
}
