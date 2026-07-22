import { describe, expect, it } from 'vitest'
import { render, screen, within } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import { ProjectProvider } from '../../../../entities/project'
import type { Project } from '../../../../entities/project'
import { IssueDetailsCard, type IssueDetailsCardIssue } from './IssueDetailsCard'
import type { IssueBodyPartition } from '../../../../entities/issue'
import { useProject } from '../../../../entities/project'
import type { ReactNode } from 'react'

const projects: Project[] = [
  {
    id: 'proj-1',
    name: 'Project 1',
    createdAt: '2026-01-01T00:00:00Z',
    updatedAt: '2026-01-01T00:00:00Z',
    repositories: [],
  },
]

function ProjectProbe({ children }: { children: ReactNode }) {
  const ctx = useProject()
  return <>{ctx ? children : null}</>
}

function renderCard(
  issue: IssueDetailsCardIssue,
  bodyMetadata: Pick<IssueBodyPartition, 'recommendedWorkflow' | 'recommendedWorkflowReason' | 'risk'> = {},
) {
  return render(
    <MemoryRouter>
      <ProjectProvider initialProjects={projects} initialProjectId="proj-1">
        <ProjectProbe>
          <IssueDetailsCard issue={issue} bodyMetadata={bodyMetadata} unframed />
        </ProjectProbe>
      </ProjectProvider>
    </MemoryRouter>,
  )
}

const baseIssue: IssueDetailsCardIssue = {
  status: 'in_progress' as never,
  projectName: 'mohist-local',
  parentIssueRef: { number: 13, title: 'Parent issue' },
  childIssuesSummary: {
    hasChildren: true,
    count: 2,
    backlogCount: 0,
    inProgressCount: 1,
    doneCount: 1,
    cancelledCount: 0,
    blockedCount: 0,
  },
  repository: {
    name: 'master',
    baseBranch: 'master',
    gitUrl: 'https://github.com/suraciii/mohist.git',
  },
  repositoryName: 'master',
}

describe('IssueDetailsCard status metadata removal', () => {
  it('does not render Issue Stage or Workflow Stage rows', () => {
    renderCard(baseIssue)
    const details = screen.getByTestId('issue-detail-details-metadata')
    expect(within(details).queryByText('Issue Stage')).toBeNull()
    expect(within(details).queryByText('Workflow Stage')).toBeNull()
  })

  it('still renders parent/child relationships, project, repository name, base branch, and Git URL', () => {
    renderCard(baseIssue)
    const details = screen.getByTestId('issue-detail-details-metadata')

    expect(within(details).getByTestId('parent-issue-metadata-row')).toBeTruthy()
    expect(within(details).getByTestId('child-issues-metadata-row')).toBeTruthy()
    expect(within(details).getByTestId('child-issues-progress-row')).toBeTruthy()
    expect(within(details).getByText('mohist-local')).toBeTruthy()
    expect(within(details).getByTestId('repository-metadata-row')).toBeTruthy()
    expect(within(details).getByTestId('repository-name')).toHaveTextContent('master')
    expect(within(details).getByTestId('repository-base-branch')).toHaveTextContent('master')
    expect(within(details).getByTestId('repository-git-url')).toHaveTextContent('https://github.com/suraciii/mohist.git')
  })

  it('labels the parent reference row and the child-issues row distinctly', () => {
    renderCard(baseIssue)
    const details = screen.getByTestId('issue-detail-details-metadata')

    const parentRow = within(details).getByTestId('parent-issue-metadata-row')
    const childRow = within(details).getByTestId('child-issues-metadata-row')

    expect(within(parentRow).getByText('Parent Issue')).toBeTruthy()
    expect(within(childRow).getByText('Parent of')).toBeTruthy()
    expect(within(childRow).queryByText('Parent Issue')).toBeNull()
    expect(within(details).getAllByText('Parent Issue')).toHaveLength(1)
  })

  it('omits Issue Stage and Workflow Stage even when workflowStage data is present in the source issue', () => {
    const issue = {
      ...baseIssue,
      workflowStage: 'build' as never,
    }
    renderCard(issue)
    const details = screen.getByTestId('issue-detail-details-metadata')
    expect(within(details).queryByText('Issue Stage')).toBeNull()
    expect(within(details).queryByText('Workflow Stage')).toBeNull()
  })

  it('labels workflow body values as recommendations', () => {
    renderCard(baseIssue, {
      recommendedWorkflow: 'mohist/local',
      recommendedWorkflowReason: 'Best fit for this change',
    })

    const details = screen.getByTestId('issue-detail-details-metadata')
    expect(within(details).getByText('Recommended workflow')).toBeTruthy()
    expect(within(details).getByText('mohist/local')).toBeTruthy()
    expect(within(details).getByText('Recommendation reason')).toBeTruthy()
    expect(within(details).getByText('Best fit for this change')).toBeTruthy()
    expect(within(details).queryByText('Workflow Profile')).toBeNull()
  })

  it('renders one authoritative Risk value instead of a conflicting body default', () => {
    renderCard({ ...baseIssue, risk: 'high' }, { risk: 'low' })

    const details = screen.getByTestId('issue-detail-details-metadata')
    expect(within(details).getAllByText('Risk')).toHaveLength(1)
    expect(within(details).getByText('high')).toBeTruthy()
    expect(within(details).queryByText('low')).toBeNull()
  })

  it('uses body risk only when Issue risk is absent', () => {
    renderCard({ ...baseIssue, risk: null }, { risk: 'medium' })

    const details = screen.getByTestId('issue-detail-details-metadata')
    expect(within(details).getAllByText('Risk')).toHaveLength(1)
    expect(within(details).getByText('medium')).toBeTruthy()
  })
})
