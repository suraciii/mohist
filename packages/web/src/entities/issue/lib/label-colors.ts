type LabelStyle = {
  bg: string
  text: string
  size: 'sm' | 'md'
}

const TYPE_LABEL_COLORS: Record<string, LabelStyle> = {
  bug: { bg: '#fee2e2', text: '#ef4444', size: 'md' },
  feature: { bg: '#dcfce7', text: '#22c55e', size: 'md' },
  enhancement: { bg: '#dbeafe', text: '#3b82f6', size: 'md' },
  'tech-debt': { bg: '#f3f4f6', text: '#6b7280', size: 'md' },
  performance: { bg: '#fef9c3', text: '#eab308', size: 'md' },
}

const URGENCY_LABEL_COLORS: Record<string, LabelStyle> = {
  critical: { bg: '#991b1b', text: '#ffffff', size: 'md' },
}

const AREA_LABEL_COLORS: Record<string, LabelStyle> = {
  agent: { bg: '#f3f4f6', text: '#6b7280', size: 'sm' },
  webui: { bg: '#f3f4f6', text: '#6b7280', size: 'sm' },
  api: { bg: '#f3f4f6', text: '#6b7280', size: 'sm' },
  frontend: { bg: '#f3f4f6', text: '#6b7280', size: 'sm' },
  logging: { bg: '#f3f4f6', text: '#6b7280', size: 'sm' },
  'data-model': { bg: '#f3f4f6', text: '#6b7280', size: 'sm' },
  recovery: { bg: '#f3f4f6', text: '#6b7280', size: 'sm' },
  explore: { bg: '#f3f4f6', text: '#6b7280', size: 'sm' },
}

const DEFAULT_STYLE: LabelStyle = { bg: '#f3f4f6', text: '#6b7280', size: 'md' }

const AREA_LABELS = new Set(Object.keys(AREA_LABEL_COLORS))
const URGENCY_LABELS = new Set(Object.keys(URGENCY_LABEL_COLORS))
const TYPE_LABELS = new Set(Object.keys(TYPE_LABEL_COLORS))

const TYPE_STRIP_COLORS: Record<string, string> = {
  bug: '#ef4444',
  feature: '#22c55e',
  enhancement: '#3b82f6',
  'tech-debt': '#6b7280',
  performance: '#eab308',
}

const STRIP_PRIORITY = ['bug', 'feature', 'enhancement', 'tech-debt', 'performance']

export function getLabelStyle(label: string): LabelStyle {
  if (TYPE_LABEL_COLORS[label]) return TYPE_LABEL_COLORS[label]
  if (URGENCY_LABEL_COLORS[label]) return URGENCY_LABEL_COLORS[label]
  if (AREA_LABEL_COLORS[label]) return AREA_LABEL_COLORS[label]
  return DEFAULT_STYLE
}

export function getStripColor(labels: Record<string, string> | string[] | undefined | null): string {
  if (!labels) return '#6b7280'
  if (Array.isArray(labels)) {
    for (const type of STRIP_PRIORITY) {
      if (labels.includes(type)) return TYPE_STRIP_COLORS[type]
    }
    return '#6b7280'
  }
  for (const type of STRIP_PRIORITY) {
    if (type in labels) return TYPE_STRIP_COLORS[type]
  }
  return '#6b7280'
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

export function getTypeColor(label: string): { bg: string; text: string } {
  const style = TYPE_LABEL_COLORS[label]
  if (style) return { bg: style.bg, text: style.text }
  return { bg: '#f3f4f6', text: '#6b7280' }
}

export function formatPriority(priority: string): string {
  if (!priority) return ''
  return priority.toUpperCase()
}

const PRIORITY_STRIP_COLORS: Record<string, string> = {
  p0: '#dc2626',
  p1: '#ea580c',
  p2: '#ca8a04',
  p3: '#16a34a',
  p4: '#6b7280',
}

const PRIORITY_STRIP_FALLBACK = '#6b7280'

export function getPriorityStripColor(priority: string | null | undefined): string {
  if (!priority) return PRIORITY_STRIP_FALLBACK
  return PRIORITY_STRIP_COLORS[priority] ?? PRIORITY_STRIP_FALLBACK
}

const PRIORITY_COLORS: Record<string, { bg: string; text: string }> = {
  p0: { bg: '#fee2e2', text: '#7f1d1d' },
  p1: { bg: '#ffedd5', text: '#7c2d12' },
  p2: { bg: '#fef9c3', text: '#713f12' },
  p3: { bg: '#dcfce7', text: '#14532d' },
  p4: { bg: '#f3f4f6', text: '#1f2937' },
}

export function getPriorityStyle(priority: string): { bg: string; text: string } {
  return PRIORITY_COLORS[priority] ?? { bg: '#fef9c3', text: '#713f12' }
}

const RISK_COLORS: Record<string, { bg: string; text: string }> = {
  low: { bg: '#dcfce7', text: '#16a34a' },
  medium: { bg: '#fef9c3', text: '#ca8a04' },
  high: { bg: '#fee2e2', text: '#dc2626' },
}

export function getRiskStyle(risk: string): { bg: string; text: string } {
  return RISK_COLORS[risk] ?? { bg: '#f3f4f6', text: '#6b7280' }
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
