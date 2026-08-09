import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, render, screen } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router-dom'
import { ProjectProvider } from '../../../entities/project'
import { SubscriptionsSection, type SubscriptionOperationsHook } from './SubscriptionsSection'
import type { AgentInfo, AgentSubscriptionDto, AgentSubscriptionListDto } from '../../../entities/agent'

const mocks = { data: undefined as AgentSubscriptionListDto | undefined, loading: false, error: false, create: [] as unknown[], createFailures: 0, update: [] as unknown[], remove: [] as unknown[] }

function subscription(overrides: Partial<AgentSubscriptionDto> = {}): AgentSubscriptionDto {
  return {
    id: 'rule_x', projectId: 'proj-1', agentId: 'agent-1', name: 'fallback',
    match: 'event.type == "com.example.failed"', responsePrompt: 'inspect', continue: false,
    position: 1, status: 'active', createdAt: '2026-08-09T00:00:00.000Z', updatedAt: '2026-08-09T00:00:00.000Z', ...overrides,
  }
}

function agent(overrides: Partial<AgentInfo> = {}): AgentInfo {
  return {
    id: 'agent-1', projectId: 'proj-1', name: 'Agent', description: '', instructions: '...', agentConfig: null,
    skills: [], maxConcurrentRuns: null, status: 'active', createdAt: '', updatedAt: '', ...overrides,
  }
}

const operations: SubscriptionOperationsHook = () => ({
  subscriptionsQuery: { data: mocks.data, isLoading: mocks.loading, isError: mocks.error, error: mocks.error ? new Error('request failed') : null },
  createMutation: { mutate: (data: unknown, options?: { onSuccess?: () => void }) => { mocks.create.push(data); if (mocks.createFailures > 0) mocks.createFailures -= 1; else options?.onSuccess?.() }, isPending: false },
  updateMutation: { mutate: (data: unknown, options?: { onSuccess?: () => void }) => { mocks.update.push(data); options?.onSuccess?.() }, isPending: false },
  deleteMutation: { mutate: (data: unknown, options?: { onSuccess?: () => void }) => { mocks.remove.push(data); options?.onSuccess?.() }, isPending: false },
})

function renderSection(value = agent()) {
  return render(
    <QueryClientProvider client={new QueryClient()}>
      <ProjectProvider initialProjectId="proj-1" initialProjects={[{ id: 'proj-1', name: 'Project', createdAt: '', updatedAt: '', repositories: [] }]}>
        <MemoryRouter><SubscriptionsSection agent={value} operationsHook={operations} /></MemoryRouter>
      </ProjectProvider>
    </QueryClientProvider>,
  )
}

describe('SubscriptionsSection', () => {
  beforeEach(() => {
    mocks.data = { subscriptions: [], state: 'empty', agentStatus: 'active', readiness: 'Ready', connection: 'no_connection' }
    mocks.loading = false; mocks.error = false; mocks.create.length = 0; mocks.createFailures = 0; mocks.update.length = 0; mocks.remove.length = 0
  })
  afterEach(() => { cleanup(); vi.clearAllMocks() })

  it('shows the explicit empty state and connection state', () => {
    renderSection()
    expect(screen.getByTestId('agent-subscriptions-empty')).toHaveTextContent('No subscriptions configured')
    expect(screen.getByTestId('agent-subscriptions-state')).toHaveTextContent('no_connection')
  })

  it('does not turn a failed request into an empty list', () => {
    mocks.error = true
    renderSection()
    expect(screen.getByTestId('agent-subscriptions-error')).toHaveTextContent('request failed')
    expect(screen.queryByTestId('agent-subscriptions-empty')).not.toBeInTheDocument()
  })

  it('renders the canonical match and supports edit', () => {
    mocks.data = { ...mocks.data!, state: 'configured', subscriptions: [subscription()] }
    renderSection()
    expect(screen.getByTestId('agent-subscription-row-rule_x-match')).toHaveTextContent('event.type')
    fireEvent.click(screen.getByTestId('agent-subscription-edit-rule_x'))
    fireEvent.change(screen.getByTestId('subscription-create-name'), { target: { value: 'updated' } })
    fireEvent.click(screen.getByTestId('subscription-create-submit'))
    expect(mocks.update).toEqual([{ subscriptionId: 'rule_x', data: expect.objectContaining({ name: 'updated' }) }])
  })

  it('allows configuration to be inspected but not changed for an archived Agent', () => {
    mocks.data = { ...mocks.data!, subscriptions: [subscription()] }
    renderSection(agent({ status: 'archived' }))
    expect(screen.getByTestId('agent-subscriptions-create')).toBeDisabled()
    expect(screen.getByTestId('agent-subscription-edit-rule_x')).toBeDisabled()
    expect(screen.getByTestId('agent-subscriptions-archived-notice')).toBeInTheDocument()
  })

  it('keeps the generated key when the create response is lost and the form is retried', () => {
    mocks.createFailures = 1
    renderSection()
    fireEvent.click(screen.getByTestId('agent-subscriptions-create'))
    fireEvent.change(screen.getByTestId('subscription-create-name'), { target: { value: 'fallback' } })
    fireEvent.change(screen.getByTestId('subscription-create-match'), { target: { value: 'event.type == "x"' } })
    fireEvent.change(screen.getByTestId('subscription-create-response-prompt'), { target: { value: 'inspect' } })
    fireEvent.click(screen.getByTestId('subscription-create-submit'))

    const first = mocks.create[0] as { idempotencyKey: string }
    expect(first.idempotencyKey).toBeTruthy()
    expect(screen.getByTestId('subscription-create-idempotency-key')).toHaveTextContent(first.idempotencyKey)

    fireEvent.click(screen.getByTestId('subscription-create-submit'))
    const second = mocks.create[1] as { idempotencyKey: string }
    expect(second.idempotencyKey).toBe(first.idempotencyKey)
  })
})
