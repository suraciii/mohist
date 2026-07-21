import { describe, expect, it, vi } from 'vitest'
import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { MemoryRouter, useLocation, useNavigate } from 'react-router-dom'
import {
  issueDetailSectionFromHash,
  issueDetailSectionLocation,
  useIssueDetailSectionNavigation,
} from './useIssueDetailSectionNavigation'

function Probe({ workflow = true, artifacts = true, comments = true }) {
  const navigation = useIssueDetailSectionNavigation({ workflow, artifacts, comments })
  const location = useLocation()
  const navigate = useNavigate()

  return (
    <>
      <div data-testid="location">{location.pathname}{location.search}{location.hash}</div>
      <button type="button" onClick={() => navigate(navigation.links.workflow)}>Workflow</button>
      <button type="button" onClick={() => navigate(navigation.links.artifacts)}>Artifacts</button>
      <button type="button" onClick={() => navigation.onActivityOpenChange(true)}>Open activity</button>
      <button type="button" onClick={() => navigation.onActivityOpenChange(false)}>Close activity</button>
      <div id="workflow" />
      <div id="artifacts" />
      <div id="comments" />
    </>
  )
}

describe('issue detail section navigation', () => {
  it('accepts only the four canonical fragments', () => {
    expect(issueDetailSectionFromHash('#workflow')).toBe('workflow')
    expect(issueDetailSectionFromHash('#artifacts')).toBe('artifacts')
    expect(issueDetailSectionFromHash('#activity')).toBe('activity')
    expect(issueDetailSectionFromHash('#comments')).toBe('comments')
    expect(issueDetailSectionFromHash('#unknown')).toBeNull()
    expect(issueDetailSectionFromHash('')).toBeNull()
  })

  it('builds same-document destinations without dropping pathname or search', () => {
    expect(issueDetailSectionLocation(
      { pathname: '/project%20one/issues/42', search: '?from=activity' },
      'comments',
    )).toEqual({
      pathname: '/project%20one/issues/42',
      search: '?from=activity',
      hash: '#comments',
    })
  })

  it('reveals a target once it becomes ready and reacts to in-page hash changes', async () => {
    const scrollIntoView = vi.spyOn(Element.prototype, 'scrollIntoView')
    const { rerender } = render(
      <MemoryRouter initialEntries={['/project/issues/42?from=board#artifacts']}>
        <Probe artifacts={false} />
      </MemoryRouter>,
    )

    expect(scrollIntoView).not.toHaveBeenCalled()
    rerender(
      <MemoryRouter initialEntries={['/project/issues/42?from=board#artifacts']}>
        <Probe artifacts />
      </MemoryRouter>,
    )
    await waitFor(() => expect(scrollIntoView).toHaveBeenCalledTimes(1))
    expect(scrollIntoView.mock.instances[0]).toHaveAttribute('id', 'artifacts')

    fireEvent.click(screen.getByRole('button', { name: 'Workflow' }))
    await waitFor(() => expect(scrollIntoView).toHaveBeenCalledTimes(2))
    expect(scrollIntoView.mock.instances[1]).toHaveAttribute('id', 'workflow')
    expect(screen.getByTestId('location')).toHaveTextContent('/project/issues/42?from=board#workflow')
  })

  it('uses the hash as activity state and clears only that hash idempotently', () => {
    render(
      <MemoryRouter initialEntries={['/project/issues/42?from=board']}>
        <Probe />
      </MemoryRouter>,
    )

    fireEvent.click(screen.getByRole('button', { name: 'Open activity' }))
    expect(screen.getByTestId('location')).toHaveTextContent('/project/issues/42?from=board#activity')
    fireEvent.click(screen.getByRole('button', { name: 'Close activity' }))
    expect(screen.getByTestId('location')).toHaveTextContent('/project/issues/42?from=board')
    fireEvent.click(screen.getByRole('button', { name: 'Close activity' }))
    expect(screen.getByTestId('location')).toHaveTextContent('/project/issues/42?from=board')
  })

  it('does not scroll for activity, unknown fragments, or absent targets', () => {
    const scrollIntoView = vi.spyOn(Element.prototype, 'scrollIntoView')
    const { unmount } = render(
      <MemoryRouter initialEntries={['/project/issues/42#activity']}>
        <Probe />
      </MemoryRouter>,
    )
    expect(scrollIntoView).not.toHaveBeenCalled()
    unmount()

    render(
      <MemoryRouter initialEntries={['/project/issues/42#unknown']}>
        <Probe />
      </MemoryRouter>,
    )
    expect(scrollIntoView).not.toHaveBeenCalled()
  })
})
