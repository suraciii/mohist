import '@testing-library/jest-dom'
import { describe, expect, it } from 'vitest'
import { render, screen } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import { ProjectProvider } from '../../../entities/project'
import { deriveRunnerSummary, type RunnerStatusEntry } from '../../../entities/runner'
import { RunnerList } from './RunnerList'
import { RunnerSummary } from './RunnerSummary'
import type { RunnerStatusSummary } from '../../../entities/runner'

function makeRow(overrides: Partial<RunnerStatusEntry> = {}): RunnerStatusEntry {
  return {
    identity: {
      id: 'runner-1',
      hostname: 'host-1',
      kind: 'external',
      component: 'mohist-runner',
      sourceRevision: 'abc',
      releaseId: 'rel-1',
      generation: 7,
    },
    presence: { state: 'online', lastObservedAt: '2026-01-01T12:00:00Z' },
    control: { state: 'connected', generation: 'connection-1' },
    admission: { state: 'ready', reasonCodes: [] },
    capabilities: ['spec/*'],
    runtimes: [],
    capacity: { used: 1, total: 2 },
    activeWorks: [],
    drain: null,
    nextActions: [],
    ...overrides,
  }
}

function makeSummary(
  rows: RunnerStatusEntry[],
  inventory = { state: 'ready' as const, nextActions: [] },
): RunnerStatusSummary {
  return { ...deriveRunnerSummary(rows), inventory }
}

describe('RunnerSummary', () => {
  it('uses admission language and never renders an idle or busy health badge', () => {
    const row = makeRow({
      admission: { state: 'blocked', reasonCodes: ['capacity-full'] },
      capacity: { used: 2, total: 2 },
    })
    render(
      <MemoryRouter>
        <RunnerSummary summary={makeSummary([row])} />
      </MemoryRouter>,
    )
    expect(screen.getByText('Runner admission blocked')).toBeInTheDocument()
    expect(screen.getByText(/0 active works/)).toBeInTheDocument()
    expect(screen.queryByText(/Runner (idle|busy)/i)).not.toBeInTheDocument()
  })

  it('renders only the Server install action for first install', () => {
    render(
      <MemoryRouter>
        <RunnerSummary
          summary={{
            ...deriveRunnerSummary([]),
            inventory: {
              state: 'first-install',
              nextActions: [
                {
                  code: 'install-runner',
                  message: 'Install and start the first Runner.',
                  command: 'mo install runner --repo-root <path>',
                },
              ],
            },
          }}
        />
      </MemoryRouter>,
    )
    expect(screen.getByText('Install and start the first Runner.')).toBeInTheDocument()
    expect(screen.getByText('mo install runner --repo-root <path>')).toBeInTheDocument()
    expect(screen.queryByText(/start the installed/i)).not.toBeInTheDocument()
  })
})

describe('RunnerList', () => {
  it('renders independent presence, control, admission, capacity, drain, owners, and actions', () => {
    const row = makeRow({
      admission: { state: 'blocked', reasonCodes: ['draining', 'capacity-full'] },
      drain: { active: true, kind: 'update', updateInterruptId: 'interrupt-123' },
      activeWorks: [
        {
          workId: 'wf-work',
          ownerKind: 'workflow',
          ownerId: 'workflow-1',
          workType: 'workflow',
          stage: 'Build',
          title: 'Workflow work',
          issue: { projectId: 'project-1', issueNumber: 12 },
        },
        {
          workId: 'job-work',
          ownerKind: 'agent-job',
          ownerId: 'job-1',
          workType: 'agent-job',
          stage: null,
          title: 'Agent work',
          issue: null,
        },
      ],
      nextActions: [
        { code: 'wait-for-capacity', message: 'Wait for the active owner to release a Runner slot.', command: null },
      ],
    })
    render(
      <MemoryRouter>
        <ProjectProvider
          initialProjectId="project-1"
          initialProjects={[{ id: 'project-1', name: 'Payments', createdAt: '', updatedAt: '', repositories: [] }]}
        >
          <RunnerList rows={[row]} />
        </ProjectProvider>
      </MemoryRouter>,
    )
    expect(screen.getByText('online')).toBeInTheDocument()
    expect(screen.getByText('control connected')).toBeInTheDocument()
    expect(screen.getByText('admission blocked')).toBeInTheDocument()
    expect(screen.getByText('1/2 slots')).toBeInTheDocument()
    expect(screen.getByTestId('runner-drain')).toHaveTextContent('interrupt-123')
    expect(screen.getByText('Workflow')).toBeInTheDocument()
    expect(screen.getByText('AgentJob')).toBeInTheDocument()
    expect(screen.getByTestId('active-work-issue-link')).toHaveAttribute('href', '/Payments/issues/12')
    expect(screen.getByText('Wait for the active owner to release a Runner slot.')).toBeInTheDocument()
    expect(screen.queryByText(/\bidle\b|\bbusy\b/i)).not.toBeInTheDocument()
  })

  it('keeps an offline known definition visible with configured capacity and unresolved issue references as text', () => {
    const row = makeRow({
      identity: {
        id: 'runner-offline',
        hostname: null,
        kind: null,
        component: null,
        sourceRevision: null,
        releaseId: null,
        generation: null,
      },
      presence: { state: 'offline', lastObservedAt: null },
      control: { state: 'disconnected', generation: null },
      admission: { state: 'blocked', reasonCodes: ['presence-offline', 'control-disconnected'] },
      capacity: { used: null, total: 4 },
      activeWorks: [
        {
          workId: 'w',
          ownerKind: 'workflow',
          ownerId: 'wf',
          workType: 'workflow',
          stage: null,
          title: null,
          issue: { projectId: 'unknown-project', issueNumber: 8 },
        },
      ],
      nextActions: [{ code: 'start-runner', message: 'Start the Runner process.', command: 'mo service start runner' }],
    })
    render(
      <MemoryRouter>
        <RunnerList rows={[row]} />
      </MemoryRouter>,
    )
    expect(screen.getByText('runner-offline')).toBeInTheDocument()
    expect(screen.getByText('unknown/4 slots')).toBeInTheDocument()
    expect(screen.getByTestId('active-work-issue-reference')).toHaveTextContent('unknown-project · #8')
    expect(screen.getByText('mo service start runner')).toBeInTheDocument()
  })

  it('renders first-install inventory action without adding a recovery command', () => {
    render(
      <MemoryRouter>
        <RunnerList
          rows={[]}
          inventory={{
            state: 'first-install',
            nextActions: [
              {
                code: 'install-runner',
                message: 'Install and start the first Runner.',
                command: 'mo install runner --repo-root <path>',
              },
            ],
          }}
        />
      </MemoryRouter>,
    )
    expect(screen.getByText('No Runner definitions')).toBeInTheDocument()
    expect(screen.getByText('mo install runner --repo-root <path>')).toBeInTheDocument()
    expect(screen.queryByText('mo service start runner')).not.toBeInTheDocument()
  })
})
