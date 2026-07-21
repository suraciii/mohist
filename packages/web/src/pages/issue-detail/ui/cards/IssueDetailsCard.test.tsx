import { describe, expect, it } from 'vitest'
import { render, screen, within } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import { ProjectProvider } from '../../../../entities/project'
import type { Project } from '../../../../entities/project'
import { IssueDetailsCard, type IssueDetailsCardIssue } from './IssueDetailsCard'
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

function renderCard(issue: IssueDetailsCardIssue) {
  return render(
    <MemoryRouter>
      <ProjectProvider initialProjects={projects} initialProjectId="proj-1">
        <ProjectProbe>
          <IssueDetailsCard issue={issue} unframed />
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
})
