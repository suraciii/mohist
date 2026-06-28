import { describe, expect, it } from 'vitest'
import { deriveGraphBannerState, GRAPH_BANNER_MESSAGES } from './graphBanner'

describe('deriveGraphBannerState', () => {
  it('returns no banner when the graph has not reported any renderability yet (loading)', () => {
    const state = deriveGraphBannerState({
      graphRenderError: false,
      graphRenderable: { renderable: false, reason: null },
    })
    expect(state).toEqual({ show: false, reason: null, message: null })
  })

  it('returns the cyclic banner with the dependency-cycle explanation when reason is cyclic', () => {
    const state = deriveGraphBannerState({
      graphRenderError: false,
      graphRenderable: { renderable: false, reason: 'cyclic' },
    })
    expect(state.show).toBe(true)
    expect(state.reason).toBe('cyclic')
    expect(state.message).toBe(GRAPH_BANNER_MESSAGES.cyclic)
    expect(state.message).toMatch(/cycle/i)
    expect(state.message).toMatch(/use the list below/i)
  })

  it('returns the empty banner explaining there is not enough data when reason is empty', () => {
    const state = deriveGraphBannerState({
      graphRenderError: false,
      graphRenderable: { renderable: false, reason: 'empty' },
    })
    expect(state.show).toBe(true)
    expect(state.reason).toBe('empty')
    expect(state.message).toBe(GRAPH_BANNER_MESSAGES.empty)
    expect(state.message).toMatch(/not enough/i)
    expect(state.message).toMatch(/use the list below/i)
  })

  it('returns the error fallback banner ("Graph is unavailable") when the Error Boundary caught a render exception', () => {
    const state = deriveGraphBannerState({
      graphRenderError: true,
      graphRenderable: { renderable: false, reason: null },
    })
    expect(state.show).toBe(true)
    expect(state.reason).toBe('error')
    expect(state.message).toBe(GRAPH_BANNER_MESSAGES.error)
    expect(state.message).toMatch(/graph is unavailable/i)
    expect(state.message).toMatch(/use the list below/i)
  })

  it('returns no banner when the graph is renderable (renderable=true, reason=null)', () => {
    const state = deriveGraphBannerState({
      graphRenderError: false,
      graphRenderable: { renderable: true, reason: null },
    })
    expect(state).toEqual({ show: false, reason: null, message: null })
  })

  it('prefers the error fallback over any stale renderability reason (Error Boundary wins)', () => {
    const state = deriveGraphBannerState({
      graphRenderError: true,
      graphRenderable: { renderable: false, reason: 'cyclic' },
    })
    expect(state.show).toBe(true)
    expect(state.reason).toBe('error')
    expect(state.message).toBe(GRAPH_BANNER_MESSAGES.error)
  })

  it('uses the dedicated messages map so each unrenderable reason has a distinct, user-facing copy', () => {
    expect(GRAPH_BANNER_MESSAGES.cyclic).not.toBe(GRAPH_BANNER_MESSAGES.empty)
    expect(GRAPH_BANNER_MESSAGES.cyclic).not.toBe(GRAPH_BANNER_MESSAGES.error)
    expect(GRAPH_BANNER_MESSAGES.empty).not.toBe(GRAPH_BANNER_MESSAGES.error)
    for (const message of Object.values(GRAPH_BANNER_MESSAGES)) {
      expect(message).toMatch(/use the list below/i)
    }
  })
})