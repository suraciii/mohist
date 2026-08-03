import type { TimelineEntry, TimelineGroup, TimelineItem } from './types'

function isGroupCandidate(item: TimelineItem): item is TimelineItem & { renderClass: 'file-read' | 'shell' | 'tool' } {
  return (item.renderClass === 'file-read' || item.renderClass === 'shell' || item.renderClass === 'tool')
    && item.isTerminal
    && !item.summary.endsWith(' → 失败')
}

function compatible(left: TimelineItem, right: TimelineItem): boolean {
  if (left.renderClass !== right.renderClass) return false
  if (left.groupKey !== undefined || right.groupKey !== undefined) return left.groupKey === right.groupKey
  return true
}

function groupSummary(renderClass: TimelineGroup['renderClass'], count: number): string {
  switch (renderClass) {
    case 'file-read':
      return `读取了 ${count} 个文件`
    case 'shell':
      return `运行了 ${count} 个命令`
    case 'tool':
      return `执行了 ${count} 个工具`
  }
}

function toGroup(items: TimelineItem[]): TimelineGroup {
  const first = items[0]
  const last = items[items.length - 1]
  if (!first || !last || !isGroupCandidate(first)) throw new Error('Timeline group requires items')
  return {
    id: `group:${first.id}:${last.id}`,
    renderClass: first.renderClass,
    sourceIds: items.flatMap(item => item.sourceIds),
    summary: groupSummary(first.renderClass, items.length),
    salience: 'low',
    items,
  }
}

export function groupTimelineItems(items: TimelineItem[]): TimelineEntry[] {
  const entries: TimelineEntry[] = []
  let index = 0

  while (index < items.length) {
    const item = items[index]
    if (!item || !isGroupCandidate(item)) {
      if (item) entries.push(item)
      index += 1
      continue
    }

    let end = index + 1
    while (end < items.length) {
      const candidate = items[end]
      if (!candidate || !isGroupCandidate(candidate) || !compatible(item, candidate)) break
      end += 1
    }

    const segment = items.slice(index, end)
    if (segment.length >= 3) entries.push(toGroup(segment))
    else entries.push(...segment)
    index = end
  }

  return entries
}
