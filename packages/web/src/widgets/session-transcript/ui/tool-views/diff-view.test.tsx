import '@testing-library/jest-dom'
import { fireEvent, render, screen } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import { DiffContentView } from './diff-view'

describe('DiffContentView', () => {
  it('uses buildDiffFromEdit for edit rawInput (oldString/newString shape)', () => {
    const rawInput = JSON.stringify({
      filePath: '/repo/src/foo.ts',
      oldString: 'a',
      newString: 'b',
    })

    render(<DiffContentView rawInput={rawInput} normalizedName="edit" />)

    expect(screen.getByText(/Changed files/)).toBeInTheDocument()
    expect(screen.getAllByText('foo.ts').length).toBeGreaterThan(0)
  })

  it('uses buildDiffFromPatchText for apply_patch rawInput (patch text)', () => {
    const rawInput = JSON.stringify({
      patchText: '*** Update File: src/edit.ts\n-old\n+new',
    })

    render(<DiffContentView rawInput={rawInput} normalizedName="apply_patch" />)

    expect(screen.getByText(/Changed files/)).toBeInTheDocument()
    expect(screen.getAllByText('src/edit.ts').length).toBeGreaterThan(0)
  })

  it('renders raw input / raw output when no diff can be built and no changed files', () => {
    render(
      <DiffContentView
        rawInput="plain input"
        rawOutput="plain output"
        normalizedName="edit"
      />,
    )

    expect(screen.getByText('Input')).toBeInTheDocument()
    expect(screen.getByText('Output')).toBeInTheDocument()
    expect(screen.getByText('plain input')).toBeInTheDocument()
    expect(screen.getByText('plain output')).toBeInTheDocument()
  })

  it('lists changed files supplied by the caller (DisplayChangedFile shape)', () => {
    const changedFiles = [
      { path: 'src/foo.ts', operation: 'modified' as const, additions: 3, deletions: 1 },
      { path: 'src/bar.ts', operation: 'created' as const, additions: 5, deletions: 0 },
    ]

    render(<DiffContentView changedFiles={changedFiles} normalizedName="edit" />)

    expect(screen.getByText(/Changed files \(2\)/)).toBeInTheDocument()
    expect(screen.getByText('src/foo.ts')).toBeInTheDocument()
    expect(screen.getByText('src/bar.ts')).toBeInTheDocument()
    expect(screen.getByText('+3')).toBeInTheDocument()
    expect(screen.getAllByText('+5')).toHaveLength(1)
    expect(screen.getByText('-1')).toBeInTheDocument()
  })

  it('toggles between diff view and raw view when Show raw is clicked', () => {
    const rawOutput = 'diff --git a/src/foo.ts b/src/foo.ts\n--- a/src/foo.ts\n+++ b/src/foo.ts\n@@ -1,1 +1,1 @@\n-a\n+b'

    render(<DiffContentView rawOutput={rawOutput} normalizedName="edit" />)

    fireEvent.click(screen.getByText('Show raw'))

    expect(screen.getByText('Raw output')).toBeInTheDocument()
  })
})
