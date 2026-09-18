import '@testing-library/jest-dom'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { cleanup, render, screen, waitFor, within } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { QueryClient, QueryClientProvider, useMutation } from '@tanstack/react-query'
import { ProjectProvider } from '../../../entities/project'
import type { Project } from '../../../entities/project'
import type { RunnerStatusEntry } from '../../../entities/runner'
import { ApiError } from '../../../shared/api/client'
import { RunnerDetailPage, type RunnerDetailPageDependencies } from './RunnerDetailPage'

const PROJECT: Project = { id: 'project-1', name: 'Payments', createdAt: '', updatedAt: '', repositories: [] }

function makeRunner(overrides: Partial<RunnerStatusEntry> = {}): RunnerStatusEntry {
  return {
    identity: {
      id: 'runner-7',
      hostname: 'host-7',
      kind: 'external',
      component: 'mohist-runner',
      sourceRevision: 'source-123',
      releaseId: 'release-7',
      generation: 8,
    },
    presence: { state: 'online', lastObservedAt: '2026-01-01T12:00:00Z' },
    control: { state: 'connected', generation: 'server-epoch:12' },
    admission: { state: 'ready', reasonCodes: [] },
    capabilities: ['spec/*'],
    runtimes: [
      {
        name: 'pi',
        readiness: { state: 'ready', generation: 3, reasonCode: null },
        catalog: {
          complete: true,
          capabilityRevision: 'catalog-sha',
          modelCount: 1,
          models: ['openai/gpt-5'],
          variants: { 'openai/gpt-5': ['high'] },
          supportsReasoningEffort: true,
          reasoningEfforts: { 'openai/gpt-5': ['high'] },
        },
      },
    ],
    capacity: { used: 2, total: 3 },
    activeWorks: [],
    drain: null,
    nextActions: [],
    ...overrides,
  }
}

let runner: RunnerStatusEntry | undefined
let error: Error | null = null
let loading = false
const runnerHook: NonNullable<RunnerDetailPageDependencies['runnerHook']> = () =>
  ({ data: runner, error, isLoading: loading }) as never
const slotsMutationHook: NonNullable<RunnerDetailPageDependencies['slotsMutationHook']> = () =>
  useMutation({ mutationFn: async () => ({ runnerId: 'runner-7', slots: 3 }) })
const dependencies: RunnerDetailPageDependencies = { runnerHook, slotsMutationHook }

function renderPage() {
  return render(
    <QueryClientProvider client={new QueryClient()}>
      <ProjectProvider initialProjectId={PROJECT.id} initialProjects={[PROJECT]}>
        <MemoryRouter initialEntries={['/runners/runner-7']}>
          <Routes>
            <Route path="/runners/:runnerId" element={<RunnerDetailPage dependencies={dependencies} />} />
            <Route path="/runners" element={<div>Runner list</div>} />
            <Route path="/Payments/issues/:number" element={<div>Issue</div>} />
          </Routes>
        </MemoryRouter>
      </ProjectProvider>
    </QueryClientProvider>,
  )
}

beforeEach(() => {
  runner = undefined
  error = null
  loading = false
})
afterEach(cleanup)

describe('RunnerDetailPage', () => {
  it('renders build identity, independent status facts, and complete Runtime catalog detail', async () => {
    runner = makeRunner({
      activeWorks: [
        {
          workId: 'workflow-work',
          ownerKind: 'workflow',
          ownerId: 'workflow-1',
          workType: 'workflow',
          stage: 'Build',
          title: 'Workflow',
          issue: { projectId: 'project-1', issueNumber: 12 },
        },
        {
          workId: 'agent-work',
          ownerKind: 'agent-job',
          ownerId: 'job-1',
          workType: 'agent-job',
          stage: null,
          title: 'AgentJob',
          issue: null,
        },
      ],
    })
    renderPage()
    await waitFor(() => expect(screen.getByTestId('runner-detail-id')).toHaveTextContent('runner-7'))
    expect(screen.getByTestId('runner-detail-component')).toHaveTextContent('mohist-runner')
    expect(screen.getByTestId('runner-detail-source-revision')).toHaveTextContent('source-123')
    expect(screen.getByTestId('runner-detail-release-id')).toHaveTextContent('release-7')
    expect(screen.getByTestId('runner-detail-generation')).toHaveTextContent('8')
    expect(screen.getByTestId('runner-runtime-readiness')).toHaveTextContent('ready')
    expect(screen.getByTestId('runner-runtime-models')).toHaveTextContent('openai/gpt-5')
    expect(screen.getByTestId('runner-runtime-variants')).toHaveTextContent('high')
    expect(screen.getByTestId('runner-runtime-reasoning-support')).toHaveTextContent('supported')
    expect(screen.getByTestId('runner-detail-capacity')).toHaveTextContent('2/3 slots')
    const works = screen.getByTestId('runner-detail-active-works-list')
    expect(within(works).getAllByTestId('active-work-detail-row')).toHaveLength(2)
    expect(within(works).getByTestId('active-work-issue-link')).toHaveAttribute('href', '/Payments/issues/12')
    expect(within(works).getAllByText('AgentJob').length).toBeGreaterThan(0)
    expect(screen.queryByText(/idle|busy/i)).not.toBeInTheDocument()
  })

  it('shows catalog availability separately from a Runtime readiness blocker', () => {
    runner = makeRunner({
      runtimes: [
        {
          name: 'pi',
          readiness: { state: 'not-ready', generation: null, reasonCode: 'runtime-witness-missing' },
          catalog: {
            complete: true,
            capabilityRevision: 'catalog-sha',
            modelCount: 1,
            models: ['model-that-is-not-readiness'],
            variants: {},
            supportsReasoningEffort: false,
            reasoningEfforts: {},
          },
        },
      ],
    })
    renderPage()
    expect(screen.getByTestId('runner-runtime-readiness')).toHaveTextContent('not-ready')
    expect(screen.getByTestId('runner-runtime-models')).toHaveTextContent('model-that-is-not-readiness')
    expect(screen.getByText(/Runtime readiness and capability catalog are independent facts/i)).toBeInTheDocument()
  })

  it('shows the active environment and durable application without inventing a remote candidate', () => {
    runner = makeRunner({
      environment: {
        activeVersion: 'env-v2',
        activeLoadedAt: '2026-01-01T11:59:00Z',
        application: {
          updateId: '2fb831bf-0319-4f31-8651-f3b0cc55f116',
          targetVersion: 'env-v3',
          previousVersion: 'env-v2',
          phase: 'waiting',
          failureCode: null,
          baseProcessGeneration: 'process-2',
          baseConnectionGeneration: 'server-epoch:12',
          requestedAt: '2026-01-01T12:00:00Z',
          completedAt: null,
        },
      },
    })

    renderPage()

    const environment = screen.getByTestId('runner-detail-environment-section')
    expect(within(environment).getByTestId('runner-environment-active-version')).toHaveTextContent('env-v2')
    expect(within(environment).getByTestId('runner-environment-application')).toHaveTextContent('waiting')
    expect(within(environment).getByText('env-v3')).toBeInTheDocument()
    expect(within(environment).getByText(/mo runner environment status/)).toBeInTheDocument()
    expect(within(environment).queryByText(/candidate source/i)).not.toBeInTheDocument()
  })

  it('keeps an offline known definition and safely quoted Server re-enrollment action', () => {
    const runnerId = "build runner'$(touch /tmp/owned);"
    const command = "mo install runner --repo-root <path> --runner-id 'build runner'\"'\"'$(touch /tmp/owned);'"
    runner = makeRunner({
      identity: {
        id: runnerId,
        hostname: null,
        kind: null,
        component: null,
        sourceRevision: null,
        releaseId: null,
        generation: null,
      },
      presence: { state: 'offline', lastObservedAt: null },
      control: { state: 'disconnected', generation: null },
      admission: { state: 'blocked', reasonCodes: ['presence-offline', 'credential-revoked'] },
      runtimes: [],
      capacity: { used: null, total: 4 },
      nextActions: [
        {
          code: 'reenroll-runner',
          message: 'Re-enroll the Runner credential.',
          command,
        },
      ],
    })
    renderPage()
    expect(screen.getByTestId('runner-detail-id')).toHaveTextContent(runnerId)
    expect(screen.getByTestId('runner-detail-max-slots')).toContainElement(screen.getByTestId('slots-editor'))
    expect(screen.getByTestId('runner-detail-capacity')).toHaveTextContent('unknown/4 slots')
    expect(screen.getByText(command)).toBeInTheDocument()
    expect(screen.queryByText('mo runner start runner')).not.toBeInTheDocument()
  })

  it('renders canonical not-found detail errors', () => {
    error = new ApiError('missing', 404)
    renderPage()
    expect(screen.getByTestId('runner-not-found')).toBeInTheDocument()
    expect(screen.getByText(/global Runner inventory/i)).toBeInTheDocument()
  })
})
