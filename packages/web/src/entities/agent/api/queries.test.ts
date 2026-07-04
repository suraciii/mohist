import { beforeEach, describe, expect, it, vi } from 'vitest'
import { useAgent, useAgentSessions, useAgents, useArchiveAgent, useCreateAgent, useUnarchiveAgent, useUpdateAgent } from './queries'

const useQueryMock = vi.fn()
const useMutationMock = vi.fn()
const useQueryClientMock = vi.fn()
const useProjectMock = vi.fn()
const listAgentsMock = vi.fn()
const getAgentMock = vi.fn()
const createAgentMock = vi.fn()
const updateAgentMock = vi.fn()
const archiveAgentMock = vi.fn()
const unarchiveAgentMock = vi.fn()
const getAgentScopedSessionsMock = vi.fn()
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

vi.mock('./client', async (importOriginal) => {
  const actual = await importOriginal<typeof import('./client')>()
  return {
    ...actual,
    listAgents: (...args: unknown[]) => listAgentsMock(...args),
    getAgent: (...args: unknown[]) => getAgentMock(...args),
    createAgent: (...args: unknown[]) => createAgentMock(...args),
    updateAgent: (...args: unknown[]) => updateAgentMock(...args),
    archiveAgent: (...args: unknown[]) => archiveAgentMock(...args),
    unarchiveAgent: (...args: unknown[]) => unarchiveAgentMock(...args),
  }
})

vi.mock('./agent-sessions', () => ({
  getAgentSessions: (...args: unknown[]) => getAgentScopedSessionsMock(...args),
}))

vi.mock('sonner', () => ({
  toast: {
    success: (...args: unknown[]) => toastSuccessMock(...args),
    error: (...args: unknown[]) => toastErrorMock(...args),
  },
}))

beforeEach(() => {
  useQueryMock.mockReset()
  useMutationMock.mockReset()
  useQueryClientMock.mockReset()
  useProjectMock.mockReset()
  listAgentsMock.mockReset()
  getAgentMock.mockReset()
  createAgentMock.mockReset()
  updateAgentMock.mockReset()
  archiveAgentMock.mockReset()
  unarchiveAgentMock.mockReset()
  getAgentScopedSessionsMock.mockReset()
  invalidateQueriesMock.mockReset()
  toastSuccessMock.mockReset()
  toastErrorMock.mockReset()
  useProjectMock.mockReturnValue({ projectId: 'proj-1' })
  useQueryClientMock.mockReturnValue({ invalidateQueries: invalidateQueriesMock })
  useQueryMock.mockReturnValue({ data: [], isLoading: false })
})

function getLastQueryOptions() {
  const calls = useQueryMock.mock.calls
  return calls[calls.length - 1][0] as { queryKey: unknown[]; queryFn: () => unknown; enabled: boolean }
}

function getLastMutationOptions(): {
  mutationFn: (...a: unknown[]) => unknown
  onSuccess: (...a: unknown[]) => void
  onError: (...a: unknown[]) => void
} {
  const calls = useMutationMock.mock.calls
  const last = calls[calls.length - 1][0] as {
    mutationFn: (...a: unknown[]) => unknown
    onSuccess: (...a: unknown[]) => void
    onError: (...a: unknown[]) => void
  }
  return last
}

/* ── useAgents ──────────────────────────────────────────── */
describe('useAgents', () => {
  it('uses query key ["agents", projectId]', () => {
    useAgents()
    const opts = getLastQueryOptions()
    expect(opts.queryKey).toEqual(['agents', 'proj-1'])
  })

  it('is enabled only when projectId is present', () => {
    useProjectMock.mockReturnValue({ projectId: 'proj-1' })
    useAgents()
    expect(getLastQueryOptions().enabled).toBe(true)

    useProjectMock.mockReturnValue({ projectId: null })
    useAgents()
    expect(getLastQueryOptions().enabled).toBe(false)
  })

  it('calls listAgents(projectId, { all: true }) so archived agents are included', async () => {
    listAgentsMock.mockResolvedValue([{ id: 'a1' }, { id: 'a2', status: 'archived' }])
    useAgents()
    const opts = getLastQueryOptions()
    await opts.queryFn()
    expect(listAgentsMock).toHaveBeenCalledWith('proj-1', { all: true })
  })

  it('returns typed AgentInfo[]', () => {
    const data = [{ id: 'a1', name: 'A1' }]
    useQueryMock.mockReturnValue({ data, isLoading: false })
    // Test is about the hook's return — ensure typing works
    const result = useAgents()
    expect(result.data).toEqual(data)
  })
})

/* ── useAgent ───────────────────────────────────────────── */
describe('useAgent', () => {
  it('uses query key ["agents", projectId, agentRef]', () => {
    useAgent('agent-alpha')
    const opts = getLastQueryOptions()
    expect(opts.queryKey).toEqual(['agents', 'proj-1', 'agent-alpha'])
  })

  it('is enabled only when projectId and agentRef are present', () => {
    useProjectMock.mockReturnValue({ projectId: 'proj-1' })
    useAgent('alpha')
    expect(getLastQueryOptions().enabled).toBe(true)

    useProjectMock.mockReturnValue({ projectId: null })
    useAgent('alpha')
    expect(getLastQueryOptions().enabled).toBe(false)

    useProjectMock.mockReturnValue({ projectId: 'proj-1' })
    useAgent('')
    expect(getLastQueryOptions().enabled).toBe(false)
  })

  it('calls getAgent(projectId, agentRef) as the query function', async () => {
    getAgentMock.mockResolvedValue({ id: 'a1' })
    useAgent('agent-beta')
    const opts = getLastQueryOptions()
    await opts.queryFn()
    expect(getAgentMock).toHaveBeenCalledWith('proj-1', 'agent-beta')
  })
})

/* ── useAgentSessions ───────────────────────────────────── */
describe('useAgentSessions', () => {
  it('uses query key ["agents", projectId, agentRef, "sessions"]', () => {
    useAgentSessions({ agentRef: 'agent-gamma' })
    const opts = getLastQueryOptions()
    expect(opts.queryKey).toEqual(['agents', 'proj-1', 'agent-gamma', 'sessions'])
  })

  it('is enabled only when projectId and agentRef are present', () => {
    useProjectMock.mockReturnValue({ projectId: 'proj-1' })
    useAgentSessions({ agentRef: 'gamma' })
    expect(getLastQueryOptions().enabled).toBe(true)

    useProjectMock.mockReturnValue({ projectId: null })
    useAgentSessions({ agentRef: 'gamma' })
    expect(getLastQueryOptions().enabled).toBe(false)

    useProjectMock.mockReturnValue({ projectId: 'proj-1' })
    useAgentSessions({ agentRef: '' })
    expect(getLastQueryOptions().enabled).toBe(false)
  })

  it('calls getAgentScopedSessions(projectId, agentRef)', async () => {
    getAgentScopedSessionsMock.mockResolvedValue([{ sessionId: 's1' }])
    useAgentSessions({ agentRef: 'gamma' })
    const opts = getLastQueryOptions()
    await opts.queryFn()
    expect(getAgentScopedSessionsMock).toHaveBeenCalledWith('proj-1', 'gamma')
  })
})

/* ── useCreateAgent ─────────────────────────────────────── */
describe('useCreateAgent', () => {
  beforeEach(() => {
    useMutationMock.mockImplementation((options: unknown) => ({ mutate: vi.fn(), options }))
  })

  it('calls createAgent(projectId, data) in mutationFn', () => {
    useCreateAgent()
    const opts = getLastMutationOptions()
    createAgentMock.mockResolvedValue({ id: 'new-1' })
    void opts.mutationFn({ name: 'New Agent', instructions: 'Do things' } as Parameters<typeof opts.mutationFn>[0])
    expect(createAgentMock).toHaveBeenCalledWith('proj-1', { name: 'New Agent', instructions: 'Do things' })
  })

  it('invalidates ["agents"] on success', () => {
    useCreateAgent()
    getLastMutationOptions().onSuccess()
    expect(invalidateQueriesMock).toHaveBeenCalledWith({ queryKey: ['agents'] })
  })

  it('shows success toast on success', () => {
    useCreateAgent()
    getLastMutationOptions().onSuccess()
    expect(toastSuccessMock).toHaveBeenCalledWith('Agent created')
  })

  it('shows error toast on failure', () => {
    useCreateAgent()
    getLastMutationOptions().onError(new Error('NAME_REQUIRED'))
    expect(toastErrorMock).toHaveBeenCalledWith('NAME_REQUIRED')
  })
})

/* ── useUpdateAgent ─────────────────────────────────────── */
describe('useUpdateAgent', () => {
  beforeEach(() => {
    useMutationMock.mockImplementation((options: unknown) => ({ mutate: vi.fn(), options }))
  })

  it('calls updateAgent(projectId, agentRef, data) in mutationFn', () => {
    useUpdateAgent()
    const opts = getLastMutationOptions()
    updateAgentMock.mockResolvedValue({ id: 'a1' })
    void opts.mutationFn({ agentRef: 'agent-delta', data: { instructions: 'New instructions' } })
    expect(updateAgentMock).toHaveBeenCalledWith('proj-1', 'agent-delta', { instructions: 'New instructions' })
  })

  it('invalidates ["agents"] on success', () => {
    useUpdateAgent()
    getLastMutationOptions().onSuccess()
    expect(invalidateQueriesMock).toHaveBeenCalledWith({ queryKey: ['agents'] })
  })

  it('shows success toast on success', () => {
    useUpdateAgent()
    getLastMutationOptions().onSuccess()
    expect(toastSuccessMock).toHaveBeenCalledWith('Agent updated')
  })

  it('shows error toast on failure', () => {
    useUpdateAgent()
    getLastMutationOptions().onError(new Error('NOT_FOUND'))
    expect(toastErrorMock).toHaveBeenCalledWith('NOT_FOUND')
  })
})

/* ── useArchiveAgent ────────────────────────────────────── */
describe('useArchiveAgent', () => {
  beforeEach(() => {
    useMutationMock.mockImplementation((options: unknown) => ({ mutate: vi.fn(), options }))
  })

  it('calls archiveAgent(projectId, agentRef) in mutationFn', () => {
    useArchiveAgent()
    const opts = getLastMutationOptions()
    archiveAgentMock.mockResolvedValue({ id: 'a1', status: 'archived' })
    void opts.mutationFn('agent-epsilon')
    expect(archiveAgentMock).toHaveBeenCalledWith('proj-1', 'agent-epsilon')
  })

  it('invalidates ["agents"] and ["agent-status"] on success', () => {
    useArchiveAgent()
    getLastMutationOptions().onSuccess()
    expect(invalidateQueriesMock).toHaveBeenCalledWith({ queryKey: ['agents'] })
    expect(invalidateQueriesMock).toHaveBeenCalledWith({ queryKey: ['agent-status'] })
  })

  it('shows success toast on success', () => {
    useArchiveAgent()
    getLastMutationOptions().onSuccess()
    expect(toastSuccessMock).toHaveBeenCalledWith('Agent archived')
  })

  it('shows error toast on failure', () => {
    useArchiveAgent()
    getLastMutationOptions().onError(new Error('ALREADY_ARCHIVED'))
    expect(toastErrorMock).toHaveBeenCalledWith('ALREADY_ARCHIVED')
  })
})

/* ── useUnarchiveAgent ──────────────────────────────────── */
describe('useUnarchiveAgent', () => {
  beforeEach(() => {
    useMutationMock.mockImplementation((options: unknown) => ({ mutate: vi.fn(), options }))
  })

  it('calls unarchiveAgent(projectId, agentRef) in mutationFn', () => {
    useUnarchiveAgent()
    const opts = getLastMutationOptions()
    unarchiveAgentMock.mockResolvedValue({ id: 'a1', status: 'active' })
    void opts.mutationFn('agent-zeta')
    expect(unarchiveAgentMock).toHaveBeenCalledWith('proj-1', 'agent-zeta')
  })

  it('invalidates ["agents"] and ["agent-status"] on success (mirroring useArchiveAgent)', () => {
    useUnarchiveAgent()
    getLastMutationOptions().onSuccess()
    expect(invalidateQueriesMock).toHaveBeenCalledWith({ queryKey: ['agents'] })
    expect(invalidateQueriesMock).toHaveBeenCalledWith({ queryKey: ['agent-status'] })
  })

  it('shows success toast on success', () => {
    useUnarchiveAgent()
    getLastMutationOptions().onSuccess()
    expect(toastSuccessMock).toHaveBeenCalledWith('Agent restored')
  })

  it('shows error toast on failure', () => {
    useUnarchiveAgent()
    getLastMutationOptions().onError(new Error('NOT_FOUND'))
    expect(toastErrorMock).toHaveBeenCalledWith('NOT_FOUND')
  })
})
