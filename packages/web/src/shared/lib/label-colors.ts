type LabelSize = 'sm' | 'md'

/**
 * Class-string label/priority/risk style.
 *
 * `className` is a Tailwind utility class string the consumer applies
 * directly — no inline hex literals. The previous `{ bg, text }` shape
 * forced every call site to build `style={{ backgroundColor, color }}`
 * inline, which is invisible to dark theme and inconsistent with the
 * rest of the Web UI.
 *
 * `size` keeps the existing chip-size distinction (sm vs md) so call
 * sites can choose chip dimensions without rebuilding the visual
 * treatment.
 */
export type LabelStyle = {
  className: string
  size: LabelSize
}

const SIZE_SM: LabelSize = 'sm'
const SIZE_MD: LabelSize = 'md'

/**
 * Type-label palette. Type is state-bearing (bug = danger, feature = success,
 * enhancement = info, tech-debt = muted, performance = warning), so it routes
 * through the same semantic families used by the status-presentation layer
 * (D6). The class strings are token-backed (`bg-<family>-subtle
 * text-<family> border-<family>-border`) and are dark-mode-aware by
 * construction.
 */
const TYPE_LABEL_COLORS: Record<string, LabelStyle> = {
  bug: { className: 'bg-danger-subtle text-danger border-danger-border', size: SIZE_MD },
  feature: { className: 'bg-success-subtle text-success border-success-border', size: SIZE_MD },
  enhancement: { className: 'bg-info-subtle text-info border-info-border', size: SIZE_MD },
  'tech-debt': { className: 'bg-muted text-muted-foreground border-border', size: SIZE_MD },
  performance: { className: 'bg-warning-subtle text-warning border-warning-border', size: SIZE_MD },
}

/**
 * Urgency-label palette. Categorical, not state-meaningful — kept on a
 * documented dark-aware class palette (per design D6: priority/area/urgency
 * stay off the semantic families to avoid overloading the meaning reservation).
 */
const URGENCY_LABEL_COLORS: Record<string, LabelStyle> = {
  critical: {
    className: 'bg-red-700 text-white border-red-700 dark:bg-red-500 dark:text-white dark:border-red-500',
    size: SIZE_MD,
  },
}

/**
 * Area-label palette. Categorical, not state-meaningful — same rationale as
 * urgency: areas are stream labels (agent, webui, api, ...), they don't
 * express production state and so don't route through `success`/`warning`/
 * `info`/`danger`. Documented dark-aware class palette.
 */
const AREA_LABEL_COLORS: Record<string, LabelStyle> = {
  agent: { className: 'bg-muted text-muted-foreground border-border dark:bg-muted/40', size: SIZE_SM },
  webui: { className: 'bg-muted text-muted-foreground border-border dark:bg-muted/40', size: SIZE_SM },
  api: { className: 'bg-muted text-muted-foreground border-border dark:bg-muted/40', size: SIZE_SM },
  frontend: { className: 'bg-muted text-muted-foreground border-border dark:bg-muted/40', size: SIZE_SM },
  logging: { className: 'bg-muted text-muted-foreground border-border dark:bg-muted/40', size: SIZE_SM },
  'data-model': { className: 'bg-muted text-muted-foreground border-border dark:bg-muted/40', size: SIZE_SM },
  recovery: { className: 'bg-muted text-muted-foreground border-border dark:bg-muted/40', size: SIZE_SM },
  explore: { className: 'bg-muted text-muted-foreground border-border dark:bg-muted/40', size: SIZE_SM },
}

const DEFAULT_STYLE: LabelStyle = {
  className: 'bg-muted text-muted-foreground border-border dark:bg-muted/40',
  size: SIZE_MD,
}

const AREA_LABELS = new Set(Object.keys(AREA_LABEL_COLORS))
const URGENCY_LABELS = new Set(Object.keys(URGENCY_LABEL_COLORS))
const TYPE_LABELS = new Set(Object.keys(TYPE_LABEL_COLORS))

/**
 * Type strip palette. Strips reuse the family foreground from `TYPE_LABEL_COLORS`
 * (per D6). Used as a background-color class (`bg-<family>`) on the type-strip
 * element; the family token is dark-mode-aware so the strip resolves correctly
 * in both themes.
 */
const TYPE_STRIP_CLASS: Record<string, string> = {
  bug: 'bg-danger',
  feature: 'bg-success',
  enhancement: 'bg-info',
  'tech-debt': 'bg-muted-foreground',
  performance: 'bg-warning',
}

const TYPE_STRIP_FALLBACK = 'bg-muted-foreground'

const STRIP_PRIORITY = ['bug', 'feature', 'enhancement', 'tech-debt', 'performance']

export function getLabelStyle(label: string): LabelStyle {
  if (TYPE_LABEL_COLORS[label]) return TYPE_LABEL_COLORS[label]
  if (URGENCY_LABEL_COLORS[label]) return URGENCY_LABEL_COLORS[label]
  if (AREA_LABEL_COLORS[label]) return AREA_LABEL_COLORS[label]
  return DEFAULT_STYLE
}

export function getStripColor(labels: Record<string, string> | string[] | undefined | null): string {
  if (!labels) return TYPE_STRIP_FALLBACK
  if (Array.isArray(labels)) {
    for (const type of STRIP_PRIORITY) {
      if (labels.includes(type)) return TYPE_STRIP_CLASS[type]
    }
    return TYPE_STRIP_FALLBACK
  }
  for (const type of STRIP_PRIORITY) {
    if (type in labels) return TYPE_STRIP_CLASS[type]
  }
  return TYPE_STRIP_FALLBACK
}

export function isTypeLabel(label: string): boolean {
  return TYPE_LABELS.has(label)
}

export function isUrgencyLabel(label: string): boolean {
  return URGENCY_LABELS.has(label)
}

export function isAreaLabel(label: string): boolean {
  return AREA_LABELS.has(label)
}

export function getTypeColor(label: string): LabelStyle {
  const style = TYPE_LABEL_COLORS[label]
  if (style) return style
  return DEFAULT_STYLE
}

export function formatPriority(priority: string): string {
  if (!priority) return ''
  return priority.toUpperCase()
}

/**
 * Priority strip palette. Priority is ordinal, NOT state-meaningful
 * (per design D6: collapsing priority onto semantic families would overload
 * the meaning reservation, since p3 is not 'healthy'). Five documented
 * ordinal hues preserved in both themes via `dark:` variants. Each entry
 * pairs a light-theme border-left color with its dark-theme counterpart so
 * the priority strip survives dark mode.
 */
const PRIORITY_STRIP_CLASS: Record<string, string> = {
  p0: 'border-l-red-600 dark:border-l-red-400',
  p1: 'border-l-orange-600 dark:border-l-orange-400',
  p2: 'border-l-yellow-600 dark:border-l-yellow-400',
  p3: 'border-l-green-600 dark:border-l-green-400',
  p4: 'border-l-gray-500 dark:border-l-gray-400',
}

const PRIORITY_STRIP_FALLBACK = 'border-l-gray-500 dark:border-l-gray-400'

export function getPriorityStripColor(priority: string | null | undefined): string {
  if (!priority) return PRIORITY_STRIP_FALLBACK
  return PRIORITY_STRIP_CLASS[priority] ?? PRIORITY_STRIP_FALLBACK
}

/**
 * Priority chip palette. Same ordinal rationale as `PRIORITY_STRIP_CLASS`.
 * Five documented light/dark-aware chip class sets — each entry has a
 * light bg/text pair and a `dark:` counterpart that preserves the ordinal
 * hue (red/orange/yellow/green/gray) in both themes.
 */
const PRIORITY_COLORS: Record<string, LabelStyle> = {
  p0: {
    className: 'bg-red-100 text-red-800 border-red-200 dark:bg-red-900/40 dark:text-red-200 dark:border-red-800',
    size: SIZE_MD,
  },
  p1: {
    className: 'bg-orange-100 text-orange-800 border-orange-200 dark:bg-orange-900/40 dark:text-orange-200 dark:border-orange-800',
    size: SIZE_MD,
  },
  p2: {
    className: 'bg-yellow-100 text-yellow-800 border-yellow-200 dark:bg-yellow-900/40 dark:text-yellow-200 dark:border-yellow-800',
    size: SIZE_MD,
  },
  p3: {
    className: 'bg-green-100 text-green-800 border-green-200 dark:bg-green-900/40 dark:text-green-200 dark:border-green-800',
    size: SIZE_MD,
  },
  p4: {
    className: 'bg-gray-100 text-gray-800 border-gray-200 dark:bg-gray-800/40 dark:text-gray-200 dark:border-gray-700',
    size: SIZE_MD,
  },
}

const PRIORITY_FALLBACK: LabelStyle = PRIORITY_COLORS.p2!

export function getPriorityStyle(priority: string): LabelStyle {
  return PRIORITY_COLORS[priority] ?? PRIORITY_FALLBACK
}

/**
 * Risk chip palette. Risk IS state-meaningful (per D6) — `low` is healthy,
 * `medium` needs attention, `high` is blocking — so it routes through the
 * semantic families. Each entry is the family's soft-tinted treatment
 * (`bg-<family>-subtle text-<family> border-<family>-border`), which is
 * dark-mode-aware by construction.
 */
const RISK_COLORS: Record<string, LabelStyle> = {
  low: { className: 'bg-success-subtle text-success border-success-border', size: SIZE_MD },
  medium: { className: 'bg-warning-subtle text-warning border-warning-border', size: SIZE_MD },
  high: { className: 'bg-danger-subtle text-danger border-danger-border', size: SIZE_MD },
}

const RISK_FALLBACK: LabelStyle = {
  className: 'bg-muted text-muted-foreground border-border',
  size: SIZE_MD,
}

export function getRiskStyle(risk: string): LabelStyle {
  return RISK_COLORS[risk] ?? RISK_FALLBACK
}

export function formatLabelEntry(key: string, value: string): string {
  return `${key}=${value}`
}

export function sortLabels(labels: Record<string, string> | string[] | undefined | null): string[] {
  const types: string[] = []
  const urgency: string[] = []
  const areas: string[] = []
  const other: string[] = []

  if (!labels) return []

  if (Array.isArray(labels)) {
    for (const label of labels) {
      if (isTypeLabel(label)) types.push(label)
      else if (isUrgencyLabel(label)) urgency.push(label)
      else if (isAreaLabel(label)) areas.push(label)
      else other.push(label)
    }
    return [...types.sort(), ...urgency.sort(), ...areas.sort(), ...other.sort()]
  }

  for (const [key, value] of Object.entries(labels)) {
    const formatted = formatLabelEntry(key, value)
    if (isTypeLabel(formatted)) types.push(formatted)
    else if (isUrgencyLabel(formatted)) urgency.push(formatted)
    else if (isAreaLabel(formatted)) areas.push(formatted)
    else other.push(formatted)
  }

  return [...types.sort(), ...urgency.sort(), ...areas.sort(), ...other.sort()]
}