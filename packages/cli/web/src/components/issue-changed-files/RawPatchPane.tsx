interface RawPatchPaneProps {
  rawPatch: string
}

export function RawPatchPane({ rawPatch }: RawPatchPaneProps) {
  if (!rawPatch) {
    return (
      <div className="flex items-center justify-center h-full text-gray-400 text-sm">
        No patch available for this file
      </div>
    )
  }

  return (
    <div className="flex flex-col h-full">
      <div className="sticky top-0 z-10 bg-gray-50 border-b border-gray-200 px-4 py-2 flex items-center justify-between text-xs">
        <span className="font-medium text-gray-700">Raw patch</span>
        <button
          onClick={() => navigator.clipboard.writeText(rawPatch)}
          className="px-2 py-1 text-xs bg-gray-100 hover:bg-gray-200 rounded border border-gray-200 transition-colors"
        >
          Copy
        </button>
      </div>
      <div className="flex-1 overflow-auto">
        <pre className="text-xs font-mono p-4 whitespace-pre-wrap break-all text-gray-700">
          {rawPatch}
        </pre>
      </div>
    </div>
  )
}