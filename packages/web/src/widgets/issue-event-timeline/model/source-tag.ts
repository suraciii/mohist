import type { TimelineSource } from './types'

export function classifySource(type: string): TimelineSource {
  if (type.startsWith('com.mohist.issue.') || type === 'comment_added') {
    return 'ISSUE'
  }
  return 'WORKFLOW'
}
