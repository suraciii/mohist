// @vitest-environment jsdom
import '@testing-library/jest-dom'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { cleanup, render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { InboxSubscriptionSection } from './InboxSubscriptionSection'

const useInboxSubscriptionMock = vi.fn()
const useUpdateInboxSubscriptionMock = vi.fn()

vi.mock('../../../entities/inbox', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../../../entities/inbox')>()),
  useInboxSubscription: (...args: unknown[]) => useInboxSubscriptionMock(...args),
  useUpdateInboxSubscription: (...args: unknown[]) => useUpdateInboxSubscriptionMock(...args),
}))

function renderSection() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={queryClient}>
      <InboxSubscriptionSection />
    </QueryClientProvider>,
  )
}

beforeEach(() => {
  useInboxSubscriptionMock.mockReset()
  useUpdateInboxSubscriptionMock.mockReset()
})

afterEach(() => {
  cleanup()
  vi.clearAllMocks()
})

const ALL_ENABLED = {
  workflow_failed: true,
  approval_requested: true,
  issue_started: true,
  issue_completed: true,
}

describe('InboxSubscriptionSection', () => {
  it('renders exactly four toggles with product-facing labels', () => {
    useInboxSubscriptionMock.mockReturnValue({ data: ALL_ENABLED, isLoading: false })
    useUpdateInboxSubscriptionMock.mockReturnValue({ mutate: vi.fn() })

    renderSection()

    expect(screen.getByText('Workflow failed')).toBeInTheDocument()
    expect(screen.getByText('Approval requested')).toBeInTheDocument()
    expect(screen.getByText('Issue started')).toBeInTheDocument()
    expect(screen.getByText('Issue completed')).toBeInTheDocument()

    const toggles = screen.getAllByRole('switch')
    expect(toggles).toHaveLength(4)
  })

  it('does not display raw event or CloudEvent type names', () => {
    useInboxSubscriptionMock.mockReturnValue({ data: ALL_ENABLED, isLoading: false })
    useUpdateInboxSubscriptionMock.mockReturnValue({ mutate: vi.fn() })

    renderSection()

    for (const forbidden of [
      /workflow_failed/,
      /approval_requested/,
      /issue_started/,
      /issue_completed/,
      /NotificationKind/,
      /CloudEvent/i,
    ]) {
      expect(
        Array.from(document.body.querySelectorAll('*')).some(
          (el) => el.children.length === 0 && forbidden.test(el.textContent ?? ''),
        ),
        `forbidden copy matched: ${forbidden}`,
      ).toBe(false)
    }
  })

  it('shows all toggles as checked when subscription data has all enabled (no stored preferences)', () => {
    useInboxSubscriptionMock.mockReturnValue({ data: ALL_ENABLED, isLoading: false })
    useUpdateInboxSubscriptionMock.mockReturnValue({ mutate: vi.fn() })

    renderSection()

    const toggles = screen.getAllByRole('switch')
    for (const toggle of toggles) {
      expect(toggle).toBeChecked()
    }
  })

  it('shows toggles reflecting persisted state when some are disabled', () => {
    useInboxSubscriptionMock.mockReturnValue({
      data: {
        workflow_failed: false,
        approval_requested: true,
        issue_started: false,
        issue_completed: true,
      },
      isLoading: false,
    })
    useUpdateInboxSubscriptionMock.mockReturnValue({ mutate: vi.fn() })

    renderSection()

    const toggles = screen.getAllByRole('switch')
    expect(toggles[0]).not.toBeChecked()
    expect(toggles[1]).toBeChecked()
    expect(toggles[2]).not.toBeChecked()
    expect(toggles[3]).toBeChecked()
  })

  it('calls update mutation with the full subscription when a toggle is changed', async () => {
    const mutateMock = vi.fn()
    useInboxSubscriptionMock.mockReturnValue({ data: ALL_ENABLED, isLoading: false })
    useUpdateInboxSubscriptionMock.mockReturnValue({ mutate: mutateMock })

    const user = userEvent.setup()
    renderSection()

    const toggles = screen.getAllByRole('switch')
    await user.click(toggles[0])

    expect(mutateMock).toHaveBeenCalledTimes(1)
    expect(mutateMock).toHaveBeenCalledWith({
      workflow_failed: false,
      approval_requested: true,
      issue_started: true,
      issue_completed: true,
    })
  })

  it('preserves earlier rapid toggle changes in later whole-object mutations', async () => {
    const mutateMock = vi.fn()
    useInboxSubscriptionMock.mockReturnValue({ data: ALL_ENABLED, isLoading: false })
    useUpdateInboxSubscriptionMock.mockReturnValue({ mutate: mutateMock })

    const user = userEvent.setup()
    renderSection()

    const toggles = screen.getAllByRole('switch')
    await user.click(toggles[0])
    await user.click(toggles[1])

    expect(mutateMock).toHaveBeenCalledTimes(2)
    expect(mutateMock).toHaveBeenLastCalledWith({
      workflow_failed: false,
      approval_requested: false,
      issue_started: true,
      issue_completed: true,
    })
  })

  it('shows loading state when subscription is loading', () => {
    useInboxSubscriptionMock.mockReturnValue({ data: undefined, isLoading: true })
    useUpdateInboxSubscriptionMock.mockReturnValue({ mutate: vi.fn() })

    renderSection()

    expect(screen.getByText('Loading subscription preferences...')).toBeInTheDocument()
    expect(screen.queryByRole('switch')).toBeNull()
  })

  it('each toggle has an accessible name satisfying aria-toggle-field-name', () => {
    useInboxSubscriptionMock.mockReturnValue({ data: ALL_ENABLED, isLoading: false })
    useUpdateInboxSubscriptionMock.mockReturnValue({ mutate: vi.fn() })

    renderSection()

    const toggles = screen.getAllByRole('switch')
    expect(toggles).toHaveLength(4)

    // Each toggle is wrapped in a <Label> so its accessible name comes from
    // the label text content. The labels render product-facing kind names.
    const labelTexts = toggles.map((t) => t.closest('label')?.textContent ?? '')
    expect(labelTexts).toContain('Workflow failed')
    expect(labelTexts).toContain('Approval requested')
    expect(labelTexts).toContain('Issue started')
    expect(labelTexts).toContain('Issue completed')
  })

  it('uses outcome-oriented settings copy', () => {
    useInboxSubscriptionMock.mockReturnValue({ data: ALL_ENABLED, isLoading: false })
    useUpdateInboxSubscriptionMock.mockReturnValue({ mutate: vi.fn() })

    renderSection()

    expect(screen.getByText('Inbox recording')).toBeInTheDocument()
    expect(screen.getByText('Workflow updates')).toBeInTheDocument()
    expect(screen.getByText('Choose which workflow updates create future inbox items.')).toBeInTheDocument()
  })
})
