import { describe, expect, it } from 'vitest'
import {
  changedFile,
  fireEvent,
  FOO_DIFF,
  makeLargeDiff,
  SAMPLE_DIFF_DATA,
  SAMPLE_ISSUE,
  screen,
  selectOption,
  useIssueChangedFilesPageFixture,
  waitFor,
  withFiles,
} from './IssueChangedFilesPage.fixture'

const { createPageWithoutLocationProbe, renderPage, state } = useIssueChangedFilesPageFixture()

function setLargeDiff(path = 'src/large.txt', lineCount = 350) {
  state.diffData = withFiles([changedFile(path, Math.ceil(lineCount / 2), Math.floor(lineCount / 2), makeLargeDiff(path, lineCount))])
}

describe('IssueChangedFilesPage', () => {
  describe('lockfile and generated large-diff collapse', () => {
    it('shows collapsed placeholder for lockfile by default', async () => {
      state.diffData = withFiles([
        changedFile('package-lock.json', 200, 200, makeLargeDiff('package-lock.json', 400)),
        changedFile('src/foo.ts', 4, 1, FOO_DIFF),
      ])
      renderPage()
      await screen.findByText('Test Issue')
      fireEvent.click(screen.getByText('package-lock.json'))
      await waitFor(() => {
        expect(screen.getByText(/Lockfile/)).toBeTruthy()
        expect(screen.getByText(/Render anyway/)).toBeTruthy()
      })
    })

    it('shows collapsed placeholder for generated file by default', async () => {
      setLargeDiff('dist/bundle.js', 400)
      renderPage()
      await screen.findByText('Test Issue')
      fireEvent.click(screen.getByText('bundle.js'))
      await waitFor(() => {
        expect(screen.getByText(/Generated file/)).toBeTruthy()
        expect(screen.getByText(/Render anyway/)).toBeTruthy()
      })
    })

    it('shows changed-line count in collapsed placeholder', async () => {
      setLargeDiff()
      renderPage()
      await screen.findByText('Test Issue')
      fireEvent.click(screen.getByText('large.txt'))
      await waitFor(() => {
        expect(screen.getByText(/350 lines changed/)).toBeTruthy()
      })
    })

    it('renders lockfile content when Render anyway is clicked', async () => {
      setLargeDiff('package-lock.json', 200)
      renderPage()
      await screen.findByText('Test Issue')
      fireEvent.click(screen.getByText('package-lock.json'))
      await waitFor(() => {
        expect(screen.getByText(/Lockfile/)).toBeTruthy()
      })
      fireEvent.click(screen.getByText('Render anyway'))
      await waitFor(() => {
        expect(screen.queryByText(/Lockfile/)).toBeNull()
      })
    })

    it('keeps non-selected files collapsed after Render anyway', async () => {
      state.diffData = withFiles([
        changedFile('package-lock.json', 200, 200, makeLargeDiff('package-lock.json', 200)),
        changedFile('yarn.lock', 150, 150, makeLargeDiff('yarn.lock', 150)),
      ])
      renderPage()
      await screen.findByText('Test Issue')
      fireEvent.click(screen.getByText('package-lock.json'))
      await waitFor(() => {
        expect(screen.getByText(/Lockfile/)).toBeTruthy()
      })
      fireEvent.click(screen.getByText('Render anyway'))
      await waitFor(() => {
        expect(screen.queryByText(/Lockfile/)).toBeNull()
      })
      fireEvent.click(screen.getByText('yarn.lock'))
      await waitFor(() => {
        expect(screen.getByText(/Lockfile/)).toBeTruthy()
      })
    })

    it('resets Render anyway state when navigating to a different issue', async () => {
      setLargeDiff('package-lock.json', 200)
      const { rerender } = renderPage('/issues/123/files')
      await screen.findByText('Test Issue')
      fireEvent.click(screen.getByText('package-lock.json'))
      await waitFor(() => {
        expect(screen.getByText(/Lockfile/)).toBeTruthy()
      })
      fireEvent.click(screen.getByText('Render anyway'))
      await waitFor(() => {
        expect(screen.queryByText(/Lockfile/)).toBeNull()
      })

      state.issueData = { ...SAMPLE_ISSUE, number: 124, title: 'Another Issue' }
      state.diffData = {
        ...SAMPLE_DIFF_DATA,
        head: 'mo/issue-124',
        files: [changedFile('package-lock.json', 100, 100, makeLargeDiff('package-lock.json', 200))],
      }
      rerender(createPageWithoutLocationProbe('/issues/124/files'))

      await waitFor(() => {
        expect(screen.getByText('Another Issue')).toBeTruthy()
        expect(screen.getByText(/Lockfile/)).toBeTruthy()
        expect(screen.getByText(/Render anyway/)).toBeTruthy()
      })
    })

    it('applies large-diff collapse in split view mode', async () => {
      setLargeDiff()
      renderPage()
      await screen.findByText('Test Issue')
      fireEvent.click(screen.getByText('large.txt'))
      fireEvent.click(screen.getByRole('button', { name: /split view/i }))
      await waitFor(() => {
        expect(screen.getByText(/Large diff/)).toBeTruthy()
        expect(screen.getByText(/Render anyway/)).toBeTruthy()
      })
    })

    it('applies large-diff collapse in raw patch mode', async () => {
      setLargeDiff()
      renderPage()
      await screen.findByText('Test Issue')
      fireEvent.click(screen.getByText('large.txt'))
      await selectOption('Reader mode', 'Raw')
      await waitFor(() => {
        expect(screen.getByText(/Large diff/)).toBeTruthy()
        expect(screen.getByText(/Render anyway/)).toBeTruthy()
      })
    })

    it('applies large-diff collapse in full file mode', async () => {
      setLargeDiff()
      renderPage()
      await screen.findByText('Test Issue')
      fireEvent.click(screen.getByText('large.txt'))
      await selectOption('Reader mode', 'Full file')
      await waitFor(() => {
        expect(screen.getByText(/Large diff/)).toBeTruthy()
        expect(screen.getByText(/Render anyway/)).toBeTruthy()
      })
      expect(state.fileContentHandler).not.toHaveBeenCalled()
    })

    it('applies large-diff collapse in search mode', async () => {
      setLargeDiff()
      renderPage()
      await screen.findByText('Test Issue')
      fireEvent.click(screen.getByText('large.txt'))
      await selectOption('Reader mode', 'Search')
      await waitFor(() => {
        expect(screen.getByText(/Large diff/)).toBeTruthy()
        expect(screen.getByText(/Render anyway/)).toBeTruthy()
      })
    })
  })
})
