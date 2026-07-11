import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { act, cleanup, render, screen, fireEvent, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router-dom'
import { ProjectProvider } from '../../../entities/project'
import { SubscriptionsSection, type SubscriptionOperationsHook } from './SubscriptionsSection'
import type { AgentInfo, AgentSubscriptionDto } from '../../../entities/agent'

const mocks = {
  subscriptions: [] as AgentSubscriptionDto[],
  subscriptionsLoading: false,
  listMutateCalls: [] as Array<{ data: unknown; options?: unknown }>,
  archiveMutateCalls: [] as Array<{ vars: { subscriptionId: string }; options?: unknown }>,
  restoreMutateCalls: [] as Array<{ vars: { subscriptionId: string }; options?: unknown }>,
  deleteMutateCalls: [] as Array<{ vars: { subscriptionId: string }; options?: unknown }>,
  createPending: false,
  archivePending: false,
  restorePending: false,
  deletePending: false,
}

function makeSubscription(overrides: Partial<AgentSubscriptionDto> = {}): AgentSubscriptionDto {
  return {
    id: 'subs_default',
    projectId: 'proj-1',
    agentId: 'agent-1',
    name: 'fallback',
    filter: {
      type: 'com.mohist.workflow.stage.*',
      source: null,
      subject: null,
    },
    responsePrompt: 'Approve the workflow if the proposal is clear and complete.',
    priority: null,
    status: 'active',
    createdAt: '2026-06-01T00:00:00.000Z',
    updatedAt: '2026-06-01T00:00:00.000Z',
    ...overrides,
  }
}

function makeAgent(overrides: Partial<AgentInfo> = {}): AgentInfo {
  return {
    id: 'agent-1',
    projectId: 'proj-1',
    name: 'Test Agent',
    description: '',
    instructions: '...',
    agentConfig: null,
    skills: [],
    maxConcurrentRuns: null,
    status: 'active',
    createdAt: '2026-06-01T00:00:00.000Z',
    updatedAt: '2026-06-01T00:00:00.000Z',
    ...overrides,
  }
}

function createQueryClient() {
  return new QueryClient({ defaultOptions: { queries: { retry: false } } })
}

const operationsHook: SubscriptionOperationsHook = () => ({
  subscriptionsQuery: {
    data: mocks.subscriptions,
    isLoading: mocks.subscriptionsLoading,
  },
  createMutation: {
    mutate: (data, options) => {
      mocks.listMutateCalls.push({ data, options })
      const onSuccess = options?.onSuccess as ((created: AgentSubscriptionDto) => void) | undefined
      onSuccess?.(makeSubscription({ id: 'subs_new', name: data.name }))
    },
    isPending: mocks.createPending,
  },
  archiveMutation: {
    mutate: (vars, options) => {
      mocks.archiveMutateCalls.push({ vars, options })
    },
    isPending: mocks.archivePending,
  },
  restoreMutation: {
    mutate: (vars, options) => {
      mocks.restoreMutateCalls.push({ vars, options })
    },
    isPending: mocks.restorePending,
  },
  deleteMutation: {
    mutate: (vars, options) => {
      mocks.deleteMutateCalls.push({ vars, options })
      const onSuccess = options?.onSuccess as (() => void) | undefined
      onSuccess?.()
    },
    isPending: mocks.deletePending,
  },
})

function renderSection(agent: AgentInfo = makeAgent()) {
  const queryClient = createQueryClient()
  return render(
    <QueryClientProvider client={queryClient}>
      <ProjectProvider initialProjectId="proj-1" initialProjects={[{
        id: 'proj-1', name: 'Test',
        createdAt: '2026-01-01T00:00:00.000Z', updatedAt: '2026-01-01T00:00:00.000Z',
        repositories: [],
      }]}>
        <MemoryRouter>
          <SubscriptionsSection agent={agent} operationsHook={operationsHook} />
        </MemoryRouter>
      </ProjectProvider>
    </QueryClientProvider>,
  )
}

describe('SubscriptionsSection', () => {
  beforeEach(() => {
    mocks.subscriptions = []
    mocks.subscriptionsLoading = false
    mocks.listMutateCalls.length = 0
    mocks.archiveMutateCalls.length = 0
    mocks.restoreMutateCalls.length = 0
    mocks.deleteMutateCalls.length = 0
    mocks.createPending = false
    mocks.archivePending = false
    mocks.restorePending = false
    mocks.deletePending = false
  })

  afterEach(() => {
    cleanup()
    vi.clearAllMocks()
  })

  describe('rendering', () => {
    it('renders the section heading', () => {
      renderSection()
      expect(screen.getByText('Subscriptions')).toBeInTheDocument()
    })

    it('shows the empty state when there are no subscriptions', () => {
      renderSection()
      expect(screen.getByTestId('agent-subscriptions-empty')).toBeInTheDocument()
    })

    it('shows the loading state when the query is loading', () => {
      mocks.subscriptionsLoading = true
      renderSection()
      expect(screen.getByTestId('agent-subscriptions-loading')).toBeInTheDocument()
    })

    it('renders each subscription with name, filter, priority, status, and a prompt preview', () => {
      mocks.subscriptions = [
        makeSubscription({
          id: 'subs_a',
          name: 'fallback',
          filter: { type: 'com.mohist.workflow.stage.*', source: null, subject: null },
          priority: null,
          status: 'active',
          responsePrompt: 'Approve clearly.',
        }),
        makeSubscription({
          id: 'subs_b',
          name: 'takeover-42',
          filter: {
            type: 'com.mohist.workflow.stage.approval-requested',
            source: '/mohist/workflow-runs/run_x',
            subject: null,
          },
          priority: 10,
          status: 'archived',
        }),
      ]
      renderSection()
      expect(screen.getByTestId('agent-subscription-row-subs_a')).toBeInTheDocument()
      expect(screen.getByTestId('agent-subscription-row-subs_a-filter')).toHaveTextContent(
        'com.mohist.workflow.stage.*',
      )
      expect(screen.getByTestId('agent-subscription-row-subs_a-priority')).toHaveTextContent('default')
      expect(screen.getByTestId('agent-subscription-row-subs_a-prompt-preview')).toHaveTextContent(
        'Approve clearly.',
      )
      expect(screen.getByTestId('agent-subscription-row-subs_a')).toHaveAttribute(
        'data-subscription-status',
        'active',
      )

      expect(screen.getByTestId('agent-subscription-row-subs_b-filter')).toHaveTextContent(
        'com.mohist.workflow.stage.approval-requested',
      )
      expect(screen.getByTestId('agent-subscription-row-subs_b-filter')).toHaveTextContent(
        'source=/mohist/workflow-runs/run_x',
      )
      expect(screen.getByTestId('agent-subscription-row-subs_b-priority')).toHaveTextContent('priority 10')
      expect(screen.getByTestId('agent-subscription-row-subs_b')).toHaveAttribute(
        'data-subscription-status',
        'archived',
      )
    })
  })

  describe('archive / restore', () => {
    it('renders archive button on an active subscription and calls useArchiveAgentSubscription on click', () => {
      mocks.subscriptions = [makeSubscription({ id: 'subs_active', status: 'active' })]
      renderSection()
      const archiveBtn = screen.getByTestId('agent-subscription-archive-subs_active')
      fireEvent.click(archiveBtn)
      expect(mocks.archiveMutateCalls).toHaveLength(1)
      expect(mocks.archiveMutateCalls[0].vars.subscriptionId).toBe('subs_active')
    })

    it('renders restore button on an archived subscription and calls useRestoreAgentSubscription on click', () => {
      mocks.subscriptions = [makeSubscription({ id: 'subs_archived', status: 'archived' })]
      renderSection()
      const restoreBtn = screen.getByTestId('agent-subscription-restore-subs_archived')
      fireEvent.click(restoreBtn)
      expect(mocks.restoreMutateCalls).toHaveLength(1)
      expect(mocks.restoreMutateCalls[0].vars.subscriptionId).toBe('subs_archived')
    })
  })

  describe('delete', () => {
    it('opens a confirmation dialog when delete is clicked', () => {
      mocks.subscriptions = [makeSubscription({ id: 'subs_x', status: 'active' })]
      renderSection()
      fireEvent.click(screen.getByTestId('agent-subscription-delete-subs_x'))
      expect(screen.getByTestId('agent-subscription-delete-confirm-dialog')).toBeInTheDocument()
      expect(screen.getByTestId('agent-subscription-delete-confirm')).toBeInTheDocument()
    })

    it('calls useDeleteAgentSubscription when the confirmation is accepted', () => {
      mocks.subscriptions = [makeSubscription({ id: 'subs_x', status: 'active' })]
      renderSection()
      fireEvent.click(screen.getByTestId('agent-subscription-delete-subs_x'))
      fireEvent.click(screen.getByTestId('agent-subscription-delete-confirm'))
      expect(mocks.deleteMutateCalls).toHaveLength(1)
      expect(mocks.deleteMutateCalls[0].vars.subscriptionId).toBe('subs_x')
    })

    it('does NOT call delete when the confirmation is cancelled', () => {
      mocks.subscriptions = [makeSubscription({ id: 'subs_x', status: 'active' })]
      renderSection()
      fireEvent.click(screen.getByTestId('agent-subscription-delete-subs_x'))
      fireEvent.click(screen.getByTestId('agent-subscription-delete-cancel'))
      expect(screen.queryByTestId('agent-subscription-delete-confirm-dialog')).not.toBeInTheDocument()
      expect(mocks.deleteMutateCalls).toHaveLength(0)
    })
  })

  describe('create flow', () => {
    function fillForm() {
      fireEvent.change(screen.getByTestId('subscription-create-name'), {
        target: { value: 'approver' },
      })
      fireEvent.change(screen.getByTestId('subscription-create-filter-type'), {
        target: { value: 'com.mohist.workflow.stage.approval-requested' },
      })
      fireEvent.change(screen.getByTestId('subscription-create-response-prompt'), {
        target: { value: 'Approve if clear' },
      })
    }

    it('opens the create dialog when the create button is clicked', () => {
      renderSection()
      fireEvent.click(screen.getByTestId('agent-subscriptions-create'))
      expect(screen.getByTestId('agent-subscriptions-create-dialog')).toBeInTheDocument()
    })

    it('blocks submission when required fields are missing', async () => {
      renderSection()
      fireEvent.click(screen.getByTestId('agent-subscriptions-create'))
      await act(async () => {
        screen.getByTestId('subscription-create-submit').click()
      })
      await waitFor(() => {
        expect(screen.getByTestId('subscription-create-name-error')).toHaveTextContent('Name is required')
      })
      expect(screen.getByTestId('subscription-create-filter-type-error')).toHaveTextContent(
        'Filter type is required',
      )
      expect(screen.getByTestId('subscription-create-response-prompt-error')).toHaveTextContent(
        'Response prompt is required',
      )
      expect(mocks.listMutateCalls).toHaveLength(0)
    })

    it('blocks submission when priority is non-numeric', async () => {
      renderSection()
      fireEvent.click(screen.getByTestId('agent-subscriptions-create'))
      fillForm()
      fireEvent.change(screen.getByTestId('subscription-create-priority'), {
        target: { value: 'notanumber' },
      })
      await act(async () => {
        screen.getByTestId('subscription-create-submit').click()
      })
      await waitFor(() => {
        expect(screen.getByTestId('subscription-create-priority-error')).toBeInTheDocument()
      })
      expect(mocks.listMutateCalls).toHaveLength(0)
    })

    it('calls useCreateAgentSubscription with trimmed values and optional priority', async () => {
      renderSection()
      fireEvent.click(screen.getByTestId('agent-subscriptions-create'))
      fillForm()
      fireEvent.change(screen.getByTestId('subscription-create-filter-source'), {
        target: { value: '/mohist/workflow-runs/run_x' },
      })
      fireEvent.change(screen.getByTestId('subscription-create-priority'), {
        target: { value: '5' },
      })
      await act(async () => {
        screen.getByTestId('subscription-create-submit').click()
      })
      expect(mocks.listMutateCalls).toHaveLength(1)
      expect(mocks.listMutateCalls[0].data).toEqual({
        name: 'approver',
        filter: {
          type: 'com.mohist.workflow.stage.approval-requested',
          source: '/mohist/workflow-runs/run_x',
          subject: null,
        },
        responsePrompt: 'Approve if clear',
        priority: 5,
      })
    })

    it('passes priority=null when the priority field is left blank', async () => {
      renderSection()
      fireEvent.click(screen.getByTestId('agent-subscriptions-create'))
      fillForm()
      await act(async () => {
        screen.getByTestId('subscription-create-submit').click()
      })
      expect(mocks.listMutateCalls).toHaveLength(1)
      expect(mocks.listMutateCalls[0].data).toMatchObject({ priority: null })
    })
  })

  describe('archived-agent gating', () => {
    it('disables the create button on an archived agent and renders the notice', () => {
      renderSection(makeAgent({ status: 'archived' }))
      expect(screen.getByTestId('agent-subscriptions-create')).toBeDisabled()
      expect(screen.getByTestId('agent-subscriptions-archived-notice')).toBeInTheDocument()
    })

    it('also disables archive buttons for subscriptions on archived agents (the agent is the boundary)', () => {
      mocks.subscriptions = [makeSubscription({ id: 'subs_x', status: 'active' })]
      renderSection(makeAgent({ status: 'archived' }))
      expect(screen.getByTestId('agent-subscription-archive-subs_x')).toBeDisabled()
      expect(screen.queryByTestId('agent-subscription-delete-subs_x')).not.toBeDisabled()
    })
  })
})
