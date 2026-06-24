import type { DiffLine, FileBlock } from '../../issue-changed-files/model/diffModel'
import { parsePatchOperations } from './transcript-tool-utils'

export type { DiffLine, FileBlock } from '../../issue-changed-files/model/diffModel'

export function buildDiffFromEdit(filePath: string, oldStr: string, newStr: string): FileBlock[] {
  const oldLines = oldStr.split('\n')
  const newLines = newStr.split('\n')

  const additions = newLines.filter(l => l.trim() !== '').length
  const deletions = oldLines.filter(l => l.trim() !== '').length

  const diffLines: DiffLine[] = []

  diffLines.push({ type: 'hunk', content: `--- a/${filePath}`, oldLine: undefined, newLine: undefined })
  diffLines.push({ type: 'hunk', content: `+++ b/${filePath}`, oldLine: undefined, newLine: undefined })
  diffLines.push({ type: 'hunk', content: `@@ -1,${oldLines.length} +1,${newLines.length} @@`, oldLine: 1, newLine: 1 })

  const maxLines = Math.max(oldLines.length, newLines.length)
  const contextBefore: string[] = []
  const contextAfter: string[] = []
  const addLines: string[] = []
  const delLines: string[] = []

  for (let i = 0; i < maxLines; i++) {
    const oldLine = oldLines[i]
    const newLine = newLines[i]

    if (oldLine !== undefined && newLine !== undefined && oldLine !== newLine) {
      if (contextBefore.length > 0) {
        for (const ctx of contextBefore) {
          diffLines.push({ type: 'context', content: ` ${ctx}`, oldLine: undefined, newLine: undefined })
        }
        contextBefore.length = 0
      }
      if (delLines.length > 0) {
        for (const dl of delLines) {
          diffLines.push({ type: 'del', content: `-${dl}`, oldLine: undefined, newLine: undefined })
        }
        delLines.length = 0
      }
      if (addLines.length > 0) {
        for (const al of addLines) {
          diffLines.push({ type: 'add', content: `+${al}`, oldLine: undefined, newLine: undefined })
        }
        addLines.length = 0
      }
      if (oldLine.trim() !== '') {
        diffLines.push({ type: 'del', content: `-${oldLine}`, oldLine: undefined, newLine: undefined })
      }
      if (newLine.trim() !== '') {
        diffLines.push({ type: 'add', content: `+${newLine}`, oldLine: undefined, newLine: undefined })
      }
    } else if (oldLine !== undefined && oldLine !== newLine) {
      if (contextBefore.length > 0 && delLines.length === 0 && addLines.length === 0) {
        contextBefore.push(oldLine)
      } else if (delLines.length > 0 || addLines.length > 0) {
        if (oldLine.trim() !== '') {
          delLines.push(oldLine)
        }
      } else {
        contextBefore.push(oldLine)
      }
      if (newLine !== undefined && newLine !== oldLine) {
        if (contextBefore.length > 0) {
          for (const ctx of contextBefore) {
            diffLines.push({ type: 'context', content: ` ${ctx}`, oldLine: undefined, newLine: undefined })
          }
          contextBefore.length = 0
        }
        if (newLine.trim() !== '') {
          addLines.push(newLine)
        }
      }
    } else if (oldLine !== undefined) {
      if (delLines.length > 0) {
        for (const dl of delLines) {
          diffLines.push({ type: 'del', content: `-${dl}`, oldLine: undefined, newLine: undefined })
        }
        delLines.length = 0
      }
      if (addLines.length > 0) {
        for (const al of addLines) {
          diffLines.push({ type: 'add', content: `+${al}`, oldLine: undefined, newLine: undefined })
        }
        addLines.length = 0
      }
      contextAfter.push(oldLine)
      if (contextAfter.length > 3) {
        const removed = contextAfter.shift()!
        diffLines.push({ type: 'context', content: ` ${removed}`, oldLine: undefined, newLine: undefined })
      }
    }
  }

  if (delLines.length > 0) {
    for (const dl of delLines) {
      diffLines.push({ type: 'del', content: `-${dl}`, oldLine: undefined, newLine: undefined })
    }
  }
  if (addLines.length > 0) {
    for (const al of addLines) {
      diffLines.push({ type: 'add', content: `+${al}`, oldLine: undefined, newLine: undefined })
    }
  }
  for (const ctx of contextBefore) {
    diffLines.push({ type: 'context', content: ` ${ctx}`, oldLine: undefined, newLine: undefined })
  }
  for (const ctx of contextAfter) {
    diffLines.push({ type: 'context', content: ` ${ctx}`, oldLine: undefined, newLine: undefined })
  }

  return [{
    oldPath: filePath,
    newPath: filePath,
    status: 'modified',
    isBinary: false,
    additions,
    deletions,
    hunks: [],
    lines: diffLines,
    changedLineCount: diffLines.length,
    hunkCount: 1,
  }]
}

export function buildDiffFromPatchText(patchText: string): FileBlock[] {
  const changes = parsePatchOperations(patchText)
  const blocks: FileBlock[] = []

  for (const change of changes) {
    const diffLines: DiffLine[] = []
    diffLines.push({ type: 'hunk', content: `--- a/${change.path}`, oldLine: undefined, newLine: undefined })
    diffLines.push({ type: 'hunk', content: `+++ b/${change.path}`, oldLine: undefined, newLine: undefined })

    const patchForFile = extractPatchForFile(patchText, change.path)
    if (patchForFile) {
      diffLines.push({ type: 'hunk', content: `@@ -1,${change.deletions ?? 0} +1,${change.additions ?? 0} @@`, oldLine: 1, newLine: 1 })

      const lines = patchForFile.split('\n')
      for (const line of lines) {
        if (line.startsWith('+') && !line.startsWith('+++')) {
          diffLines.push({ type: 'add', content: line, oldLine: undefined, newLine: undefined })
        } else if (line.startsWith('-') && !line.startsWith('---')) {
          diffLines.push({ type: 'del', content: line, oldLine: undefined, newLine: undefined })
        } else if (!line.startsWith('@@')) {
          diffLines.push({ type: 'context', content: ` ${line}`, oldLine: undefined, newLine: undefined })
        }
      }
    }

    const status = change.operation === 'created' ? 'added'
      : change.operation === 'deleted' ? 'deleted'
      : change.operation === 'moved' ? 'renamed'
      : 'modified'

    blocks.push({
      oldPath: change.oldPath ?? change.path,
      newPath: change.path,
      status,
      isBinary: false,
      additions: change.additions ?? 0,
      deletions: change.deletions ?? 0,
      hunks: [],
      lines: diffLines,
      changedLineCount: diffLines.length,
      hunkCount: 1,
    })
  }

  return blocks
}

export function extractPatchForFile(patchText: string, filePath: string): string | undefined {
  const lines = patchText.split('\n')
  let inFile = false
  const fileLines: string[] = []

  const escapedPath = filePath.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')
  const addRegex = new RegExp(`^\\*\\*\\* Add File:\\s*${escapedPath}$`)
  const updateRegex = new RegExp(`^\\*\\*\\* Update File:\\s*${escapedPath}$`)
  const deleteRegex = new RegExp(`^\\*\\*\\* Delete File:\\s*${escapedPath}$`)

  for (const line of lines) {
    const addMatch = line.match(addRegex)
    const updateMatch = line.match(updateRegex)
    const deleteMatch = line.match(deleteRegex)

    if (addMatch || updateMatch || deleteMatch) {
      inFile = true
      fileLines.length = 0
      continue
    }

    if (inFile) {
      if (line.startsWith('*** ') || line.startsWith('diff ') || line.startsWith('--- a/')) {
        if (fileLines.length > 0) {
          break
        }
      }
      if (line.match(/^\*\*\* (Add File|Update File|Delete File|Move to|OldPath):/)) {
        break
      }
      fileLines.push(line)
    }
  }

  return fileLines.length > 0 ? fileLines.join('\n') : undefined
}