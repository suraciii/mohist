/**
 * Shared fixtures for the LiveTaskProvider colocated test files.
 *
 * `vi.mock` / `vi.hoisted` are hoisted per-file, so each `*.test.ts`
 * declares its own `mocks` object plus mock blocks and the file-local
 * `mountWith()` render helper (which closes over the file-local `mocks`). Those
 * cannot be imported. This module only exports the non-mock fixtures shared
 * across the LiveTaskProvider test files:
 *   - `TEST_PROJECT` fixture
 *   - `makeBaseIssue()` issue factory for the D2 outcome/lifecycle tests
 */
import { IssueStatus, IssueHealth } from '../../entities/issue/model/issue'
import type { Issue } from '../../entities/issue'

export const TEST_PROJECT = {
  id: 'test-project',
  name: 'Test Project',
  createdAt: '2024-01-01T00:00:00.000Z',
  updatedAt: '2024-01-01T00:00:00.000Z',
  repositories: [{ name: 'main', gitUrl: 'https://example.com/test.git', baseBranch: 'main', isDefault: true }],
}

export function makeBaseIssue(_id: string, number: number): Issue {
  return {
    number,
    title: `Issue ${number}`,
    status: IssueStatus.Backlog,
    health: IssueHealth.Active,
    projectId: TEST_PROJECT.id,
    labels: {},
    createdAt: '2024-01-01T00:00:00.000Z',
    updatedAt: '2024-01-01T00:00:00.000Z',
    isDraft: false,
    canStart: true,
    blocker: null,
  }
}
