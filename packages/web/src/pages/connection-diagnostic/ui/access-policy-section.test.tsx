import '@testing-library/jest-dom'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { AccessPolicySection } from './access-policy-section'
import type { SlackMemberSearchEntry } from '../../../entities/agent-connection'

const DISCLOSURE = 'Invoking this Bot grants channel members the Agent authority.'

afterEach(cleanup)

function makeEntry(id: string, name: string | null = null): SlackMemberSearchEntry {
  return { slackUserId: id, displayName: name, avatarUrl: null }
}

const baseProps = {
  ownerSlackUserId: 'U_OWNER',
  anyoneDisclosure: DISCLOSURE,
  isSubmitting: false,
  errorMessage: null,
}

describe('AccessPolicySection', () => {
  it('renders the current owner_only policy and does not show the allowlist editor', () => {
    render(
      <AccessPolicySection
        {...baseProps}
        accessPolicy="owner_only"
        allowMembers={[]}
        onSubmit={vi.fn()}
        searchMembers={vi.fn()}
      />,
    )
    expect(screen.getByTestId('connection-access-policy-radio-owner_only')).toBeChecked()
    expect(screen.queryByTestId('connection-access-policy-allowlist')).not.toBeInTheDocument()
  })

  it('selects allowlist, searches members, and adds a stable id as a chip', async () => {
    const user = userEvent.setup()
    const searchMembers = vi.fn().mockResolvedValue([makeEntry('U_ALICE', 'Alice')])
    render(
      <AccessPolicySection
        {...baseProps}
        accessPolicy="allowlist"
        allowMembers={[]}
        onSubmit={vi.fn()}
        searchMembers={searchMembers}
      />,
    )

    await user.type(screen.getByTestId('connection-access-policy-member-search'), 'alice')
    await waitFor(() => expect(searchMembers).toHaveBeenCalledWith('alice'))

    await waitFor(() =>
      expect(screen.getByTestId('connection-access-policy-member-option')).toBeInTheDocument(),
    )
    await user.click(screen.getByTestId('connection-access-policy-member-option'))

    const chip = screen.getByTestId('connection-access-policy-chip')
    expect(chip).toHaveAttribute('data-slack-user-id', 'U_ALICE')
    expect(screen.getByTestId('connection-access-policy-chip-owner')).toHaveTextContent('U_OWNER')
  })

  it('removes a member chip but never the Owner chip', async () => {
    const user = userEvent.setup()
    render(
      <AccessPolicySection
        {...baseProps}
        accessPolicy="allowlist"
        allowMembers={['U_BOB']}
        onSubmit={vi.fn()}
        searchMembers={vi.fn()}
      />,
    )

    expect(screen.getByTestId('connection-access-policy-chip')).toHaveAttribute('data-slack-user-id', 'U_BOB')
    const removeButtons = screen.getAllByTestId('connection-access-policy-chip-remove')
    expect(removeButtons).toHaveLength(1)
    await user.click(removeButtons[0])
    expect(screen.queryByTestId('connection-access-policy-chip')).not.toBeInTheDocument()
    expect(screen.getByTestId('connection-access-policy-chip-owner')).toBeInTheDocument()
  })

  it('requires the Anyone confirmation checkbox before enabling submit', async () => {
    const user = userEvent.setup()
    render(
      <AccessPolicySection
        {...baseProps}
        accessPolicy="anyone"
        allowMembers={[]}
        onSubmit={vi.fn()}
        searchMembers={vi.fn()}
      />,
    )

    expect(screen.getByTestId('connection-access-policy-submit')).toBeDisabled()
    expect(screen.getByTestId('connection-access-policy-anyone-disclosure')).toHaveTextContent(DISCLOSURE)

    await user.click(screen.getByTestId('connection-access-policy-anyone-confirm'))
    expect(screen.getByTestId('connection-access-policy-submit')).not.toBeDisabled()
  })

  it('submits the policy and allowlist excluding the Owner', async () => {
    const user = userEvent.setup()
    const onSubmit = vi.fn()
    render(
      <AccessPolicySection
        {...baseProps}
        accessPolicy="allowlist"
        allowMembers={['U_BOB']}
        onSubmit={onSubmit}
        searchMembers={vi.fn()}
      />,
    )

    await user.click(screen.getByTestId('connection-access-policy-submit'))

    await waitFor(() => expect(onSubmit).toHaveBeenCalledTimes(1))
    expect(onSubmit).toHaveBeenCalledWith({
      accessPolicy: 'allowlist',
      allowMembers: ['U_BOB'],
    })
  })

  it('clears allowlist members when switching away from allowlist', async () => {
    const user = userEvent.setup()
    const onSubmit = vi.fn()
    render(
      <AccessPolicySection
        {...baseProps}
        accessPolicy="allowlist"
        allowMembers={['U_BOB']}
        onSubmit={onSubmit}
        searchMembers={vi.fn()}
      />,
    )

    await user.click(screen.getByTestId('connection-access-policy-radio-owner_only'))
    await user.click(screen.getByTestId('connection-access-policy-submit'))

    await waitFor(() => expect(onSubmit).toHaveBeenCalled())
    expect(onSubmit).toHaveBeenCalledWith({ accessPolicy: 'owner_only', allowMembers: [] })
  })
})
