import { describe, it, expect } from 'vitest'
import { render, screen } from '../../../../tests/test-utils'
import type { IssueDiffResponse, IssueCommitsResponse } from '../../../entities/issue'

import { ChangesPanel } from './ChangesPanel'

function makeDiffResponse(overrides: Partial<IssueDiffResponse> = {}): IssueDiffResponse {
  return {
    available: true,
    reason: null,
    base: 'main',
    head: 'mo/issue-1',
    summary: { filesChanged: 0, commits: 0, additions: 0, deletions: 0 },
    files: [],
    ...overrides,
  } as IssueDiffResponse
}

function makeCommitsResponse(overrides: Partial<IssueCommitsResponse> = {}): IssueCommitsResponse {
  return {
    available: true,
    reason: null,
    base: 'main',
    head: 'mo/issue-1',
    summary: { filesChanged: 0, commits: 0, additions: 0, deletions: 0 },
    commits: [],
    ...overrides,
  } as IssueCommitsResponse
}

describe('ChangesPanel', () => {
  describe('Files tab as default', () => {
    it('shows file list when diffTab is files', () => {
      const diffData = makeDiffResponse({
        available: true,
        files: [
          { file: 'src/a.ts', additions: 10, deletions: 2, diff: 'diff --git', isBinary: false },
          { file: 'src/b.ts', additions: 5, deletions: 1, diff: 'diff --git', isBinary: false },
        ],
      })
      const commitsData = makeCommitsResponse()

      render(
        <ChangesPanel
          diffData={diffData}
          commitsData={commitsData}
          diffTab="files"
          setDiffTab={vi.fn()}
          expandedFiles={new Set()}
          setExpandedFiles={vi.fn()}
          expandedCommits={new Set()}
          setExpandedCommits={vi.fn()}
          issueNumber={1}
        />
      )

      expect(screen.getByText('src/a.ts')).toBeInTheDocument()
      expect(screen.getByText('src/b.ts')).toBeInTheDocument()
    })

    it('shows No file changes yet when files array is empty but diff is available', () => {
      const diffData = makeDiffResponse({ available: true, files: [] })
      const commitsData = makeCommitsResponse()

      render(
        <ChangesPanel
          diffData={diffData}
          commitsData={commitsData}
          diffTab="files"
          setDiffTab={vi.fn()}
          expandedFiles={new Set()}
          setExpandedFiles={vi.fn()}
          expandedCommits={new Set()}
          setExpandedCommits={vi.fn()}
          issueNumber={1}
        />
      )

      expect(screen.getByText('No file changes yet.')).toBeInTheDocument()
    })
  })

  describe('Workspace removed copy', () => {
    it('shows workspace removed message instead of No changes yet when workspace_removed', () => {
      const diffData = makeDiffResponse({ available: false, reason: 'workspace_removed', message: 'Workspace has been removed. Diff is only available while the issue workspace is retained.' })
      const commitsData = makeCommitsResponse({ available: false, reason: 'workspace_removed', message: 'Workspace has been removed.' })

      render(
        <ChangesPanel
          diffData={diffData}
          commitsData={commitsData}
          diffTab="files"
          setDiffTab={vi.fn()}
          expandedFiles={new Set()}
          setExpandedFiles={vi.fn()}
          expandedCommits={new Set()}
          setExpandedCommits={vi.fn()}
          issueNumber={1}
        />
      )

      expect(screen.getByText(/workspace removed/i)).toBeInTheDocument()
      expect(screen.queryByText('No changes yet')).not.toBeInTheDocument()
    })

    it('shows branch missing message when branch_missing', () => {
      const diffData = makeDiffResponse({ available: false, reason: 'branch_missing', message: 'Branch mo/issue-1 not found.' })
      const commitsData = makeCommitsResponse({ available: false, reason: 'branch_missing', message: 'Branch mo/issue-1 not found.' })

      render(
        <ChangesPanel
          diffData={diffData}
          commitsData={commitsData}
          diffTab="files"
          setDiffTab={vi.fn()}
          expandedFiles={new Set()}
          setExpandedFiles={vi.fn()}
          expandedCommits={new Set()}
          setExpandedCommits={vi.fn()}
          issueNumber={1}
        />
      )

      expect(screen.getByText(/branch missing/i)).toBeInTheDocument()
    })

    it('shows No changes yet for not_started', () => {
      const diffData = makeDiffResponse({ available: false, reason: 'not_started', message: 'Issue has not started yet.' })
      const commitsData = makeCommitsResponse({ available: false, reason: 'not_started', message: 'Issue has not started yet.' })

      render(
        <ChangesPanel
          diffData={diffData}
          commitsData={commitsData}
          diffTab="files"
          setDiffTab={vi.fn()}
          expandedFiles={new Set()}
          setExpandedFiles={vi.fn()}
          expandedCommits={new Set()}
          setExpandedCommits={vi.fn()}
          issueNumber={1}
        />
      )

      expect(screen.getByText('No changes yet')).toBeInTheDocument()
    })

    it('shows failed to load message for git_error', () => {
      const diffData = makeDiffResponse({ available: false, reason: 'git_error', message: 'Failed to load changes.' })
      const commitsData = makeCommitsResponse({ available: false, reason: 'git_error', message: 'Failed to load commits.' })

      render(
        <ChangesPanel
          diffData={diffData}
          commitsData={commitsData}
          diffTab="files"
          setDiffTab={vi.fn()}
          expandedFiles={new Set()}
          setExpandedFiles={vi.fn()}
          expandedCommits={new Set()}
          setExpandedCommits={vi.fn()}
          issueNumber={1}
        />
      )

      expect(screen.getByText(/Failed to load/i)).toBeInTheDocument()
    })
  })

  describe('Review summary header', () => {
    it('displays base→head, file count, commit count, additions, deletions when available', () => {
      const diffData = makeDiffResponse({
        available: true,
        base: 'main',
        head: 'mo/issue-5',
        summary: { filesChanged: 3, commits: 2, additions: 50, deletions: 20 },
        files: [],
      })
      const commitsData = makeCommitsResponse({
        available: true,
        summary: { filesChanged: 3, commits: 2, additions: 50, deletions: 20 },
      })

      render(
        <ChangesPanel
          diffData={diffData}
          commitsData={commitsData}
          diffTab="files"
          setDiffTab={vi.fn()}
          expandedFiles={new Set()}
          setExpandedFiles={vi.fn()}
          expandedCommits={new Set()}
          setExpandedCommits={vi.fn()}
          issueNumber={5}
        />
      )

      expect(screen.getByText(/main → mo\/issue-5/)).toBeInTheDocument()
      expect(screen.getByText(/3 files changed/)).toBeInTheDocument()
      expect(screen.getByText(/2 commits?/)).toBeInTheDocument()
      expect(screen.getByText(/\+50/)).toBeInTheDocument()
      expect(screen.getByText(/-20/)).toBeInTheDocument()
      expect(screen.getByText(/Workspace retained/)).toBeInTheDocument()
    })
  })

  describe('Commits tab', () => {
    it('renders commit list when diffTab is commits', () => {
      const diffData = makeDiffResponse()
      const commitsData = makeCommitsResponse({
        available: true,
        commits: [
          { hash: 'abc1234', shortHash: 'abc1234', message: 'fix: add feature', author: 'Test', date: '2024-01-01T00:00:00Z', filesChanged: 2, additions: 10, deletions: 2, files: ['a.txt', 'b.txt'] },
          { hash: 'def5678', shortHash: 'def5678', message: 'chore: cleanup', author: 'Test', date: '2024-01-02T00:00:00Z', filesChanged: 1, additions: 1, deletions: 0, files: ['c.txt'] },
        ],
      })

      render(
        <ChangesPanel
          diffData={diffData}
          commitsData={commitsData}
          diffTab="commits"
          setDiffTab={vi.fn()}
          expandedFiles={new Set()}
          setExpandedFiles={vi.fn()}
          expandedCommits={new Set()}
          setExpandedCommits={vi.fn()}
          issueNumber={1}
        />
      )

      expect(screen.getByText('fix: add feature')).toBeInTheDocument()
      expect(screen.getByText('chore: cleanup')).toBeInTheDocument()
    })

    it('shows No commits yet when commits array is empty but commits are available', () => {
      const diffData = makeDiffResponse()
      const commitsData = makeCommitsResponse({ available: true, commits: [] })

      render(
        <ChangesPanel
          diffData={diffData}
          commitsData={commitsData}
          diffTab="commits"
          setDiffTab={vi.fn()}
          expandedFiles={new Set()}
          setExpandedFiles={vi.fn()}
          expandedCommits={new Set()}
          setExpandedCommits={vi.fn()}
          issueNumber={1}
        />
      )

      expect(screen.getByText('No commits yet.')).toBeInTheDocument()
    })
  })

})
