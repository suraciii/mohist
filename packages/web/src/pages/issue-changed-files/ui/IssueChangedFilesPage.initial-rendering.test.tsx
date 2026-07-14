import { describe, expect, it } from 'vitest'
import { parseDiff, parseDiffFiles } from '../../../shared/lib/diff-model'
import {
  BAR_DIFF,
  changedFile,
  fireEvent,
  FOO_DIFF,
  makeLargeDiff,
  screen,
  useIssueChangedFilesPageFixture,
  waitFor,
  withFiles,
} from './IssueChangedFilesPage.fixture'

const { renderPage, state } = useIssueChangedFilesPageFixture()

describe('IssueChangedFilesPage', () => {
  describe('non-eager default rendering', () => {
    it('does not render every line of every changed file on initial load', async () => {
      const { container } = renderPage()
      await screen.findByText('Test Issue')
      expect(container.querySelectorAll('tbody tr').length).toBeLessThan(500)
    })

    it('shows summary prompt when no file is auto-selected', async () => {
      state.diffData = withFiles([
        changedFile('package-lock.json', 500, 200, makeLargeDiff('package-lock.json', 500)),
        changedFile('yarn.lock', 400, 150, makeLargeDiff('yarn.lock', 400)),
      ])
      renderPage()
      await screen.findByText('Test Issue')
      expect(screen.getByText('Select a file from the tree to read its diff')).toBeTruthy()
    })

    it('does not render all-files patch stream on initial load', async () => {
      const { container } = renderPage()
      await screen.findByText('Test Issue')
      expect(container.querySelectorAll('[class*="sticky"]').length).toBeLessThanOrEqual(1)
    })

    it('auto-selects first readable non-generated file by default', async () => {
      renderPage()
      await screen.findByText('Test Issue')
      expect(screen.getByText('foo.ts')).toBeTruthy()
      expect(screen.getAllByText(/\+import React/)).toBeTruthy()
    })

    it('does not select lockfile as default file', async () => {
      state.diffData = withFiles([
        changedFile('package-lock.json', 400, 100, makeLargeDiff('package-lock.json', 400)),
        changedFile('src/foo.ts', 4, 1, FOO_DIFF),
      ])
      renderPage()
      await screen.findByText('Test Issue')
      expect(screen.getAllByText(/\+import React/)).toBeTruthy()
    })

    it('keeps metadata-only binary files visible instead of showing the empty diff state', async () => {
      state.diffData = {
        ...withFiles([changedFile('image.png', 0, 0, '', true)]),
        summary: { filesChanged: 1, additions: 0, deletions: 0 },
      }
      renderPage()
      await screen.findByText('Test Issue')
      expect(screen.getByText('image.png')).toBeTruthy()
      expect(screen.queryByText('No file changes yet')).toBeNull()
      expect(screen.getByText('Select a file from the tree to read its diff')).toBeTruthy()
      expect(screen.getByText('1 file changed')).toBeTruthy()
    })
  })

  describe('no duplicate file headers', () => {
    it('renders only one file header for selected file in unified mode', async () => {
      renderPage()
      await screen.findByText('Test Issue')
      fireEvent.click(screen.getByText('foo.ts'))
      await waitFor(() => {
        expect(screen.getAllByText('foo.ts')).toHaveLength(1)
      })
    })

    it('renders only one file header for selected file in split mode', async () => {
      renderPage()
      await screen.findByText('Test Issue')
      fireEvent.click(screen.getByText('foo.ts'))
      fireEvent.click(screen.getByRole('button', { name: /split view/i }))
      await waitFor(() => {
        expect(screen.getAllByText('foo.ts')).toHaveLength(1)
      })
    })

    it('does not duplicate file header when switching modes', async () => {
      renderPage()
      await screen.findByText('Test Issue')
      fireEvent.click(screen.getByText('foo.ts'))
      await waitFor(() => {
        expect(screen.getAllByText('foo.ts')).toHaveLength(1)
      })
      fireEvent.click(screen.getByRole('button', { name: /split view/i }))
      await waitFor(() => {
        expect(screen.getAllByText('foo.ts')).toHaveLength(1)
      })
      fireEvent.click(screen.getByRole('button', { name: /unified view/i }))
      await waitFor(() => {
        expect(screen.getAllByText('foo.ts')).toHaveLength(1)
      })
    })

    it('file tree entry click selects file with single header', async () => {
      renderPage()
      await screen.findByText('Test Issue')
      fireEvent.click(screen.getByText('foo.ts'))
      await waitFor(() => {
        expect(screen.getAllByText('foo.ts')).toHaveLength(1)
      })
      fireEvent.click(screen.getByText('bar.ts'))
      await waitFor(() => {
        expect(screen.getAllByText('bar.ts')).toHaveLength(1)
      })
    })

    it('no duplicate headers when collapsing and expanding files', async () => {
      renderPage()
      await screen.findByText('Test Issue')
      fireEvent.click(screen.getByText('Expand all'))
      fireEvent.click(screen.getByText('Collapse all'))
      fireEvent.click(screen.getByText('src/'))
      fireEvent.click(screen.getByText('foo.ts'))
      await waitFor(() => {
        expect(screen.getAllByText('foo.ts')).toHaveLength(1)
      })
    })
  })

  describe('diff parser regressions', () => {
    it('finalizes every file block and does not duplicate hunk lines', () => {
      const blocks = parseDiff(`${FOO_DIFF}\n${BAR_DIFF}`)

      expect(blocks).toHaveLength(2)
      expect(blocks[0].changedLineCount).toBe(7)
      expect(blocks[0].hunkCount).toBe(2)
      expect(blocks[1].changedLineCount).toBe(2)
      expect(blocks[1].hunkCount).toBe(1)
      expect(blocks[0].lines.filter((line) => line.content === "+import React from 'react'")).toHaveLength(1)
      expect(blocks[0].lines.filter((line) => line.type === 'hunk')).toHaveLength(2)
    })

    it('keeps metadata-only binary files as file blocks', () => {
      const blocks = parseDiffFiles([changedFile('image.png', 0, 0, '', true)])

      expect(blocks).toHaveLength(1)
      expect(blocks[0].newPath).toBe('image.png')
      expect(blocks[0].isBinary).toBe(true)
      expect(blocks[0].hunkCount).toBe(0)
      expect(blocks[0].rawPatch).toBe('')
    })
  })
})
