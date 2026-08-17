import { useMemo, useState } from 'react'
import { BotIcon, ChevronDownIcon, SearchIcon } from 'lucide-react'
import type { AgentInfo } from '../../../entities/agent'
import { Badge } from '@/shared/ui/components/badge'
import { Button } from '@/shared/ui/components/button'
import { Input } from '@/shared/ui/components/input'
import { Popover, PopoverContent, PopoverTrigger } from '@/shared/ui/components/popover'
import { cn } from '@/shared/lib/utils'

export function AgentSelector({
  agents,
  selectedRef,
  onChange,
  isLoading,
}: {
  agents: AgentInfo[] | undefined
  selectedRef: string
  onChange: (ref: string) => void
  isLoading: boolean
}) {
  const [open, setOpen] = useState(false)
  const [search, setSearch] = useState('')
  const selectedAgent = agents?.find((a) => a.id === selectedRef) ?? null

  const filtered = useMemo(() => {
    if (!agents) return []
    if (!search.trim()) return agents
    const q = search.toLowerCase()
    return agents.filter((a) => a.name.toLowerCase().includes(q) || a.id.toLowerCase().includes(q))
  }, [agents, search])

  if (isLoading) {
    return (
      <Button variant="outline" className="w-full justify-between" disabled>
        <span className="text-muted-foreground">Loading agents...</span>
        <ChevronDownIcon className="size-4 text-muted-foreground" />
      </Button>
    )
  }

  return (
    <Popover open={open} onOpenChange={setOpen}>
      <PopoverTrigger
        render={
          <Button variant="outline" data-testid="agent-selector-trigger" className="w-full justify-between">
            {selectedAgent ? (
              <span className="truncate">{selectedAgent.name}</span>
            ) : (
              <span className="text-muted-foreground">New Agent for this task</span>
            )}
            <ChevronDownIcon className="size-4 shrink-0 text-muted-foreground" />
          </Button>
        }
      />
      <PopoverContent className="w-80 p-0" align="start">
        <div className="p-2">
          <div className="relative">
            <SearchIcon className="absolute left-2.5 top-1/2 size-4 -translate-y-1/2 text-muted-foreground" />
            <Input
              value={search}
              onChange={(e) => setSearch(e.target.value)}
              placeholder="Search agents..."
              className="pl-8 h-8 text-sm"
              data-testid="agent-search-input"
            />
          </div>
        </div>
        <div className="max-h-64 overflow-y-auto border-t">
          <div
            role="button"
            tabIndex={0}
            data-testid="agent-option-new-task"
            onClick={() => {
              onChange('')
              setOpen(false)
              setSearch('')
            }}
            onKeyDown={(e) => {
              if (e.key === 'Enter') {
                onChange('')
                setOpen(false)
                setSearch('')
              }
            }}
            className={cn(
              'flex items-center gap-2 px-3 py-2 cursor-pointer text-sm border-b',
              selectedRef === '' ? 'bg-muted' : 'hover:bg-muted',
            )}
          >
            <BotIcon className="size-4 shrink-0 text-muted-foreground" />
            <span className="font-medium text-foreground">New Agent for this task</span>
          </div>
          {filtered.length === 0 && (
            <div className="px-3 py-4 text-center text-sm text-muted-foreground">No agents found</div>
          )}
          {filtered.map((agent) => {
            const isSelected = agent.id === selectedRef
            const isArchived = agent.status === 'archived'
            return (
              <div
                key={agent.id}
                role="button"
                tabIndex={0}
                data-testid={`agent-option-${agent.id}`}
                data-agent-ref={agent.id}
                data-archived={isArchived ? 'true' : 'false'}
                onClick={() => {
                  onChange(agent.id)
                  setOpen(false)
                  setSearch('')
                }}
                onKeyDown={(e) => {
                  if (e.key === 'Enter') {
                    onChange(agent.id)
                    setOpen(false)
                    setSearch('')
                  }
                }}
                className={cn(
                  'flex items-center gap-2 px-3 py-2 cursor-pointer text-sm',
                  isSelected ? 'bg-muted' : 'hover:bg-muted',
                )}
              >
                <BotIcon className={cn('size-4 shrink-0', isArchived ? 'text-muted-foreground' : 'text-blue-600')} />
                <span className="flex-1 truncate font-medium">{agent.name}</span>
                {isArchived && (
                  <Badge variant="outline" className="text-[10px] px-1 py-0 h-4 text-muted-foreground">
                    Archived
                  </Badge>
                )}
              </div>
            )
          })}
        </div>
      </PopoverContent>
    </Popover>
  )
}
