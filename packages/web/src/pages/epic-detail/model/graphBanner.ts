import type { Renderability } from '../../../widgets/epic-dependency-graph'

export type GraphUnrenderableReason = 'cyclic' | 'empty' | 'error'

export interface GraphBannerState {
  show: boolean
  reason: GraphUnrenderableReason | null
  message: string | null
}

export interface GraphBannerInput {
  graphRenderError: boolean
  graphRenderable: { renderable: boolean; reason: Renderability | null }
}

const BANNER_MESSAGES: Record<GraphUnrenderableReason, string> = {
  cyclic: "Dependency graph has a cycle and can't be drawn. Use the list below.",
  empty: 'Not enough linked issues to draw a graph. Use the list below.',
  error: 'Graph is unavailable. Use the list below.',
}

export function deriveGraphBannerState({ graphRenderError, graphRenderable }: GraphBannerInput): GraphBannerState {
  if (graphRenderError) {
    return { show: true, reason: 'error', message: BANNER_MESSAGES.error }
  }
  if (graphRenderable.reason === 'cyclic') {
    return { show: true, reason: 'cyclic', message: BANNER_MESSAGES.cyclic }
  }
  if (graphRenderable.reason === 'empty') {
    return { show: true, reason: 'empty', message: BANNER_MESSAGES.empty }
  }
  return { show: false, reason: null, message: null }
}

export const GRAPH_BANNER_MESSAGES = BANNER_MESSAGES