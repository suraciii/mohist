import { useEffect, useRef } from 'react'
import { toast } from 'sonner'
import type { RuntimeSummary } from '../../../widgets/issue-workflow'

interface IssueAttentionNudgeOptions {
  issueNumber: number
  summary: RuntimeSummary | null
}

export function useIssueAttentionNudges({ issueNumber, summary }: IssueAttentionNudgeOptions): void {
  const previousIssueNumber = useRef(issueNumber)
  const previousSummary = useRef(summary)

  useEffect(() => {
    if (previousIssueNumber.current !== issueNumber) {
      previousIssueNumber.current = issueNumber
      previousSummary.current = summary
      return
    }

    if (previousSummary.current === null) {
      previousSummary.current = summary
      return
    }

    if (summary === previousSummary.current) return

    if (summary === 'approval-required') {
      toast.info(`Issue #${issueNumber} needs approval`)
    } else if (summary === 'blocked') {
      toast.error(`Issue #${issueNumber} is blocked`)
    }

    previousSummary.current = summary
  }, [issueNumber, summary])
}
