import { Link } from 'react-router-dom'
import { ChevronLeftIcon, ChevronRightIcon } from 'lucide-react'
import type { RuntimeSessionLineageEntry } from '../../../../entities/coder-session'

export interface CompactionLineageLinkProps {
  /**
   * Ordered lineage of runtime sessions bound to this Mohist session
   * over its lifetime (projected from `AgentSessionMetadataDto`).
   * Null / empty / single-entry chains render nothing — the spec
   * requires no link when there is no compaction relationship.
   */
  runtimeSessionLineage?: RuntimeSessionLineageEntry[] | null
  /**
   * The runtime session the user is currently looking at. When the
   * URL carries `?rt=<runtimeSessionId>` the page passes that value;
   * otherwise it falls back to the latest binding (the page default).
   * Null/undefined means the latest entry in the chain is in view.
   */
  viewedRuntimeSessionId?: string | null
  /**
   * Builds the navigation target for a runtime session link. The
   * returned path MUST stay on the existing session route and MUST
   * encode the target runtime session id as a `?rt=` query param, e.g.
   * `/Test/issues/12/workflow/sessions/compile-assets?rt=rt-abc`. Both
   * predecessor and successor links share the same base path — the
   * chain is intra-Mohist-session.
   */
  buildTargetPath: (runtimeSessionId: string) => string
  className?: string
}

/**
 * Resolve which runtime session the user is currently looking at
 * within the lineage chain. Returns the index of the matching entry,
 * or `-1` if not found.
 */
function findViewedIndex(
  lineage: RuntimeSessionLineageEntry[],
  viewedRuntimeSessionId: string | null | undefined,
): number {
  if (!viewedRuntimeSessionId) return -1
  return lineage.findIndex((entry) => entry.agentRuntimeSessionId === viewedRuntimeSessionId)
}

/**
 * Compact, navigable lineage link between runtime sessions within a
 * single Mohist session. Renders a single-row component with at most
 * two anchors — one pointing back to the predecessor runtime session
 * and one pointing forward to the successor — based on which runtime
 * session is currently in view.
 *
 * The component hides itself entirely when:
 * - the lineage is null, undefined, or empty (truly unbound session)
 * - the lineage has a single entry (no compaction relationship)
 *
 * Both predecessor and successor links share the same session route
 * (the Mohist session is the stable identity, the runtime session is
 * a mutable facet). Navigation is implemented via the
 * `?rt=<runtimeSessionId>` query-param anchor scheme so the page can
 * later scroll to / anchor the transcript at the compaction boundary
 * for the targeted runtime session — no new per-runtime-session route
 * is added.
 */
export function CompactionLineageLink({
  runtimeSessionLineage,
  viewedRuntimeSessionId,
  buildTargetPath,
  className,
}: CompactionLineageLinkProps) {
  const lineage = runtimeSessionLineage ?? []
  if (lineage.length < 2) {
    return null
  }

  let viewedIndex = findViewedIndex(lineage, viewedRuntimeSessionId)
  if (viewedIndex === -1) {
    // No explicit viewed id, or it doesn't match any entry (legacy
    // sessions, malformed query param, etc.). The page default is
    // "showing the latest runtime session", so anchor the link
    // context to the last entry in the chain. This keeps the
    // common-case behavior — page shows latest, predecessor link is
    // visible — even when no `?rt` query param is present.
    viewedIndex = lineage.length - 1
  }

  const predecessor = viewedIndex > 0 ? lineage[viewedIndex - 1] : null
  const successor = viewedIndex < lineage.length - 1 ? lineage[viewedIndex + 1] : null

  if (!predecessor && !successor) {
    return null
  }

  return (
    <div
      className={className}
      data-testid="compaction-lineage-link"
      data-lineage-length={lineage.length}
      data-viewed-index={viewedIndex}
    >
      <div className="flex items-center gap-1.5 text-[10px] font-medium text-info">
        <svg
          className="h-3 w-3 shrink-0 text-info"
          viewBox="0 0 20 20"
          fill="currentColor"
          aria-hidden="true"
        >
          <path
            fillRule="evenodd"
            d="M12.5 5a.75.75 0 01.75-.75h3.5a.75.75 0 01.75.75v10.5a.75.75 0 01-.75.75h-3.5a.75.75 0 010-1.5h2.75V5.75h-2.75A.75.75 0 0112.5 5zM7.78 5.97a.75.75 0 011.06 0l3 3a.75.75 0 010 1.06l-3 3a.75.75 0 11-1.06-1.06L9.44 9.5H3.75a.75.75 0 010-1.5h5.69L7.78 7.03a.75.75 0 010-1.06z"
            clipRule="evenodd"
          />
        </svg>
        <span className="uppercase tracking-wide text-info" data-testid="compaction-lineage-link-label">
          Compaction chain
        </span>
        {predecessor && (
          <>
            <span className="text-info/60" aria-hidden="true">·</span>
            <Link
              to={buildTargetPath(predecessor.agentRuntimeSessionId)}
              className="inline-flex items-center gap-1 rounded border border-info-border bg-background px-1.5 py-0.5 font-mono text-info transition-colors hover:border-info hover:bg-info-subtle hover:text-info"
              data-testid="compaction-lineage-link-predecessor"
              data-target-runtime-session-id={predecessor.agentRuntimeSessionId}
              title={`Previous runtime session: ${predecessor.agentRuntimeSessionId}`}
              aria-label={`Navigate to previous runtime session ${predecessor.agentRuntimeSessionId}`}
            >
              <ChevronLeftIcon className="h-3 w-3 shrink-0" aria-hidden="true" />
              <span className="truncate">{predecessor.agentRuntimeSessionId}</span>
            </Link>
          </>
        )}
        {successor && (
          <>
            <span className="text-info/60" aria-hidden="true">·</span>
            <Link
              to={buildTargetPath(successor.agentRuntimeSessionId)}
              className="inline-flex items-center gap-1 rounded border border-info-border bg-background px-1.5 py-0.5 font-mono text-info transition-colors hover:border-info hover:bg-info-subtle hover:text-info"
              data-testid="compaction-lineage-link-successor"
              data-target-runtime-session-id={successor.agentRuntimeSessionId}
              title={`Next runtime session: ${successor.agentRuntimeSessionId}`}
              aria-label={`Navigate to next runtime session ${successor.agentRuntimeSessionId}`}
            >
              <span className="truncate">{successor.agentRuntimeSessionId}</span>
              <ChevronRightIcon className="h-3 w-3 shrink-0" aria-hidden="true" />
            </Link>
          </>
        )}
      </div>
    </div>
  )
}
