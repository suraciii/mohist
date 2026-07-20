import { describe, expect, it, beforeEach } from 'vitest'
import { screen, waitFor, fireEvent } from '@testing-library/react'
import { render } from '../../../../tests/test-utils'
import type { ComponentProps } from 'react'
import { LatestArtifactsPanel as DefaultLatestArtifactsPanel, type LatestArtifactsHook } from './LatestArtifactsPanel'
import { WorkflowView as DefaultWorkflowView, type WorkflowTimelineHook } from './WorkflowView'
import type { ArtifactContentHook } from './ArtifactContentViewer'
import {
  IssueStatus,
  IssueHealth,
  WorkflowStage,
  type Issue,
  type WorkflowTimeline,
  type WorkflowArtifact,
} from '../../../entities/issue'
import type { WorkflowArtifactContentResult } from '../../../entities/issue/api/client'
import { setScopedValue } from '../../../../tests/support/scoped-property'

let artifactsData: WorkflowArtifact[] = []
let artifactContent: WorkflowArtifactContentResult | null = null
let artifactFileContent: WorkflowArtifactContentResult | null = null
let timelineData: WorkflowTimeline | null = null

const artifactsHook: LatestArtifactsHook = () => ({
  data: artifactsData,
  isLoading: false,
  error: null,
})

const contentHook: ArtifactContentHook = (_issueNumber, _artifactId, options, enabled = true) => ({
  data: enabled ? (options?.file ? artifactFileContent : artifactContent) ?? undefined : undefined,
  isLoading: false,
  error: null,
})

const timelineHook: WorkflowTimelineHook = () => ({ data: timelineData })

function LatestArtifactsPanel(
  props: Omit<ComponentProps<typeof DefaultLatestArtifactsPanel>, 'artifactsHook' | 'contentHook'>,
) {
  return (
    <DefaultLatestArtifactsPanel
      {...props}
      artifactsHook={artifactsHook}
      contentHook={contentHook}
    />
  )
}

function WorkflowView(props: Omit<ComponentProps<typeof DefaultWorkflowView>, 'timelineHook'>) {
  return (
    <DefaultWorkflowView
      {...props}
      timelineHook={timelineHook}
      dependencies={{ ...props.dependencies, artifactContentHook: contentHook }}
    />
  )
}

beforeEach(() => {
  artifactsData = []
  artifactContent = null
  artifactFileContent = null
  timelineData = null
})

function makeIssue(overrides: Partial<Issue> = {}): Issue {
  return {
    number: 1,
    title: 'Implement workflow artifacts',
    body: '',
    status: IssueStatus.InProgress,
    workflowStage: WorkflowStage.Check,
    workflowRunId: 'workflow-run-1',
    health: IssueHealth.Active,
    projectId: 'test-project',
    labels: {},
    createdAt: '2026-01-01T00:00:00.000Z',
    updatedAt: '2026-01-01T00:00:00.000Z',
    comments: [],
    isDraft: false,
    canStart: true,
    blocker: null,
    ...overrides,
  }
}

function makeFileArtifact(overrides: Partial<WorkflowArtifact> = {}): WorkflowArtifact {
  const path = overrides.path ?? 'review.md'
  return {
    artifactId: 'art-1',
    workflowRunId: 'workflow-run-1',
    taskRunId: 'ai-review.1',
    path,
    kind: 'file',
    contentType: 'text/markdown',
    size: 123,
    recordedAt: '2026-01-01T00:00:00.000Z',
    displayName: path,
    ...overrides,
  }
}

function makeDirectoryArtifact(overrides: Partial<WorkflowArtifact> = {}): WorkflowArtifact {
  const path = overrides.path ?? 'specs/'
  return {
    artifactId: 'art-dir-1',
    workflowRunId: 'workflow-run-1',
    taskRunId: 'plan.1',
    path,
    kind: 'directory',
    size: 456,
    recordedAt: '2026-01-01T00:00:00.000Z',
    displayName: path,
    ...overrides,
  }
}

function makeTimelineWithTaskArtifacts(): WorkflowTimeline {
  return {
    workflowRunId: 'workflow-run-1',
    status: 'Running',
    currentStage: WorkflowStage.Check,
    pendingWork: null,
    stages: [
      {
        stage: WorkflowStage.Check,
        status: 'running',
        order: 3,
        startedAt: '2026-01-01T00:00:00.000Z',
        completedAt: null,
        durationMs: null,
        tasks: [
          {
            id: 'ai-review.1',
            title: 'AI review',
            uses: 'mohist/opencode',
            status: 'completed',
            startedAt: '2026-01-01T00:00:00.000Z',
            completedAt: '2026-01-01T00:01:00.000Z',
            durationMs: 60000,
            attempts: 1,
            message: null,
            classification: 'UserFacing',
            artifactSummaries: [makeFileArtifact({ artifactId: 'art-review-1', taskRunId: 'ai-review.1' })],
          },
          {
            id: 'fix-review-findings.1',
            title: 'Fix review findings',
            uses: 'mohist/coder-agent',
            status: 'completed',
            startedAt: '2026-01-01T00:01:00.000Z',
            completedAt: '2026-01-01T00:02:00.000Z',
            durationMs: 60000,
            attempts: 1,
            message: null,
            classification: 'UserFacing',
            artifactSummaries: [],
          },
          {
            id: 'ai-review.2',
            title: 'AI review',
            uses: 'mohist/opencode',
            status: 'completed',
            startedAt: '2026-01-01T00:02:00.000Z',
            completedAt: '2026-01-01T00:03:00.000Z',
            durationMs: 60000,
            attempts: 1,
            message: null,
            classification: 'UserFacing',
            artifactSummaries: [
              makeFileArtifact({
                artifactId: 'art-review-2',
                taskRunId: 'ai-review.2',
                path: 'review.md',
                displayName: 'review.md',
              }),
            ],
          },
        ],
        checks: [],
        approval: null,
      },
    ],
    availableActions: [],
  }
}

async function expandTaskByTitle(taskTitle: string) {
  const titles = screen.getAllByText(taskTitle)
  for (const titleEl of titles) {
    const taskRow = titleEl.closest('[class*="rounded-md"]')
    const expandButton = taskRow?.querySelector('button')
    if (expandButton) {
      fireEvent.click(expandButton)
    }
  }
  await waitFor(() => {
    const mutedPanels = document.querySelectorAll('[class*="bg-muted"]')
    expect(mutedPanels.length).toBeGreaterThanOrEqual(titles.length)
  })
}

describe('LatestArtifactsPanel', () => {
  it('renders latest artifacts grouped by path', async () => {
    artifactsData = [
        makeFileArtifact({ artifactId: 'art-proposal', path: 'proposal.md', taskRunId: 'plan.1' }),
        makeFileArtifact({ artifactId: 'art-review-2', path: 'review.md', taskRunId: 'ai-review.2' }),
    ]

    render(<LatestArtifactsPanel issueNumber={1} workflowRunId="workflow-run-1" />)

    await waitFor(() => {
      expect(screen.getByText('proposal.md')).toBeInTheDocument()
      expect(screen.getByText('review.md')).toBeInTheDocument()
    })
  })

  it('renders directory artifact as one collection', async () => {
    artifactsData = [makeDirectoryArtifact({ artifactId: 'art-specs', path: 'specs/' })]

    render(<LatestArtifactsPanel issueNumber={1} workflowRunId="workflow-run-1" />)

    await waitFor(() => {
      expect(screen.getByText('specs/')).toBeInTheDocument()
      expect(screen.getByText('0 files')).toBeInTheDocument()
    })
  })

  it('opens recorded artifact content when latest artifact is clicked', async () => {
    artifactsData = [makeFileArtifact({ artifactId: 'art-review-2', path: 'review.md', taskRunId: 'ai-review.2' })]
    artifactContent = { kind: 'text', content: '# Title\n\nPASS', contentType: 'text/markdown' }

    render(<LatestArtifactsPanel issueNumber={1} workflowRunId="workflow-run-1" />)

    await waitFor(() => {
      expect(screen.getByText('review.md')).toBeInTheDocument()
    })

    fireEvent.click(screen.getByText('review.md'))

    await waitFor(() => {
      expect(screen.getByText('123 B')).toBeInTheDocument()
      const reader = screen.getByTestId('markdown-reader')
      expect(reader).toHaveAttribute('data-base-heading-level', '2')
      expect(screen.getByRole('heading', { level: 2, name: 'Title' })).toBeInTheDocument()
      expect(screen.queryByRole('heading', { level: 1, name: 'Title' })).not.toBeInTheDocument()
      expect(screen.getByText('PASS')).toBeInTheDocument()
      expect(screen.queryByText('# Title')).not.toBeInTheDocument()
    })
  })

  it('renders non-Markdown text artifact inside a <pre> block', async () => {
    artifactsData = [makeFileArtifact({ artifactId: 'art-log', path: 'output.log', taskRunId: 'plan.1', contentType: 'text/plain' })]
    artifactContent = { kind: 'text', content: 'plain line 1\nplain line 2', contentType: 'text/plain' }

    render(<LatestArtifactsPanel issueNumber={1} workflowRunId="workflow-run-1" />)

    await waitFor(() => {
      expect(screen.getByText('output.log')).toBeInTheDocument()
    })

    fireEvent.click(screen.getByText('output.log'))

    await waitFor(() => {
      expect(screen.getByText((_, el) => el?.tagName === 'PRE' && /plain line 1\nplain line 2/.test(el.textContent ?? ''))).toBeInTheDocument()
    })

    const pre = screen.getByText((_, el) => el?.tagName === 'PRE' && /plain line 1\nplain line 2/.test(el.textContent ?? '')).closest('pre')
    expect(pre).not.toBeNull()
    expect(pre?.className).toContain('whitespace-pre-wrap')
    expect(screen.queryByTestId('markdown-reader')).not.toBeInTheDocument()
  })

  it('renders directory entries and opens contained Markdown file content through MarkdownReader', async () => {
    artifactsData = [makeDirectoryArtifact({ artifactId: 'art-specs', path: 'specs/' })]
    artifactContent = {
      kind: 'directory',
      entries: [{ relativePath: 'workflow.md', size: 100, contentType: 'text/markdown' }],
      totalSize: 100,
    }
    artifactFileContent = { kind: 'text', content: '# Spec\n\nSpec body', contentType: 'text/markdown' }

    render(<LatestArtifactsPanel issueNumber={1} workflowRunId="workflow-run-1" />)

    await waitFor(() => {
      expect(screen.getByText('specs/')).toBeInTheDocument()
    })

    fireEvent.click(screen.getByText('specs/'))

    await waitFor(() => {
      expect(screen.getByText('workflow.md')).toBeInTheDocument()
    })

    fireEvent.click(screen.getByText('workflow.md'))

    await waitFor(() => {
      expect(screen.getByTestId('markdown-reader')).toBeInTheDocument()
      expect(screen.getByRole('heading', { level: 2, name: 'Spec' })).toBeInTheDocument()
      expect(screen.getByText('Spec body')).toBeInTheDocument()
      expect(screen.queryByText('# Spec')).not.toBeInTheDocument()
    })
  })
})

describe('Task artifact history rendering', () => {
  it('renders artifacts on each task run row', async () => {
    timelineData = makeTimelineWithTaskArtifacts()

    render(<WorkflowView issue={makeIssue()} />)

    await waitFor(() => {
      expect(screen.getAllByText('AI review').length).toBeGreaterThanOrEqual(2)
    })

    await expandTaskByTitle('AI review')

    await waitFor(() => {
      expect(screen.getAllByText('review.md').length).toBeGreaterThanOrEqual(1)
    })
  })

  it('preserves historical review artifact on ai-review.1 row after ai-review.2 runs', async () => {
    timelineData = makeTimelineWithTaskArtifacts()

    render(<WorkflowView issue={makeIssue()} />)

    await waitFor(() => {
      expect(screen.getAllByText('AI review').length).toBeGreaterThanOrEqual(2)
    })

    await expandTaskByTitle('AI review')

    await waitFor(() => {
      const chips = screen.getAllByText('review.md')
      expect(chips.length).toBeGreaterThanOrEqual(2)
    })
  })
})

describe('ArtifactContentViewer rendering edge cases', () => {
  it('renders .markdown artifacts as markdown (not as <pre> text)', async () => {
    artifactsData = [makeFileArtifact({ artifactId: 'art-doc', path: 'notes.markdown', size: 200 })]
    artifactContent = { kind: 'text', content: '# Heading\n\n- item 1\n- item 2', contentType: 'text/markdown' }

    render(<LatestArtifactsPanel issueNumber={1} workflowRunId="workflow-run-1" />)

    await waitFor(() => {
      expect(screen.getByText('notes.markdown')).toBeInTheDocument()
    })

    fireEvent.click(screen.getByText('notes.markdown'))

    await waitFor(() => {
      // The markdown content is rendered via MarkdownReader (baseHeadingLevel=2),
      // so we expect an <h2> heading and <li> list items, NOT a <pre> block with raw markdown text.
      expect(screen.getByRole('heading', { name: 'Heading', level: 2 })).toBeInTheDocument()
      expect(screen.getByText('item 1')).toBeInTheDocument()
      expect(screen.queryByText('# Heading')).not.toBeInTheDocument()
    })
  })

  it('falls back to "Recorded artifact content" when size is null', async () => {
    artifactsData = [makeFileArtifact({ artifactId: 'art-unknown-size', path: 'unknown-size.md', size: null })]
    artifactContent = { kind: 'text', content: 'Some content', contentType: 'text/markdown' }

    render(<LatestArtifactsPanel issueNumber={1} workflowRunId="workflow-run-1" />)

    await waitFor(() => {
      expect(screen.getByText('unknown-size.md')).toBeInTheDocument()
    })

    fireEvent.click(screen.getByText('unknown-size.md'))

    await waitFor(() => {
      expect(screen.getByText('Recorded artifact content')).toBeInTheDocument()
    })
  })

  it('shows "Unable to copy" feedback when navigator.clipboard is unavailable', async () => {
    setScopedValue(navigator, 'clipboard', undefined)
    artifactsData = [makeFileArtifact({ artifactId: 'art-copy', path: 'copy.md', size: 50 })]
    artifactContent = { kind: 'text', content: 'Copy me', contentType: 'text/markdown' }

    render(<LatestArtifactsPanel issueNumber={1} workflowRunId="workflow-run-1" />)

    await waitFor(() => {
      expect(screen.getByText('copy.md')).toBeInTheDocument()
    })

    fireEvent.click(screen.getByText('copy.md'))

    await waitFor(() => {
      expect(screen.getByRole('button', { name: 'Copy' })).toBeInTheDocument()
    })

    fireEvent.click(screen.getByRole('button', { name: 'Copy' }))

    await waitFor(() => {
      expect(screen.getByRole('button', { name: 'Unable to copy' })).toBeInTheDocument()
    })
  })
})
