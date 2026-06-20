import { beforeEach, describe, expect, it, vi } from 'vitest'
import { useStartIssue } from './queries'

const useMutationMock = vi.fn()
const useQueryClientMock = vi.fn()
const useProjectMock = vi.fn()
const startIssueMock = vi.fn()
const toastSuccessMock = vi.fn()
const toastErrorMock = vi.fn()
const invalidateQueriesMock = vi.fn()

vi.mock('@tanstack/react-query', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@tanstack/react-query')>()),
  useMutation: (...args: unknown[]) => useMutationMock(...args),
  useQueryClient: () => useQueryClientMock(),
}))

vi.mock('../../project/@x/project-context', () => ({
  useProject: () => useProjectMock(),
}))

vi.mock('../../issue', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../issue')>()
  return {
    ...actual,
    startIssue: (...args: unknown[]) => startIssueMock(...args),
  }
})

vi.mock('sonner', () => ({
  toast: {
    success: (...args: unknown[]) => toastSuccessMock(...args),
    error: (...args: unknown[]) => toastErrorMock(...args),
  },
}))

beforeEach(() => {
  useMutationMock.mockReset()
  useQueryClientMock.mockReset()
  useProjectMock.mockReset()
  startIssueMock.mockReset()
  toastSuccessMock.mockReset()
  toastErrorMock.mockReset()
  invalidateQueriesMock.mockReset()
  useProjectMock.mockReturnValue({ projectId: 'proj-1' })
  useQueryClientMock.mockReturnValue({ invalidateQueries: invalidateQueriesMock })
  // Default: capture the options object passed to useMutation so callbacks can be invoked
  useMutationMock.mockImplementation((options: unknown) => ({ mutate: vi.fn(), options }))
})

function getLastMutationOptions(): { mutationFn: (n: number) => unknown; onSuccess: (...a: unknown[]) => void; onError: (...a: unknown[]) => void } {
  const calls = useMutationMock.mock.calls
  const last = calls[calls.length - 1][0] as { mutationFn: (n: number) => unknown; onSuccess: (...a: unknown[]) => void; onError: (...a: unknown[]) => void }
  return last
}

describe('useStartIssue', () => {
  it('calls useMutation with a mutationFn that invokes startIssue(number, projectId)', () => {
    useProjectMock.mockReturnValue({ projectId: 'proj-abc' })

    useStartIssue()

    const options = getLastMutationOptions()
    expect(typeof options.mutationFn).toBe('function')

    startIssueMock.mockResolvedValue({ issue: { number: 7 }, message: 'started' })
    void options.mutationFn(7)

    expect(startIssueMock).toHaveBeenCalledWith(7, 'proj-abc')
  })

  it('forwards the projectId resolved from useProject at call time', () => {
    useProjectMock.mockReturnValue({ projectId: 'proj-xyz' })

    useStartIssue()

    const options = getLastMutationOptions()
    startIssueMock.mockResolvedValue({ issue: { number: 11 }, message: 'started' })
    void options.mutationFn(11)

    expect(startIssueMock).toHaveBeenCalledWith(11, 'proj-xyz')
  })

  it('forwards a null/undefined projectId when useProject returns none', () => {
    useProjectMock.mockReturnValue({ projectId: null })

    useStartIssue()

    const options = getLastMutationOptions()
    startIssueMock.mockResolvedValue({ issue: { number: 1 }, message: 'started' })
    void options.mutationFn(1)

    expect(startIssueMock).toHaveBeenCalledWith(1, null)
  })

  it('invalidates both ["epics"] and ["issues"] query keys on success', () => {
    useStartIssue()

    const options = getLastMutationOptions()
    options.onSuccess()

    const invalidatedKeys = invalidateQueriesMock.mock.calls.map(call => call[0].queryKey)
    expect(invalidatedKeys).toContainEqual(['epics'])
    expect(invalidatedKeys).toContainEqual(['issues'])
    expect(invalidateQueriesMock).toHaveBeenCalledTimes(2)
  })

  it('toasts success on success', () => {
    useStartIssue()

    const options = getLastMutationOptions()
    options.onSuccess()

    expect(toastSuccessMock).toHaveBeenCalledTimes(1)
    expect(toastSuccessMock.mock.calls[0][0]).toBe('Issue started')
  })

  it('toasts the error message on failure', () => {
    useStartIssue()

    const options = getLastMutationOptions()
    const error = new Error('start refused')
    options.onError(error)

    expect(toastErrorMock).toHaveBeenCalledTimes(1)
    expect(toastErrorMock).toHaveBeenCalledWith('start refused')
  })

  it('falls back to "Request failed" when the error has no message', () => {
    useStartIssue()

    const options = getLastMutationOptions()
    options.onError(new Error(''))

    expect(toastErrorMock).toHaveBeenCalledWith('Request failed')
  })

  it('does NOT invalidate issue queries with a more specific key on success (only the prefix)', () => {
    useStartIssue()

    const options = getLastMutationOptions()
    options.onSuccess()

    const keys = invalidateQueriesMock.mock.calls.map(call => (call[0] as { queryKey: string[] }).queryKey)
    // Only ['epics'] and ['issues'] are invalidated — no extra ['epics', epicId] call
    expect(keys).toEqual([['epics'], ['issues']])
  })
})