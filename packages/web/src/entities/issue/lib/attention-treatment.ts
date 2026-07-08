import { statusTreatment, type StatusTreatment } from '@/shared/status-presentation'
import type { AttentionItem } from '../model/attention'

export function attentionItemTreatment(item: AttentionItem): StatusTreatment {
  switch (item.kind) {
    case 'approval-needed':
      return statusTreatment('approval', 'awaiting')
    case 'integration-failed':
      return statusTreatment('workflow-stage', 'failed')
    case 'blocked':
      return statusTreatment('issue-health', 'blocked')
    case 'interrupted':
      return statusTreatment('issue-health', 'interrupted')
  }
}

export function attentionSummaryTreatment(items: AttentionItem[]): StatusTreatment {
  return items.some((item) => attentionItemTreatment(item).family === 'danger')
    ? statusTreatment('workflow-stage', 'failed')
    : statusTreatment('approval', 'awaiting')
}
