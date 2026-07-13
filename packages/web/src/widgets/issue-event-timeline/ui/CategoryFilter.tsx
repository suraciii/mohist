import { CATEGORY_STYLES, type TimelineCategory } from '../model/types'

interface CategoryFilterProps {
  selected: Set<TimelineCategory>
  onToggle: (category: TimelineCategory) => void
  counts: Record<TimelineCategory, number>
}

const NEUTRAL_CHIP_ACTIVE = 'bg-foreground text-background border-foreground'
const NEUTRAL_CHIP_INACTIVE = 'border-border bg-background text-muted-foreground hover:bg-muted'

export function CategoryFilter({ selected, onToggle, counts }: CategoryFilterProps) {
  const categories = Object.keys(CATEGORY_STYLES) as TimelineCategory[]

  return (
    <div className="flex flex-wrap items-center gap-1.5" data-testid="category-filter">
      {categories.map((category) => {
        const active = selected.has(category)
        const style = CATEGORY_STYLES[category]
        const count = counts[category] ?? 0
        const chipClass = active ? NEUTRAL_CHIP_ACTIVE : NEUTRAL_CHIP_INACTIVE
        return (
          <button
            key={category}
            type="button"
            onClick={() => onToggle(category)}
            className={`inline-flex items-center gap-1 rounded-full border px-2.5 py-0.5 text-xs font-semibold transition-colors ${chipClass}`}
            data-testid={`category-filter-${category}`}
          >
            <span className={`inline-block h-1.5 w-1.5 rounded-full ${style.dot}`} />
            {style.label}
            <span className="tabular-nums text-[10px] opacity-80">{count}</span>
          </button>
        )
      })}
    </div>
  )
}
