import React from 'react'
import type { FileChangeSummary } from '../../../entities/coder-session'
import {
  parseJsonSafely,
  getToolLabel,
  getToolArgs,
  parsePatchOperations,
  parseEditInput,
  parseEditWriteChanges,
  getFallbackSubtitle,
  GENERIC_TOOL_LABEL,
  normalizeToolName,
} from '../model/transcript-tool-utils'

export type ToolCategory = 'context' | 'file-change' | 'execution' | 'question' | 'network' | 'fallback'

export interface ToolRegistryEntry {
  category: ToolCategory
  getTitle: (toolName: string, rawInput?: string) => string
  getSubtitle: (toolName: string, rawInput?: string) => string | undefined
  getBadges: (toolName: string, rawInput?: string) => string[]
  icon: React.ReactElement
}

function basename(path: string): string {
  return path.split('/').pop() ?? path
}

const FallbackEntry: ToolRegistryEntry = {
  category: 'fallback',
  getTitle: (toolName: string, rawInput?: string) => {
    const label = getToolLabel(toolName, rawInput)
    if (label) return label
    if (normalizeToolName(toolName) === 'unknown') return GENERIC_TOOL_LABEL
    return toolName
  },
  getSubtitle: (_toolName: string, rawInput?: string) => {
    return getFallbackSubtitle(rawInput)
  },
  getBadges: (toolName: string, rawInput?: string) => {
    return getToolArgs(toolName, rawInput)
  },
  icon: (
    <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
      <rect x="3" y="3" width="18" height="18" rx="2" />
      <path d="M12 8v8M8 12h8" />
    </svg>
  ),
}

const BashEntry: ToolRegistryEntry = {
  category: 'execution',
  getTitle: (_toolName, rawInput) => {
    const parsed = parseJsonSafely(rawInput)
    if (!parsed) return 'bash'
    const cmd = parsed.command ?? parsed.script ?? parsed.cmd
    if (typeof cmd === 'string') return cmd
    return 'bash'
  },
  getSubtitle: () => undefined,
  getBadges: (toolName, rawInput) => {
    return getToolArgs(toolName, rawInput)
  },
  icon: (
    <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
      <polyline points="4 17 10 11 4 5" />
      <line x1="12" y1="19" x2="20" y2="19" />
    </svg>
  ),
}

const ReadEntry: ToolRegistryEntry = {
  category: 'context',
  getTitle: (_toolName, rawInput) => {
    const label = getToolLabel('read', rawInput)
    return label ?? 'read'
  },
  getSubtitle: () => undefined,
  getBadges: (toolName, rawInput) => getToolArgs(toolName, rawInput),
  icon: (
    <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
      <path d="M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z" />
      <polyline points="14 2 14 8 20 8" />
      <line x1="16" y1="13" x2="8" y2="13" />
      <line x1="16" y1="17" x2="8" y2="17" />
      <polyline points="10 9 9 9 8 9" />
    </svg>
  ),
}

const GrepEntry: ToolRegistryEntry = {
  category: 'context',
  getTitle: (_toolName, rawInput) => {
    const label = getToolLabel('grep', rawInput)
    return label ?? 'grep'
  },
  getSubtitle: () => undefined,
  getBadges: (toolName, rawInput) => getToolArgs(toolName, rawInput),
  icon: (
    <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
      <circle cx="11" cy="11" r="8" />
      <path d="m21 21-4.35-4.35" />
      <path d="M8 8h6" />
    </svg>
  ),
}

const SearchEntry: ToolRegistryEntry = {
  category: 'context',
  getTitle: (_toolName, rawInput) => {
    const label = getToolLabel('search', rawInput)
    return label ?? 'search'
  },
  getSubtitle: () => undefined,
  getBadges: (toolName, rawInput) => getToolArgs(toolName, rawInput),
  icon: GrepEntry.icon,
}

const GlobEntry: ToolRegistryEntry = {
  category: 'context',
  getTitle: (_toolName, rawInput) => {
    const label = getToolLabel('glob', rawInput)
    return label ?? 'glob'
  },
  getSubtitle: () => undefined,
  getBadges: (toolName, rawInput) => getToolArgs(toolName, rawInput),
  icon: (
    <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
      <path d="M22 19a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h5l2 3h9a2 2 0 0 1 2 2z" />
      <path d="M2 10h20" />
    </svg>
  ),
}

const ListEntry: ToolRegistryEntry = {
  category: 'context',
  getTitle: (_toolName, rawInput) => {
    const label = getToolLabel('list', rawInput)
    return label ?? 'list'
  },
  getSubtitle: () => undefined,
  getBadges: (toolName, rawInput) => getToolArgs(toolName, rawInput),
  icon: GlobEntry.icon,
}

const WebfetchEntry: ToolRegistryEntry = {
  category: 'network',
  getTitle: (_toolName, rawInput) => {
    const label = getToolLabel('webfetch', rawInput)
    return label ?? 'webfetch'
  },
  getSubtitle: () => undefined,
  getBadges: (toolName, rawInput) => getToolArgs(toolName, rawInput),
  icon: (
    <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
      <circle cx="12" cy="12" r="10" />
      <path d="M2 12h20M12 2a15.3 15.3 0 0 1 4 10 15.3 15.3 0 0 1-4 10 15.3 15.3 0 0 1-4-10 15.3 15.3 0 0 1 4-10z" />
    </svg>
  ),
}

const WebsearchEntry: ToolRegistryEntry = {
  category: 'network',
  getTitle: (_toolName, rawInput) => {
    const label = getToolLabel('websearch', rawInput)
    return label ?? getToolLabel('search', rawInput) ?? 'websearch'
  },
  getSubtitle: () => undefined,
  getBadges: (toolName, rawInput) => getToolArgs(toolName, rawInput),
  icon: WebfetchEntry.icon,
}

const QuestionEntry: ToolRegistryEntry = {
  category: 'question',
  getTitle: (_toolName, rawInput) => {
    const label = getToolLabel('question', rawInput)
    return label ?? 'question'
  },
  getSubtitle: () => undefined,
  getBadges: () => [],
  icon: (
    <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
      <circle cx="12" cy="12" r="10" />
      <path d="M9.09 9a3 3 0 0 1 5.83 1c0 2-3 3-3 3" />
      <line x1="12" y1="17" x2="12.01" y2="17" />
    </svg>
  ),
}

const TodoEntry: ToolRegistryEntry = {
  category: 'execution',
  getTitle: (_toolName, rawInput) => {
    const label = getToolLabel('todowrite', rawInput)
    return label ?? 'Update todo list'
  },
  getSubtitle: () => undefined,
  getBadges: (toolName, rawInput) => getToolArgs(toolName, rawInput),
  icon: (
    <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
      <path d="M9 11l3 3L22 4" />
      <path d="M21 12v7a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h11" />
    </svg>
  ),
}

const TaskEntry: ToolRegistryEntry = {
  category: 'execution',
  getTitle: (_toolName, rawInput) => {
    const label = getToolLabel('task', rawInput)
    return label ?? 'task'
  },
  getSubtitle: () => undefined,
  getBadges: (toolName, rawInput) => getToolArgs(toolName, rawInput),
  icon: (
    <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
      <rect x="3" y="3" width="18" height="18" rx="2" />
      <path d="M9 12h6M12 9v6" />
    </svg>
  ),
}

const SkillEntry: ToolRegistryEntry = {
  category: 'execution',
  getTitle: (_toolName, rawInput) => {
    const label = getToolLabel('skill', rawInput)
    return label ?? 'skill'
  },
  getSubtitle: () => undefined,
  getBadges: () => [],
  icon: (
    <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
      <polygon points="12 2 15.09 8.26 22 9.27 17 14.14 18.18 21.02 12 17.77 5.82 21.02 7 14.14 2 9.27 8.91 8.26 12 2" />
    </svg>
  ),
}

const ApplyPatchEntry: ToolRegistryEntry = {
  category: 'file-change',
  getTitle: (_toolName, rawInput) => {
    const parsed = parseJsonSafely(rawInput)
    if (!parsed) return 'apply_patch'
    const patchText = parsed.patchText ?? parsed.patch
    if (typeof patchText === 'string' && patchText.includes('*** ')) {
      return parsePatchOperations(patchText)[0]?.path ?? 'apply_patch'
    }
    const label = getToolLabel('apply_patch', rawInput)
    return label ?? 'apply_patch'
  },
  getSubtitle: (_toolName, rawInput) => {
    const parsed = parseJsonSafely(rawInput)
    if (!parsed) return undefined
    const patchText = parsed.patchText ?? parsed.patch
    if (typeof patchText === 'string' && patchText.includes('*** ')) {
      const changes = parsePatchOperations(patchText)
      if (changes.length > 0) {
        return changes.length === 1 ? '1 file changed' : `${changes.length} files changed`
      }
    }
    return undefined
  },
  getBadges: () => [],
  icon: (
    <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
      <path d="M4 4a2 2 0 012-2h4.586A2 2 0 0112 2.586L15.414 6A2 2 0 0116 7.414V16a2 2 0 01-2 2H6a2 2 0 01-2-2V4z" />
    </svg>
  ),
}

const EditEntry: ToolRegistryEntry = {
  category: 'file-change',
  getTitle: (_toolName, rawInput) => {
    const parsed = parseEditInput(rawInput)
    if (parsed && parsed.filePath) {
      return basename(parsed.filePath)
    }
    const label = getToolLabel('edit', rawInput)
    return label ?? 'edit'
  },
  getSubtitle: (_toolName, rawInput) => {
    const parsed = parseEditInput(rawInput)
    if (!parsed) return undefined
    const changes = parseEditWriteChanges(parsed)
    if (changes.length > 0) {
      return `${changes[0].operation}`
    }
    return undefined
  },
  getBadges: (toolName, rawInput) => getToolArgs(toolName, rawInput),
  icon: (
    <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
      <path d="M12 20h9" />
      <path d="M16.5 3.5a2.121 2.121 0 0 1 3 3L7 19l-4 1 1-4L16.5 3.5z" />
    </svg>
  ),
}

const WriteEntry: ToolRegistryEntry = {
  category: 'file-change',
  getTitle: (_toolName, rawInput) => {
    const parsed = parseEditInput(rawInput)
    if (parsed && parsed.filePath) {
      return basename(parsed.filePath)
    }
    const label = getToolLabel('write', rawInput)
    return label ?? 'write'
  },
  getSubtitle: (_toolName, rawInput) => {
    const parsed = parseEditInput(rawInput)
    if (!parsed) return undefined
    const changes = parseEditWriteChanges(parsed)
    if (changes.length > 0) {
      return `${changes[0].operation}`
    }
    return undefined
  },
  getBadges: (toolName, rawInput) => getToolArgs(toolName, rawInput),
  icon: (
    <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
      <path d="M12 20h9" />
      <path d="M16.5 3.5a2.121 2.121 0 0 1 3 3L7 19l-4 1 1-4L16.5 3.5z" />
    </svg>
  ),
}

export const TOOL_REGISTRY: Record<string, ToolRegistryEntry> = {
  bash: BashEntry,
  read: ReadEntry,
  search: SearchEntry,
  search_files: SearchEntry,
  grep: GrepEntry,
  glob: GlobEntry,
  list: ListEntry,
  webfetch: WebfetchEntry,
  websearch: WebsearchEntry,
  question: QuestionEntry,
  todo: TodoEntry,
  todowrite: TodoEntry,
  task: TaskEntry,
  skill: SkillEntry,
  apply_patch: ApplyPatchEntry,
  edit: EditEntry,
  write: WriteEntry,
}

export function getToolRegistryEntry(toolName: string): ToolRegistryEntry {
  return TOOL_REGISTRY[toolName.toLowerCase()] ?? FallbackEntry
}

export function getToolTitle(toolName: string, rawInput?: string): string {
  return getToolRegistryEntry(toolName).getTitle(toolName, rawInput)
}

export function getToolSubtitle(toolName: string, rawInput?: string): string | undefined {
  return getToolRegistryEntry(toolName).getSubtitle(toolName, rawInput)
}

export function getToolBadges(toolName: string, rawInput?: string): string[] {
  return getToolRegistryEntry(toolName).getBadges(toolName, rawInput)
}

export function getToolIcon(toolName: string): React.ReactElement {
  return getToolRegistryEntry(toolName).icon
}

export function getToolCategory(toolName: string): ToolCategory {
  return getToolRegistryEntry(toolName).category
}

export function parseToolChangedFiles(toolName: string, rawInput?: string): FileChangeSummary[] {
  const parsed = parseJsonSafely(rawInput)
  if (!parsed) return []

  const patchText = parsed.patchText ?? parsed.patch
  if (typeof patchText === 'string' && patchText.includes('*** ')) {
    return parsePatchOperations(patchText)
  }

  if (toolName === 'edit' || toolName === 'write') {
    const editInput = parseEditInput(rawInput)
    if (editInput) {
      return parseEditWriteChanges(editInput)
    }
  }

  return []
}
