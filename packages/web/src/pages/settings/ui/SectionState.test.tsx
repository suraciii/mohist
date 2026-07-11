import '@testing-library/jest-dom'
import { cleanup, render, screen } from '@testing-library/react'
import { afterEach, describe, expect, it } from 'vitest'
import { SectionState } from './SectionState'

describe('SectionState', () => {
  afterEach(() => {
    cleanup()
  })

  describe('additive API contract', () => {
    it('renders the empty variant unchanged when no action is supplied', () => {
      const { container } = render(
        <SectionState variant="empty" title="Items" />,
      )

      expect(screen.getByRole('heading', { name: 'Items', level: 3 })).toBeInTheDocument()
      expect(screen.getByText('Nothing here yet.')).toBeInTheDocument()
      expect(container.querySelector('[data-variant="empty"]')).toBeInTheDocument()
    })

    it('exposes a `no-project` variant with the dashed-box treatment', () => {
      const { container } = render(<SectionState variant="no-project" title="Repositories" />)

      const root = container.querySelector('[data-variant="no-project"]')
      expect(root).toBeInTheDocument()
      const dashed = root?.querySelector('.border-dashed')
      expect(dashed).toBeInTheDocument()
      expect(screen.getByRole('heading', { name: 'Repositories', level: 3 })).toBeInTheDocument()
    })

    it('renders an inline next-step action when `action` is provided on empty', () => {
      render(
        <SectionState
          variant="empty"
          title="Templates"
          description="No templates available."
          action={<button data-testid="inline-action">New Template</button>}
        />,
      )

      expect(screen.getByTestId('inline-action')).toBeInTheDocument()
    })

    it('renders an inline next-step action when `action` is provided on no-project', () => {
      render(
        <SectionState
          variant="no-project"
          title="Repositories"
          action={<button data-testid="no-project-action">Select project</button>}
        />,
      )

      expect(screen.getByTestId('no-project-action')).toBeInTheDocument()
    })

    it('still renders children next to `action` (back-compat)', () => {
      render(
        <SectionState
          variant="empty"
          title="Repositories"
          description="No repositories configured."
          action={<button data-testid="inline-action">Create Repository</button>}
        >
          <p data-testid="legacy-children">Legacy children content</p>
        </SectionState>,
      )

      expect(screen.getByTestId('inline-action')).toBeInTheDocument()
      expect(screen.getByTestId('legacy-children')).toBeInTheDocument()
    })

    it('does not render an action row when neither `action` nor `children` is provided', () => {
      const { container } = render(
        <SectionState variant="empty" title="Items" description="Nothing here yet." />,
      )

      const dashed = container.querySelector('.border-dashed')
      expect(dashed).toBeInTheDocument()
      expect(dashed?.querySelector('button')).toBeNull()
      expect(dashed?.querySelector('p:not(.text-muted-foreground)')).toBeNull()
    })

    it('forwards `data-testid` to the outer container', () => {
      const { container } = render(
        <SectionState
          variant="no-project"
          title="X"
          data-testid="section-state-no-project"
        />,
      )

      expect(container.querySelector('[data-testid="section-state-no-project"]')).toBeInTheDocument()
    })

    it('does not break the loading or error variants when `action` is provided', () => {
      const { rerender } = render(
        <SectionState
          variant="loading"
          title="Items"
          action={<button data-testid="loading-action">Action</button>}
        />,
      )

      expect(screen.getByRole('status')).toBeInTheDocument()
      expect(screen.queryByTestId('loading-action')).not.toBeInTheDocument()

      rerender(
        <SectionState
          variant="error"
          title="Items"
          message="Boom"
          action={<button data-testid="error-action">Action</button>}
        />,
      )

      expect(screen.getByText('Boom')).toBeInTheDocument()
      expect(screen.queryByTestId('error-action')).not.toBeInTheDocument()
    })
  })
})
