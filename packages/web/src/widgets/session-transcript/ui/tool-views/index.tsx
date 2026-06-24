import { useState } from 'react'
import { Button } from '@/shared/ui/components/button'
import type { DisplayAssistantPart } from '../../model/session-transcript-display'
import { getDisplayType } from '../../model/transcript-tool-utils'
import { BashContentView } from './bash-view'
import { ReadContentView } from './read-view'
import { SearchContentView } from './search-view'
import { TodoContentView } from './todo-view'
import { DelegationContentView } from './delegation-view'
import { DiffContentView, PatchDiffView } from './diff-view'
import {
  ToolIcon,
  ToolStatusDot,
  getFallbackSubtitle,
  getRegistrySubtitle,
  getToolDisplayArgs,
  getToolDisplayLabel,
} from './shared'

export { BashContentView } from './bash-view'
export { ReadContentView } from './read-view'
export { SearchContentView } from './search-view'
export { TodoContentView } from './todo-view'
export { DelegationContentView } from './delegation-view'
export { DiffContentView, PatchDiffView } from './diff-view'
export { ToolIcon, ToolStatusDot, truncateOutput, getToolDisplayLabel, getToolDisplayArgs, getRegistrySubtitle, getFallbackSubtitle } from './shared'

interface ToolRowViewProps {
  part: Extract<DisplayAssistantPart, { partType: 'tool' }>
}

export function ToolRowView({ part }: ToolRowViewProps) {
  const [expanded, setExpanded] = useState(false)
  const isRunning = part.status === 'running' || part.status === 'pending'
  const toolLabel = getToolDisplayLabel(part.normalizedName, part.displayTitle, part.displaySubtitle, part.input)
  const toolArgs = getToolDisplayArgs(part.normalizedName, part.input)
  const registrySubtitleCandidate = !part.displayTitle && !part.displaySubtitle ? getRegistrySubtitle(part.normalizedName, part.input) : undefined
  const registrySubtitle = registrySubtitleCandidate && registrySubtitleCandidate !== toolLabel ? registrySubtitleCandidate : undefined
  const fallbackSubtitleCandidate = !registrySubtitle && !part.displayTitle && !part.displaySubtitle ? getFallbackSubtitle(part.input) : undefined
  const fallbackSubtitle = fallbackSubtitleCandidate && fallbackSubtitleCandidate !== toolLabel ? fallbackSubtitleCandidate : undefined
  const hasChangedFiles = part.changedFiles && part.changedFiles.length > 0
  const displayType = getDisplayType(part.normalizedName)
  const displayChangedFilesInline = hasChangedFiles && !(displayType === 'diff' && registrySubtitle)

  const showExpandableDetails = !isRunning && (part.input || part.output || part.error || hasChangedFiles)

  const renderSemanticContent = () => {
    if (part.error) {
      return (
        <div className="px-3 text-xs text-red-600 bg-red-50">
          {part.error}
        </div>
      )
    }

    if (displayType === 'terminal' && (part.input || part.output)) {
      return <BashContentView input={part.input} output={part.output} details={part.details} />
    }

    if ((part.normalizedName === 'read' || part.normalizedName === 'read_file') && (part.input || part.output)) {
      return (
        <>
          <ReadContentView input={part.input} output={part.output} />
          {part.input && (
            <div className="px-3 pb-2">
              <div className="font-medium text-xs text-gray-500 mb-1">Input</div>
              <pre data-scrollable="" className="whitespace-pre-wrap break-all text-xs text-gray-700 bg-gray-50 rounded p-2 max-h-24 overflow-auto">
                {part.input}
              </pre>
            </div>
          )}
        </>
      )
    }

    if ((part.normalizedName === 'grep' || part.normalizedName === 'search' || part.normalizedName === 'search_files') && (part.input || part.output)) {
      return <SearchContentView input={part.input} output={part.output} />
    }

    if ((part.normalizedName === 'todowrite' || part.normalizedName === 'todo') && part.input) {
      return <TodoContentView input={part.input} />
    }

    if (part.normalizedName === 'task' && part.details) {
      return <DelegationContentView input={part.input} details={part.details} />
    }

    if (displayType === 'diff') {
      return (
        <DiffContentView
          changedFiles={part.changedFiles}
          rawInput={part.rawInput}
          rawOutput={part.rawOutput}
          details={part.details}
          normalizedName={part.normalizedName}
        />
      )
    }

    return (
      <>
        {part.input && (
          <div className="px-3 pt-2">
            <div className="font-medium text-xs text-gray-500 mb-1">Input</div>
            <pre data-scrollable="" className="whitespace-pre-wrap break-all text-xs text-gray-700 bg-gray-50 rounded p-2 max-h-32 overflow-auto">
              {part.input}
            </pre>
          </div>
        )}
        {part.output && (
          <div className="px-3">
            <div className="font-medium text-xs text-gray-500 mb-1">Output</div>
            <pre data-scrollable="" className="whitespace-pre-wrap break-all text-xs text-gray-700 bg-gray-50 rounded p-2 max-h-32 overflow-auto">
              {part.output}
            </pre>
          </div>
        )}
      </>
    )
  }

  return (
    <div className={`rounded-md border overflow-hidden ${part.hasError ? 'border-red-200' : 'border-gray-200'}`}>
      <Button
        variant="ghost"
        size="sm"
        onClick={showExpandableDetails ? () => setExpanded(!expanded) : undefined}
        className={`flex h-auto items-center justify-start gap-2 w-full text-left px-3 py-1.5 rounded-none transition-colors ${showExpandableDetails ? 'hover:bg-gray-50 cursor-pointer' : 'cursor-default'}`}
      >
        <ToolStatusDot status={part.status} />
        <ToolIcon normalizedName={part.normalizedName} />
        <span className="text-xs font-medium text-gray-700">{toolLabel}</span>
        {toolArgs.length > 0 && !part.displayTitle && !part.displaySubtitle && (
          <span className="flex gap-1 shrink-0">
            {toolArgs.slice(0, 2).map((arg, i) => (
              <span key={i} className="inline-flex items-center px-1 py-0.5 rounded bg-gray-100 text-xs text-gray-500 font-mono">
                {arg}
              </span>
            ))}
          </span>
        )}
        {registrySubtitle && !part.displayTitle && !part.displaySubtitle && (
          <span className="text-xs text-gray-400 truncate max-w-[150px]">{registrySubtitle}</span>
        )}
        {fallbackSubtitle && !part.displayTitle && !part.displaySubtitle && !registrySubtitle && (
          <span className="text-xs text-gray-400 truncate max-w-[150px]">{fallbackSubtitle}</span>
        )}
        {part.hasError && (
          <span className="text-xs text-red-500">failed</span>
        )}
        {displayChangedFilesInline && (
          <span className="text-xs text-green-600">
            {part.changedFiles!.length === 1
              ? part.changedFiles![0].path.split('/').pop()
              : `${part.changedFiles!.length} files`}
          </span>
        )}
        {showExpandableDetails && (
          <svg className={`h-3 w-3 text-gray-400 shrink-0 ml-auto transition-transform ${expanded ? 'rotate-90' : ''}`} viewBox="0 0 20 20" fill="currentColor">
            <path fillRule="evenodd" d="M7.21 14.77a.75.75 0 01.02-1.06L11.168 10 7.23 6.29a.75.75 0 111.04-1.08l4.5 4.25a.75.75 0 010 1.08l-4.5 4.25a.75.75 0 01-1.06-.02z" clipRule="evenodd" />
          </svg>
        )}
      </Button>
      {expanded && showExpandableDetails && (
        <div className="border-t border-gray-100">
          {renderSemanticContent()}
          {displayChangedFilesInline && (
            <PatchDiffView changedFiles={part.changedFiles!} />
          )}
        </div>
      )}
    </div>
  )
}

interface ContextGroupViewProps {
  title: string
  tools: Extract<DisplayAssistantPart, { partType: 'tool' }>[]
  hasError: boolean
}

export function ContextGroupView({ title, tools, hasError }: ContextGroupViewProps) {
  const [expanded, setExpanded] = useState(false)
  const [titlePrefix, titleDetail] = title.split(' · ', 2)
  const singleContextTool = tools.length === 1 ? tools[0] : undefined
  const canExpandSingleContextTool = singleContextTool && singleContextTool.status !== 'running' && singleContextTool.status !== 'pending'
  const singleContextToolLabel = singleContextTool ? getToolDisplayLabel(singleContextTool.normalizedName, singleContextTool.displayTitle, singleContextTool.displaySubtitle, singleContextTool.input) : undefined
  const singleContextToolArgs = singleContextTool ? getToolDisplayArgs(singleContextTool.normalizedName, singleContextTool.input) : []

  return (
    <div className="rounded-md border border-gray-200 overflow-hidden">
      <Button
        variant="ghost"
        size="sm"
        onClick={() => setExpanded(!expanded)}
        className="flex h-auto items-center justify-start gap-2 w-full text-left px-3 py-1.5 rounded-none hover:bg-gray-50 transition-colors"
      >
        <svg className="h-3.5 w-3.5 text-gray-400 shrink-0" viewBox="0 0 20 20" fill="currentColor">
          <path d="M10 3a1.5 1.5 0 110 3 1.5 1.5 0 010-3zM7.5 4.5a1.5 1.5 0 110 3 1.5 1.5 0 010-3zm5 0a1.5 1.5 0 110 3 1.5 1.5 0 010-3z" />
        </svg>
        <span className="text-xs font-medium text-gray-700">{titlePrefix}</span>
        {titleDetail && (
          <span className="text-xs text-gray-500 truncate max-w-[240px]">{titleDetail}</span>
        )}
        {hasError && (
          <span className="text-xs text-red-500">failed</span>
        )}
        <svg className={`h-3 w-3 text-gray-400 shrink-0 ml-auto transition-transform ${expanded ? 'rotate-90' : ''}`} viewBox="0 0 20 20" fill="currentColor">
          <path fillRule="evenodd" d="M7.21 14.77a.75.75 0 01.02-1.06L11.168 10 7.23 6.29a.75.75 0 111.04-1.08l4.5 4.25a.75.75 0 010 1.08l-4.5 4.25a.75.75 0 01-1.06-.02z" clipRule="evenodd" />
        </svg>
      </Button>
      {expanded && (
        <div className="px-3 pb-2 border-t border-gray-100 space-y-1.5">
          {singleContextTool && canExpandSingleContextTool ? (
            <div className="px-3 py-2 text-xs text-gray-600">
              <div className="font-medium text-xs text-gray-500 mb-1">
                {singleContextTool.normalizedName === 'read' || singleContextTool.normalizedName === 'read_file' ? 'Reading' : singleContextToolLabel}
              </div>
              {singleContextToolArgs.length > 0 && (
                <div className="flex flex-wrap gap-1 mb-2">
                  {singleContextToolArgs.map((arg) => (
                    <span key={arg} className="rounded bg-gray-100 px-1 py-0.5 font-mono text-gray-500">{arg}</span>
                  ))}
                </div>
              )}
              {singleContextTool.output && (
                <pre data-scrollable="" className="whitespace-pre-wrap break-all text-xs text-gray-700 bg-gray-50 rounded p-2 max-h-24 overflow-auto">
                  {singleContextTool.output}
                </pre>
              )}
              {singleContextTool.input && (
                <div className="mt-2">
                  <div className="font-medium text-xs text-gray-500 mb-1">Input</div>
                  <pre data-scrollable="" className="whitespace-pre-wrap break-all text-xs text-gray-700 bg-gray-50 rounded p-2 max-h-24 overflow-auto">
                    {singleContextTool.input}
                  </pre>
                </div>
              )}
            </div>
          ) : (
            tools.map((tool) => (
              <ToolRowView key={tool.id} part={tool} />
            ))
          )}
        </div>
      )}
    </div>
  )
}
