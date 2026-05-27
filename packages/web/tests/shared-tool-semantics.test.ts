import { describe, it, expect } from 'vitest'
import type { FileChangeSummary } from '../src/entities/coder-session'
import {
  getDisplayType,
  getToolLabel,
  getToolArgs,
  parsePatchOperations,
  parseEditInput,
  parseEditWriteChanges,
  getFallbackSubtitle,
  inferToolName,
  normalizeToolName,
  type ToolDisplayType,
  type EditInput,
} from '../src/widgets/session-transcript/model/transcript-tool-utils'
import { getToolRegistryEntry, getToolTitle, getToolBadges, getToolDisplayType } from '../src/widgets/session-transcript/ui/tool-registry'

interface ToolCallEntry {
  id: string
  toolName: string
  rawInput?: string
  rawOutput?: string
  result?: string
  args?: Record<string, unknown>
  state: 'pending' | 'started' | 'completed' | 'failed'
  startedAt: string
  completedAt?: string | null
  duration?: number
  error?: string
  changedFiles?: FileChangeSummary[]
}

function makeEntry(toolName: string, rawInput?: string, rawOutput?: string, state: ToolCallEntry['state'] = 'completed'): ToolCallEntry {
  return {
    id: 'entry-1',
    toolName,
    rawInput,
    rawOutput,
    state,
    startedAt: '2024-01-01T00:00:01Z',
    completedAt: state === 'completed' ? '2024-01-01T00:00:02Z' : null,
  }
}

describe('shared tool semantics: display type', () => {
  const terminalTools = ['bash']
  const diffTools = ['edit', 'write', 'apply_patch']

  for (const tool of terminalTools) {
    it(`${tool} is terminal via both paths`, () => {
      const entry = makeEntry(tool, '{}')
      const legacyType: ToolDisplayType = getDisplayType(entry.toolName)
      const registryType = getToolDisplayType(tool)
      expect(legacyType).toBe('terminal')
      expect(registryType).toBe('terminal')
      expect(legacyType).toBe(registryType)
    })
  }

  for (const tool of diffTools) {
    it(`${tool} is diff via both paths`, () => {
      const entry = makeEntry(tool, '{}')
      const legacyType: ToolDisplayType = getDisplayType(entry.toolName)
      const registryType = getToolDisplayType(tool)
      expect(legacyType).toBe('diff')
      expect(registryType).toBe('diff')
      expect(legacyType).toBe(registryType)
    })
  }

  it('unknown tool falls back to generic via both paths', () => {
    const legacyType: ToolDisplayType = getDisplayType('some_unknown_tool')
    const registryType = getToolDisplayType('some_unknown_tool')
    expect(legacyType).toBe('generic')
    expect(registryType).toBe('generic')
    expect(legacyType).toBe(registryType)
  })

  it('read and grep are summary via both paths', () => {
    const legacyRead: ToolDisplayType = getDisplayType('read')
    const registryRead = getToolDisplayType('read')
    expect(legacyRead).toBe('summary')
    expect(registryRead).toBe('summary')
    expect(legacyRead).toBe(registryRead)

    const legacyGrep: ToolDisplayType = getDisplayType('grep')
    const registryGrep = getToolDisplayType('grep')
    expect(legacyGrep).toBe('summary')
    expect(registryGrep).toBe('summary')
    expect(legacyGrep).toBe(registryGrep)
  })
})

describe('shared tool semantics: labels', () => {
  const cases: Array<{ tool: string; input: string; expectedLabel: string | undefined }> = [
    { tool: 'bash', input: JSON.stringify({ command: 'npm run build' }), expectedLabel: 'npm run build' },
    { tool: 'bash', input: JSON.stringify({ script: 'npm test' }), expectedLabel: 'npm test' },
    { tool: 'read', input: JSON.stringify({ filePath: 'src/index.ts' }), expectedLabel: 'src/index.ts' },
    { tool: 'read', input: JSON.stringify({ file_path: 'src/main.ts' }), expectedLabel: 'src/main.ts' },
    { tool: 'read', input: JSON.stringify({ path: 'src/app.ts' }), expectedLabel: 'src/app.ts' },
    { tool: 'grep', input: JSON.stringify({ query: 'TODO' }), expectedLabel: 'TODO' },
    { tool: 'grep', input: JSON.stringify({ pattern: 'FIXME' }), expectedLabel: 'FIXME' },
    { tool: 'grep', input: JSON.stringify({ search: 'BUG' }), expectedLabel: 'BUG' },
    { tool: 'webfetch', input: JSON.stringify({ url: 'https://example.com' }), expectedLabel: 'https://example.com' },
    { tool: 'webfetch', input: JSON.stringify({ uri: 'https://example.org' }), expectedLabel: 'https://example.org' },
    { tool: 'task', input: JSON.stringify({ description: 'Fix the bug' }), expectedLabel: 'Fix the bug' },
    { tool: 'skill', input: JSON.stringify({ name: 'debugging-code' }), expectedLabel: 'debugging-code' },
    { tool: 'edit', input: JSON.stringify({ filePath: 'src/utils.ts' }), expectedLabel: 'utils.ts' },
    { tool: 'write', input: JSON.stringify({ filePath: 'src/new.ts' }), expectedLabel: 'new.ts' },
    { tool: 'question', input: JSON.stringify({ question: 'How does this work?' }), expectedLabel: 'How does this work?' },
  ]

  for (const { tool, input, expectedLabel } of cases) {
    it(`${tool} label matches between legacy and registry paths`, () => {
      const legacyLabel = getToolLabel(tool, input)
      const registryTitle = getToolTitle(tool, input)
      expect(legacyLabel).toBe(expectedLabel)
      expect(registryTitle).toBe(expectedLabel)
    })
  }

  it('unknown tool uses toolName as fallback in registry', () => {
    const registryTitle = getToolTitle('foobar_tool', undefined)
    expect(registryTitle).toBe('foobar_tool')
  })
})

describe('shared tool semantics: badges/args', () => {
  const cases: Array<{ tool: string; input: string; expectedArgs: string[] }> = [
    { tool: 'bash', input: JSON.stringify({ command: 'ls', timeout: 30 }), expectedArgs: ['timeout:30'] },
    { tool: 'bash', input: JSON.stringify({ command: 'ls', cwd: '/home/user/project' }), expectedArgs: ['project'] },
    { tool: 'read', input: JSON.stringify({ filePath: 'src/index.ts', recursive: true }), expectedArgs: ['recursive'] },
    { tool: 'read', input: JSON.stringify({ filePath: 'src/index.ts', include: '*.ts' }), expectedArgs: ['*.ts'] },
    { tool: 'glob', input: JSON.stringify({ pattern: '**/*.ts', recursive: true }), expectedArgs: ['recursive'] },
    { tool: 'grep', input: JSON.stringify({ query: 'TODO', type: 'file' }), expectedArgs: ['file'] },
    { tool: 'grep', input: JSON.stringify({ query: 'TODO', scope: 'all' }), expectedArgs: ['all'] },
    { tool: 'webfetch', input: JSON.stringify({ url: 'https://example.com', method: 'GET' }), expectedArgs: ['GET'] },
    { tool: 'edit', input: JSON.stringify({ filePath: 'a.txt', oldString: 'foo' }), expectedArgs: ['edit'] },
    { tool: 'write', input: JSON.stringify({ filePath: 'b.txt', oldString: 'bar' }), expectedArgs: ['edit'] },
    { tool: 'task', input: JSON.stringify({ description: 'Do it', priority: 'high' }), expectedArgs: ['high'] },
  ]

  for (const { tool, input, expectedArgs } of cases) {
    it(`${tool} args/badges match between legacy and registry paths`, () => {
      const legacyArgs = getToolArgs(tool, input)
      const registryBadges = getToolBadges(tool, input)
      expect(legacyArgs).toEqual(expectedArgs)
      expect(registryBadges).toEqual(expectedArgs)
    })
  }
})

describe('shared tool semantics: patch/file-change parsing', () => {
  it('parsePatchOperations extracts correct file-change summaries', () => {
    const patch = `*** Update File: src/auth.ts
--- src/auth.ts
+++ src/auth.ts
@@ -1,3 +1,4 @@
 const x = 1
+const y = 2
 const z = 3`

    const changes: FileChangeSummary[] = parsePatchOperations(patch)
    expect(changes).toHaveLength(1)
    expect(changes[0].path).toBe('src/auth.ts')
    expect(changes[0].operation).toBe('modified')
    expect(changes[0].additions).toBe(1)
    expect(changes[0].deletions).toBe(0)
  })

  it('parsePatchOperations handles multiple files', () => {
    const patch = `*** Add File: src/new.ts
--- /dev/null
+++ src/new.ts
@@ -0,0 +1,3 @@
+const x = 1
*** Update File: src/existing.ts
--- src/existing.ts
+++ src/existing.ts
@@ -1,2 +1,3 @@
 const a = 1
+const b = 2`

    const changes: FileChangeSummary[] = parsePatchOperations(patch)
    expect(changes).toHaveLength(2)
    expect(changes[0].path).toBe('src/new.ts')
    expect(changes[0].operation).toBe('created')
    expect(changes[1].path).toBe('src/existing.ts')
    expect(changes[1].operation).toBe('modified')
  })

  it('parsePatchOperations handles delete operations', () => {
    const patch = `*** Delete File: src/removed.ts
--- src/removed.ts
+++ /dev/null
@@ -1,3 +0,0 @@
-const x = 1
-const y = 2
-const z = 3`

    const changes: FileChangeSummary[] = parsePatchOperations(patch)
    expect(changes).toHaveLength(1)
    expect(changes[0].path).toBe('src/removed.ts')
    expect(changes[0].operation).toBe('deleted')
    expect(changes[0].deletions).toBe(3)
  })

  it('parseEditInput parses edit tool input', () => {
    const input = JSON.stringify({
      filePath: 'src/utils.ts',
      oldString: 'const x = 1',
      newString: 'const x = 2',
    })
    const parsed: EditInput | null = parseEditInput(input)
    expect(parsed).not.toBeNull()
    expect(parsed!.filePath).toBe('src/utils.ts')
    expect(parsed!.oldString).toBe('const x = 1')
    expect(parsed!.newString).toBe('const x = 2')
  })

  it('parseEditInput parses write tool input (new file)', () => {
    const input = JSON.stringify({
      filePath: 'src/brand-new.ts',
      newString: 'const y = 1',
    })
    const parsed: EditInput | null = parseEditInput(input)
    expect(parsed).not.toBeNull()
    expect(parsed!.filePath).toBe('src/brand-new.ts')
    expect(parsed!.oldString).toBe('')
    expect(parsed!.newString).toBe('const y = 1')
  })

  it('parseEditWriteChanges produces correct summary for edit', () => {
    const parsed: EditInput = {
      filePath: 'src/utils.ts',
      oldString: 'const a = 1\nconst b = 2',
      newString: 'const a = 1\nconst b = 2\nconst c = 3',
    }
    const changes: FileChangeSummary[] = parseEditWriteChanges(parsed)
    expect(changes).toHaveLength(1)
    expect(changes[0].path).toBe('utils.ts')
    expect(changes[0].operation).toBe('modified')
    expect(changes[0].additions).toBe(3)
    expect(changes[0].deletions).toBe(2)
  })

  it('parseEditWriteChanges produces correct summary for new file', () => {
    const parsed: EditInput = {
      filePath: 'src/brand-new.ts',
      oldString: '',
      newString: 'const x = 1\nconst y = 2',
    }
    const changes: FileChangeSummary[] = parseEditWriteChanges(parsed)
    expect(changes).toHaveLength(1)
    expect(changes[0].path).toBe('brand-new.ts')
    expect(changes[0].operation).toBe('created')
    expect(changes[0].deletions).toBeUndefined()
  })
})

describe('shared tool semantics: fallback subtitle', () => {
  it('getFallbackSubtitle extracts description for unknown tool', () => {
    const input = JSON.stringify({ description: 'Does something useful' })
    const subtitle = getFallbackSubtitle(input)
    expect(subtitle).toBe('Does something useful')
  })

  it('getFallbackSubtitle extracts url for unknown tool with url field', () => {
    const input = JSON.stringify({ url: 'https://api.example.com/data' })
    const subtitle = getFallbackSubtitle(input)
    expect(subtitle).toBe('https://api.example.com/data')
  })

  it('getFallbackSubtitle extracts filepath for unknown tool', () => {
    const input = JSON.stringify({ filePath: '/some/path/to/file.txt' })
    const subtitle = getFallbackSubtitle(input)
    expect(subtitle).toBe('/some/path/to/file.txt')
  })

  it('getFallbackSubtitle returns undefined for invalid input', () => {
    expect(getFallbackSubtitle(undefined)).toBeUndefined()
    expect(getFallbackSubtitle('')).toBeUndefined()
    expect(getFallbackSubtitle('not-json')).toBeUndefined()
  })
})

describe('shared tool semantics: live/replay inference parity', () => {
  it('infers skill from semantic title', () => {
    expect(normalizeToolName('unknown', 'Loaded skill: software-design', {}, undefined)).toBe('skill')
  })

  it('infers task from delegation payload', () => {
    expect(normalizeToolName('unknown', 'delegate', { description: 'Inspect routes', subagent_type: 'explore', task_id: 'task-1' }, undefined)).toBe('task')
  })

  it('infers websearch from search URL payload', () => {
    expect(inferToolName('unknown', undefined, { url: 'https://example.com', search_query: 'session transcript parity' }, undefined)).toBe('websearch')
  })

  it('infers todo from todo-like title', () => {
    expect(normalizeToolName('unknown', 'Todo: sync tests', {}, undefined)).toBe('todo')
  })
})

describe('registry entry consistency', () => {
  it('registry entry getTitle uses shared getToolLabel for known tools', () => {
    const input = JSON.stringify({ filePath: 'src/index.ts' })
    const entry = getToolRegistryEntry('read')
    const title = entry.getTitle('read', input)
    expect(title).toBe('src/index.ts')
  })

  it('registry entry getBadges uses shared getToolArgs for known tools', () => {
    const input = JSON.stringify({ command: 'ls', timeout: 60 })
    const entry = getToolRegistryEntry('bash')
    const badges = entry.getBadges('bash', input)
    expect(badges).toContain('timeout:60')
  })

  it('registry applies_patch entry uses parsePatchOperations', () => {
    const patch = `*** Update File: src/main.ts
--- src/main.ts
+++ src/main.ts
@@ -1,2 +1,3 @@
 const x = 1
+const y = 2`
    const entry = getToolRegistryEntry('apply_patch')
    const title = entry.getTitle('apply_patch', JSON.stringify({ patchText: patch }))
    expect(title).toBe('src/main.ts')
  })
})
