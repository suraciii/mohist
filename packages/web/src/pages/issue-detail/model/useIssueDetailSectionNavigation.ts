import { useEffect } from 'react'
import { useLocation, useNavigate, type Location, type To } from 'react-router-dom'

export type IssueDetailSection = 'workflow' | 'artifacts' | 'activity' | 'comments'

const sections = new Set<IssueDetailSection>(['workflow', 'artifacts', 'activity', 'comments'])

export function issueDetailSectionFromHash(hash: string): IssueDetailSection | null {
  const section = hash.startsWith('#') ? hash.slice(1) : hash
  return sections.has(section as IssueDetailSection) ? section as IssueDetailSection : null
}

export function issueDetailSectionLocation(
  location: Pick<Location, 'pathname' | 'search'>,
  section: IssueDetailSection,
): To {
  return {
    pathname: location.pathname,
    search: location.search,
    hash: `#${section}`,
  }
}

interface IssueDetailSectionReadiness {
  workflow: boolean
  artifacts: boolean
  comments: boolean
}

export function useIssueDetailSectionNavigation(readiness: IssueDetailSectionReadiness) {
  const location = useLocation()
  const navigate = useNavigate()
  const section = issueDetailSectionFromHash(location.hash)
  const workflowReady = readiness.workflow
  const artifactsReady = readiness.artifacts
  const commentsReady = readiness.comments

  useEffect(() => {
    if (section === null || section === 'activity') return

    const ready = section === 'workflow'
      ? workflowReady
      : section === 'artifacts'
        ? artifactsReady
        : commentsReady

    if (!ready) return
    document.getElementById(section)?.scrollIntoView({ block: 'start' })
  }, [section, workflowReady, artifactsReady, commentsReady])

  const links: Record<IssueDetailSection, To> = {
    workflow: issueDetailSectionLocation(location, 'workflow'),
    artifacts: issueDetailSectionLocation(location, 'artifacts'),
    activity: issueDetailSectionLocation(location, 'activity'),
    comments: issueDetailSectionLocation(location, 'comments'),
  }

  function onActivityOpenChange(open: boolean) {
    if (open) {
      if (section !== 'activity') navigate(links.activity)
      return
    }

    if (section === 'activity') {
      navigate({ pathname: location.pathname, search: location.search, hash: '' }, { replace: true })
    }
  }

  return {
    section,
    links,
    activityOpen: section === 'activity',
    onActivityOpenChange,
  }
}
