import '@testing-library/jest-dom'
import { render, screen } from '../../../../tests/test-utils'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import {
  InboxSubscriptionSection,
  type InboxSubscriptionSectionDataHook,
} from './InboxSubscriptionSection'
import type { InboxSubscription } from '../../../entities/inbox'

const ALL_ENABLED: InboxSubscription = {
  workflow_failed: true,
  approval_requested: true,
  issue_started: true,
  issue_completed: true,
}

function renderSection({
  subscription = ALL_ENABLED,
  isLoading = false,
  updateSubscription = () => {},
}: {
  subscription?: InboxSubscription
  isLoading?: boolean
  updateSubscription?: (subscription: InboxSubscription) => void
} = {}) {
  const dataHook: InboxSubscriptionSectionDataHook = () => ({
    subscription,
    isLoading,
    updateSubscription,
  })
  return render(<InboxSubscriptionSection dataHook={dataHook} />)
}

describe('InboxSubscriptionSection', () => {
  it('renders exactly four toggles with product-facing labels', async () => {
    renderSection()

    expect(await screen.findByText('Workflow failed')).toBeInTheDocument()
    expect(screen.getByText('Approval requested')).toBeInTheDocument()
    expect(screen.getByText('Issue started')).toBeInTheDocument()
    expect(screen.getByText('Issue completed')).toBeInTheDocument()

    const toggles = screen.getAllByRole('switch')
    expect(toggles).toHaveLength(4)
  })

  it('does not display raw event or CloudEvent type names', async () => {
    renderSection()

    await screen.findByText('Workflow failed')

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

  it('shows all toggles as checked when subscription data has all enabled (no stored preferences)', async () => {
    renderSection()

    await screen.findByText('Workflow failed')

    const toggles = screen.getAllByRole('switch')
    for (const toggle of toggles) {
      expect(toggle).toBeChecked()
    }
  })

  it('shows toggles reflecting persisted state when some are disabled', async () => {
    renderSection({
      subscription: {
        workflow_failed: false,
        approval_requested: true,
        issue_started: false,
        issue_completed: true,
      },
    })

    await screen.findByText('Workflow failed')

    const toggles = screen.getAllByRole('switch')
    expect(toggles[0]).not.toBeChecked()
    expect(toggles[1]).toBeChecked()
    expect(toggles[2]).not.toBeChecked()
    expect(toggles[3]).toBeChecked()
  })

  it('calls update mutation with the full subscription when a toggle is changed', async () => {
    const user = userEvent.setup()
    const updateCaptures: InboxSubscription[] = []
    renderSection({ updateSubscription: (subscription) => updateCaptures.push(subscription) })

    await screen.findByText('Workflow failed')

    const toggles = screen.getAllByRole('switch')
    await user.click(toggles[0])

    expect(updateCaptures).toHaveLength(1)
    expect(updateCaptures[0]).toEqual({
      workflow_failed: false,
      approval_requested: true,
      issue_started: true,
      issue_completed: true,
    })
  })

  it('preserves earlier rapid toggle changes in later whole-object mutations', async () => {
    const user = userEvent.setup()
    const updateCaptures: InboxSubscription[] = []
    renderSection({ updateSubscription: (subscription) => updateCaptures.push(subscription) })

    await screen.findByText('Workflow failed')

    const toggles = screen.getAllByRole('switch')
    await user.click(toggles[0])
    await user.click(toggles[1])

    expect(updateCaptures).toHaveLength(2)
    expect(updateCaptures[1]).toEqual({
      workflow_failed: false,
      approval_requested: false,
      issue_started: true,
      issue_completed: true,
    })
  })

  it('shows loading state when subscription is loading', () => {
    renderSection({ isLoading: true })

    expect(screen.getByText('Loading subscription preferences...')).toBeInTheDocument()
    expect(screen.queryByRole('switch')).toBeNull()
  })

  it('each toggle has an accessible name satisfying aria-toggle-field-name', async () => {
    renderSection()

    await screen.findByText('Workflow failed')

    const toggles = screen.getAllByRole('switch')
    expect(toggles).toHaveLength(4)

    const labelTexts = toggles.map((t) => t.closest('label')?.textContent ?? '')
    expect(labelTexts).toContain('Workflow failed')
    expect(labelTexts).toContain('Approval requested')
    expect(labelTexts).toContain('Issue started')
    expect(labelTexts).toContain('Issue completed')
  })

  it('uses outcome-oriented settings copy', async () => {
    renderSection()

    expect(await screen.findByText('Inbox')).toBeInTheDocument()
    expect(screen.getByText('Workflow updates')).toBeInTheDocument()
    expect(screen.getByText('Choose which workflow updates create future inbox items.')).toBeInTheDocument()
  })
})
