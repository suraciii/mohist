import { describe, expect, it } from 'vitest'
import { buildDiffFromEdit, buildDiffFromPatchText, extractPatchForFile } from './diff-builder'

describe('buildDiffFromEdit', () => {
  it('produces a single modified FileBlock with hunks for an old/new edit', () => {
    const blocks = buildDiffFromEdit('src/foo.ts', 'line1\nline2', 'line1\nline2-changed')

    expect(blocks).toHaveLength(1)
    const block = blocks[0]
    expect(block.oldPath).toBe('src/foo.ts')
    expect(block.newPath).toBe('src/foo.ts')
    expect(block.status).toBe('modified')
    expect(block.isBinary).toBe(false)
    expect(block.hunks).toEqual([])
    expect(block.hunkCount).toBe(1)
    expect(block.additions).toBe(2)
    expect(block.deletions).toBe(2)

    expect(block.lines[0]).toEqual({ type: 'hunk', content: '--- a/src/foo.ts', oldLine: undefined, newLine: undefined })
    expect(block.lines[1]).toEqual({ type: 'hunk', content: '+++ b/src/foo.ts', oldLine: undefined, newLine: undefined })
    expect(block.lines[2]).toEqual({ type: 'hunk', content: '@@ -1,2 +1,2 @@', oldLine: 1, newLine: 1 })

    const lineTypes = block.lines.map(l => l.type)
    expect(lineTypes).toContain('add')
    expect(lineTypes).toContain('del')

    const delLine = block.lines.find(l => l.type === 'del' && l.content === '-line2')
    const addLine = block.lines.find(l => l.type === 'add' && l.content === '+line2-changed')
    expect(delLine).toBeDefined()
    expect(addLine).toBeDefined()

    expect(block.changedLineCount).toBe(block.lines.length)
  })

  it('reports zero additions/deletions when old and new strings are identical', () => {
    const blocks = buildDiffFromEdit('src/foo.ts', 'line1', 'line1')

    expect(blocks).toHaveLength(1)
    expect(blocks[0].additions).toBe(1)
    expect(blocks[0].deletions).toBe(1)
    expect(blocks[0].lines.filter(l => l.type === 'add' || l.type === 'del')).toHaveLength(0)
  })

  it('keeps file path symmetric on oldPath and newPath', () => {
    const blocks = buildDiffFromEdit('a/b/c.ts', 'x', 'y')
    expect(blocks[0].oldPath).toBe('a/b/c.ts')
    expect(blocks[0].newPath).toBe('a/b/c.ts')
  })
})

describe('extractPatchForFile', () => {
  it('returns undefined for an empty patch text', () => {
    expect(extractPatchForFile('', 'src/foo.ts')).toBeUndefined()
  })

  it('returns undefined when the target file is not present in the patch', () => {
    const patch = '*** Update File: src/foo.ts\n- old line\n+ new line'
    expect(extractPatchForFile(patch, 'src/missing.ts')).toBeUndefined()
  })

  it('selects only the lines belonging to the requested file across a multi-file patch', () => {
    const patch = [
      '*** Update File: src/a.ts',
      '-old-a',
      '+new-a',
      '*** Update File: src/b.ts',
      '-old-b',
      '+new-b',
      '*** Update File: src/c.ts',
      '-old-c',
      '+new-c',
    ].join('\n')

    const extracted = extractPatchForFile(patch, 'src/b.ts')

    expect(extracted).toBeDefined()
    expect(extracted).toBe('-old-b\n+new-b')
  })

  it('matches Add File and Delete File directives as file boundaries', () => {
    const patch = [
      '*** Add File: src/new.ts',
      '+created-line',
      '*** Delete File: src/old.ts',
      '-removed-line',
    ].join('\n')

    expect(extractPatchForFile(patch, 'src/new.ts')).toBe('+created-line')
    expect(extractPatchForFile(patch, 'src/old.ts')).toBe('-removed-line')
  })
})

describe('buildDiffFromPatchText', () => {
  it('returns one block per file operation in the patch', () => {
    const patch = [
      '*** Add File: src/new.ts',
      '+line1',
      '+line2',
      '*** Update File: src/edit.ts',
      '-old',
      '+new',
      '*** Delete File: src/old.ts',
      '-gone',
    ].join('\n')

    const blocks = buildDiffFromPatchText(patch)

    expect(blocks).toHaveLength(3)
    expect(blocks.map(b => b.newPath)).toEqual(['src/new.ts', 'src/edit.ts', 'src/old.ts'])
    expect(blocks.map(b => b.status)).toEqual(['added', 'modified', 'deleted'])
  })

  it('maps the moved operation to renamed status', () => {
    const patch = [
      '*** Update File: src/from.ts',
      '-a',
      '+b',
      '*** Move to: src/to.ts',
    ].join('\n')

    const blocks = buildDiffFromPatchText(patch)

    expect(blocks).toHaveLength(2)
    const moved = blocks.find(b => b.newPath === 'src/to.ts')!
    expect(moved).toBeDefined()
    expect(moved.status).toBe('renamed')
  })

  it('uses OldPath directive as the oldPath when present', () => {
    const patch = [
      '*** Update File: src/new.ts',
      'OldPath: src/old.ts',
      '-a',
      '+b',
    ].join('\n')

    const blocks = buildDiffFromPatchText(patch)

    expect(blocks).toHaveLength(1)
    expect(blocks[0].newPath).toBe('src/new.ts')
    expect(blocks[0].oldPath).toBe('src/old.ts')
  })

  it('attaches hunk header and per-file diff lines to each block', () => {
    const patch = [
      '*** Update File: src/edit.ts',
      '-old',
      '+new',
    ].join('\n')

    const blocks = buildDiffFromPatchText(patch)

    expect(blocks).toHaveLength(1)
    const block = blocks[0]
    expect(block.lines[0]).toEqual({ type: 'hunk', content: '--- a/src/edit.ts', oldLine: undefined, newLine: undefined })
    expect(block.lines[1]).toEqual({ type: 'hunk', content: '+++ b/src/edit.ts', oldLine: undefined, newLine: undefined })
    expect(block.lines[2]?.content).toMatch(/^@@ /)
    expect(block.lines.some(l => l.type === 'add' && l.content === '+new')).toBe(true)
    expect(block.lines.some(l => l.type === 'del' && l.content === '-old')).toBe(true)
  })

  it('uses the additions/deletions counts supplied by parsePatchOperations', () => {
    const patch = [
      '*** Update File: src/edit.ts',
      '-old1',
      '-old2',
      '+new1',
      '+new2',
      '+new3',
    ].join('\n')

    const blocks = buildDiffFromPatchText(patch)

    expect(blocks).toHaveLength(1)
    expect(blocks[0].additions).toBe(3)
    expect(blocks[0].deletions).toBe(2)
  })

  it('returns an empty array for a patch with no file operations', () => {
    expect(buildDiffFromPatchText('')).toEqual([])
    expect(buildDiffFromPatchText('just some unrelated text')).toEqual([])
  })
})