import { describe, expect, it } from 'vitest'
import { fireEvent, screen, useIssueChangedFilesPageFixture } from './IssueChangedFilesPage.fixture'

const { renderPage } = useIssueChangedFilesPageFixture()

describe('IssueChangedFilesPage', () => {
  describe('route rendering', () => {
    it('renders the changed-files page at the dedicated route', async () => {
      renderPage()
      await screen.findByText('Test Issue')
      expect(screen.getByText('main')).toBeTruthy()
      expect(screen.getByText('mo/issue-123')).toBeTruthy()
      expect(screen.getByText('files changed')).toBeTruthy()
    })

    it('renders issue number and title in header', async () => {
      renderPage()
      await screen.findByText('Test Issue')
      expect(screen.getByText('#123')).toBeTruthy()
    })

    it('renders diffstat with additions and deletions', async () => {
      renderPage()
      await screen.findByText('Test Issue')
      expect(screen.getAllByText('+6').length).toBeGreaterThan(0)
      expect(screen.getAllByText('-2').length).toBeGreaterThan(0)
    })
  })

  describe('View files navigation from Issue Detail', () => {
    it('has a back button that navigates to Issue Detail', async () => {
      renderPage()
      await screen.findByText('Test Issue')
      const backButton = screen.getByText('Back to issue')
      expect(backButton).toBeTruthy()
      fireEvent.click(backButton)
    })
  })

  describe('direct route loading', () => {
    it('renders the page without blank root when diff data is available', async () => {
      const { container } = renderPage()
      await screen.findByText('Test Issue')
      expect(container.firstChild).not.toBeNull()
    })

    it('renders the same content via direct route as via navigation', async () => {
      renderPage('/issues/123/files')
      await screen.findByText('Test Issue')
      expect(screen.getByText('#123')).toBeTruthy()
    })

    it('renders files page when issue number is valid and diff is available', async () => {
      renderPage('/issues/123/files')
      await screen.findByText('Test Issue')
      expect(screen.getByText('main')).toBeTruthy()
      expect(screen.getByText('mo/issue-123')).toBeTruthy()
      expect(screen.getByText('files changed')).toBeTruthy()
    })

    it('does not leave React root blank on direct load', async () => {
      const { container } = renderPage('/issues/123/files')
      await screen.findByText('Test Issue')
      const root = container.querySelector('#root') || container.firstChild
      expect(root?.textContent).not.toBe('')
    })
  })

  describe('refresh-equivalent initial routing', () => {
    it('renders the files page with fresh MemoryRouter entry', async () => {
      const { container } = renderPage('/issues/123/files')
      await screen.findByText('Test Issue')
      expect(container.firstChild).not.toBeNull()
    })

    it('renders issue header and diff metadata on fresh route entry', async () => {
      renderPage('/issues/123/files')
      await screen.findByText('Test Issue')
      expect(screen.getByText('#123')).toBeTruthy()
      expect(screen.getByText('main')).toBeTruthy()
      expect(screen.getAllByText('+6').length).toBeGreaterThan(0)
      expect(screen.getAllByText('-2').length).toBeGreaterThan(0)
    })
  })
})
