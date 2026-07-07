import { beforeEach, describe, expect, it, vi } from 'vitest'
import {
  agentSubscriptionsQueryKey,
  useAgentSubscriptions,
  useArchiveAgentSubscription,
  useCreateAgentSubscription,
  useDeleteAgentSubscription,
  useRestoreAgentSubscription,
} from './subscription-queries'

const useQueryMock = vi.fn()
const useMutationMock = vi.fn()
const useQueryClientMock = vi.fn()
const useProjectMock = vi.fn()
const listAgentSubscriptionsMock = vi.fn()
const createAgentSubscriptionMock = vi.fn()
const archiveAgentSubscriptionMock = vi.fn()
const restoreAgentSubscriptionMock = vi.fn()
const deleteAgentSubscriptionMock = vi.fn()
const invalidateQueriesMock = vi.fn()
const toastSuccessMock = vi.fn()
const toastErrorMock = vi.fn()

vi.mock('@tanstack/react-query', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@tanstack/react-query')>()),
  useQuery: (...args: unknown[]) => useQueryMock(...args),
  useMutation: (...args: unknown[]) => useMutationMock(...args),
  useQueryClient: () => useQueryClientMock(),
}))

vi.mock('../../project/@x/project-context', () => ({
  useProject: () => useProjectMock(),
}))

vi.mock('./subscriptions', async (importOriginal) => {
  const actual = await importOriginal<typeof import('./subscriptions')>()
  return {
    ...actual,
    listAgentSubscriptions: (...args: unknown[]) => listAgentSubscriptionsMock(...args),
    createAgentSubscription: (...args: unknown[]) => createAgentSubscriptionMock(...args),
    archiveAgentSubscription: (...args: unknown[]) => archiveAgentSubscriptionMock(...args),
    restoreAgentSubscription: (...args: unknown[]) => restoreAgentSubscriptionMock(...args),
    deleteAgentSubscription: (...args: unknown[]) => deleteAgentSubscriptionMock(...args),
  }
})

vi.mock('sonner', () => ({
  toast: {
    success: (...args: unknown[]) => toastSuccessMock(...args),
    error: (...args: unknown[]) => toastErrorMock(...args),
  },
}))

function getLastQueryOptions() {
  const calls = useQueryMock.mock.calls
  return calls[calls.length - 1][0] as {
    queryKey: unknown[]
    queryFn: () => unknown
    enabled: boolean
  }
}

function getLastMutationOptions(): {
  mutationFn: (...a: unknown[]) => Promise<unknown>
  onSuccess: (...a: unknown[]) => void
  onError: (...a: unknown[]) => void
} {
  const calls = useMutationMock.mock.calls
  const last = calls[calls.length - 1][0] as {
    mutationFn: (...a: unknown[]) => Promise<unknown>
    onSuccess: (...a: unknown[]) => void
    onError: (...a: unknown[]) => void
  }
  return last
}

beforeEach(() => {
  useQueryMock.mockReset()
  useMutationMock.mockReset()
  useQueryClientMock.mockReset()
  useProjectMock.mockReset()
  listAgentSubscriptionsMock.mockReset()
  createAgentSubscriptionMock.mockReset()
  archiveAgentSubscriptionMock.mockReset()
  restoreAgentSubscriptionMock.mockReset()
  deleteAgentSubscriptionMock.mockReset()
  invalidateQueriesMock.mockReset()
  toastSuccessMock.mockReset()
  toastErrorMock.mockReset()
  useProjectMock.mockReturnValue({ projectId: 'proj-1' })
  useQueryClientMock.mockReturnValue({ invalidateQueries: invalidateQueriesMock })
  useQueryMock.mockReturnValue({ data: [], isLoading: false })
  useMutationMock.mockImplementation((options: unknown) => ({ mutate: vi.fn(), options }))
})

/* ── query keys ──────────────────────────────────────────── */

describe('agentSubscriptionsQueryKey', () => {
  it('uses the segments required for per-agent scoping', () => {
    expect(agentSubscriptionsQueryKey('proj-1', 'agent-1')).toEqual([
      'agents',
      'proj-1',
      'agent-1',
      'subscriptions',
    ])
  })
})

/* ── useAgentSubscriptions ───────────────────────────────── */

describe('useAgentSubscriptions', () => {
  it('uses the canonical subscription query key', () => {
    useAgentSubscriptions('agent-1')
    const opts = getLastQueryOptions()
    expect(opts.queryKey).toEqual([
      'agents',
      'proj-1',
      'agent-1',
      'subscriptions',
    ])
  })

  it('is enabled only when both projectId and agentRef are present', () => {
    useProjectMock.mockReturnValue({ projectId: 'proj-1' })
    useAgentSubscriptions('agent-1')
    expect(getLastQueryOptions().enabled).toBe(true)

    useProjectMock.mockReturnValue({ projectId: null })
    useAgentSubscriptions('agent-1')
    expect(getLastQueryOptions().enabled).toBe(false)

    useProjectMock.mockReturnValue({ projectId: 'proj-1' })
    useAgentSubscriptions('')
    expect(getLastQueryOptions().enabled).toBe(false)
  })

  it('calls listAgentSubscriptions(projectId, agentRef) inside the queryFn', async () => {
    listAgentSubscriptionsMock.mockResolvedValue([])
    useAgentSubscriptions('agent-1')
    const opts = getLastQueryOptions()
    await opts.queryFn()
    expect(listAgentSubscriptionsMock).toHaveBeenCalledWith('proj-1', 'agent-1')
  })
})

/* ── useCreateAgentSubscription ──────────────────────────── */

describe('useCreateAgentSubscription', () => {
  it('mutationFn calls createAgentSubscription(projectId, agentRef, data)', async () => {
    useCreateAgentSubscription('agent-1')
    const opts = getLastMutationOptions()
    createAgentSubscriptionMock.mockResolvedValue({
      id: 'subs_new',
      name: 'fallback',
    })
    await opts.mutationFn({
      name: 'fallback',
      filter: { type: 'com.mohist.workflow.stage.*' },
      responsePrompt: 'approve',
    })
    expect(createAgentSubscriptionMock).toHaveBeenCalledWith('proj-1', 'agent-1', {
      name: 'fallback',
      filter: { type: 'com.mohist.workflow.stage.*' },
      responsePrompt: 'approve',
    })
  })

  it('invalidates the subscriptions query on success', () => {
    useCreateAgentSubscription('agent-1')
    getLastMutationOptions().onSuccess({ id: 'subs_new', name: 'fallback' })
    expect(invalidateQueriesMock).toHaveBeenCalledWith({
      queryKey: ['agents', 'proj-1', 'agent-1', 'subscriptions'],
    })
    expect(invalidateQueriesMock).toHaveBeenCalledWith({
      queryKey: ['agents', 'proj-1', 'agent-1'],
    })
  })

  it('shows a success toast with the created subscription name', () => {
    useCreateAgentSubscription('agent-1')
    getLastMutationOptions().onSuccess({ id: 'subs_new', name: 'fallback' })
    expect(toastSuccessMock).toHaveBeenCalledWith('Subscription "fallback" created')
  })

  it('shows an error toast on failure (preserving the server message)', () => {
    useCreateAgentSubscription('agent-1')
    getLastMutationOptions().onError(new Error('Archived agents cannot receive new subscriptions'))
    expect(toastErrorMock).toHaveBeenCalledWith('Archived agents cannot receive new subscriptions')
  })
})

/* ── useArchiveAgentSubscription ─────────────────────────── */

describe('useArchiveAgentSubscription', () => {
  it('mutationFn calls archiveAgentSubscription(projectId, agentRef, subscriptionId)', async () => {
    useArchiveAgentSubscription('agent-1')
    const opts = getLastMutationOptions()
    archiveAgentSubscriptionMock.mockResolvedValue({ id: 'subs_x', name: 'fallback', status: 'archived' })
    await opts.mutationFn({ subscriptionId: 'subs_x' })
    expect(archiveAgentSubscriptionMock).toHaveBeenCalledWith('proj-1', 'agent-1', 'subs_x')
  })

  it('invalidates the subscriptions query on success', () => {
    useArchiveAgentSubscription('agent-1')
    getLastMutationOptions().onSuccess({ id: 'subs_x', name: 'fallback', status: 'archived' })
    expect(invalidateQueriesMock).toHaveBeenCalledWith({
      queryKey: ['agents', 'proj-1', 'agent-1', 'subscriptions'],
    })
  })

  it('shows a success toast with the subscription name', () => {
    useArchiveAgentSubscription('agent-1')
    getLastMutationOptions().onSuccess({ id: 'subs_x', name: 'fallback', status: 'archived' })
    expect(toastSuccessMock).toHaveBeenCalledWith('Subscription "fallback" archived')
  })

  it('shows an error toast on failure', () => {
    useArchiveAgentSubscription('agent-1')
    getLastMutationOptions().onError(new Error('NOT_FOUND'))
    expect(toastErrorMock).toHaveBeenCalledWith('NOT_FOUND')
  })
})

/* ── useRestoreAgentSubscription ─────────────────────────── */

describe('useRestoreAgentSubscription', () => {
  it('mutationFn calls restoreAgentSubscription(projectId, agentRef, subscriptionId)', async () => {
    useRestoreAgentSubscription('agent-1')
    const opts = getLastMutationOptions()
    restoreAgentSubscriptionMock.mockResolvedValue({ id: 'subs_x', name: 'fallback', status: 'active' })
    await opts.mutationFn({ subscriptionId: 'subs_x' })
    expect(restoreAgentSubscriptionMock).toHaveBeenCalledWith('proj-1', 'agent-1', 'subs_x')
  })

  it('invalidates the subscriptions query on success', () => {
    useRestoreAgentSubscription('agent-1')
    getLastMutationOptions().onSuccess({ id: 'subs_x', name: 'fallback', status: 'active' })
    expect(invalidateQueriesMock).toHaveBeenCalledWith({
      queryKey: ['agents', 'proj-1', 'agent-1', 'subscriptions'],
    })
  })

  it('shows a success toast with the subscription name', () => {
    useRestoreAgentSubscription('agent-1')
    getLastMutationOptions().onSuccess({ id: 'subs_x', name: 'fallback', status: 'active' })
    expect(toastSuccessMock).toHaveBeenCalledWith('Subscription "fallback" restored')
  })

  it('shows an error toast on failure', () => {
    useRestoreAgentSubscription('agent-1')
    getLastMutationOptions().onError(new Error('NOT_FOUND'))
    expect(toastErrorMock).toHaveBeenCalledWith('NOT_FOUND')
  })
})

/* ── useDeleteAgentSubscription ──────────────────────────── */

describe('useDeleteAgentSubscription', () => {
  it('mutationFn calls deleteAgentSubscription(projectId, agentRef, subscriptionId)', async () => {
    useDeleteAgentSubscription('agent-1')
    const opts = getLastMutationOptions()
    deleteAgentSubscriptionMock.mockResolvedValue(null)
    await opts.mutationFn({ subscriptionId: 'subs_x' })
    expect(deleteAgentSubscriptionMock).toHaveBeenCalledWith('proj-1', 'agent-1', 'subs_x')
  })

  it('invalidates the subscriptions query on success', () => {
    useDeleteAgentSubscription('agent-1')
    getLastMutationOptions().onSuccess(null, { subscriptionId: 'subs_x' })
    expect(invalidateQueriesMock).toHaveBeenCalledWith({
      queryKey: ['agents', 'proj-1', 'agent-1', 'subscriptions'],
    })
  })

  it('shows a success toast mentioning the deleted subscription id', () => {
    useDeleteAgentSubscription('agent-1')
    getLastMutationOptions().onSuccess(null, { subscriptionId: 'subs_x' })
    expect(toastSuccessMock).toHaveBeenCalledWith('Subscription subs_x deleted')
  })

  it('shows an error toast on failure', () => {
    useDeleteAgentSubscription('agent-1')
    getLastMutationOptions().onError(new Error('NOT_FOUND'))
    expect(toastErrorMock).toHaveBeenCalledWith('NOT_FOUND')
  })
})
