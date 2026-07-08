/**
 * JS mirror of the semantic-token values defined in `app/styles/index.css`.
 *
 * The CSS exposes four families — `success` / `warning` / `info` / `danger` —
 * each with `-subtle` / `-foreground` / `-border` (and a base color) for both
 * `:root` (light) and `.dark` (dark). This file mirrors those values as
 * `oklch(L C H)` triples so unit/spec tests can compute contrast directly
 * without relying on jsdom to resolve `oklch()` (which is flaky across
 * engines).
 *
 * Invariant: a guard unit test (`tokens.guard.test.ts`) parses `index.css` and
 * fails on any drift between this fixture and the stylesheet. Keep the two in
 * sync — `index.css` is the source of truth for the rendered app.
 */

export type Family = 'success' | 'warning' | 'info' | 'danger'

export interface Oklch {
  L: number
  C: number
  H: number
}

export interface FamilyTokens {
  base: Oklch
  subtle: Oklch
  foreground: Oklch
  border: Oklch
}

export interface TokenTable {
  light: Record<Family, FamilyTokens>
  dark: Record<Family, FamilyTokens>
}

export const TOKENS: TokenTable = {
  light: {
    success: {
      base: { L: 0.45, C: 0.16, H: 145 },
      subtle: { L: 0.96, C: 0.05, H: 145 },
      foreground: { L: 0.985, C: 0, H: 0 },
      border: { L: 0.84, C: 0.10, H: 145 },
    },
    warning: {
      base: { L: 0.50, C: 0.15, H: 75 },
      subtle: { L: 0.96, C: 0.06, H: 75 },
      foreground: { L: 0.985, C: 0, H: 0 },
      border: { L: 0.84, C: 0.11, H: 75 },
    },
    info: {
      base: { L: 0.50, C: 0.16, H: 245 },
      subtle: { L: 0.95, C: 0.04, H: 245 },
      foreground: { L: 0.985, C: 0, H: 0 },
      border: { L: 0.82, C: 0.08, H: 245 },
    },
    danger: {
      base: { L: 0.52, C: 0.20, H: 27 },
      subtle: { L: 0.96, C: 0.04, H: 27 },
      foreground: { L: 0.985, C: 0, H: 0 },
      border: { L: 0.84, C: 0.10, H: 27 },
    },
  },
  dark: {
    success: {
      base: { L: 0.74, C: 0.16, H: 145 },
      subtle: { L: 0.28, C: 0.06, H: 145 },
      foreground: { L: 0.145, C: 0, H: 0 },
      border: { L: 0.42, C: 0.10, H: 145 },
    },
    warning: {
      base: { L: 0.78, C: 0.15, H: 75 },
      subtle: { L: 0.30, C: 0.06, H: 75 },
      foreground: { L: 0.145, C: 0, H: 0 },
      border: { L: 0.45, C: 0.10, H: 75 },
    },
    info: {
      base: { L: 0.74, C: 0.14, H: 245 },
      subtle: { L: 0.28, C: 0.06, H: 245 },
      foreground: { L: 0.145, C: 0, H: 0 },
      border: { L: 0.42, C: 0.10, H: 245 },
    },
    danger: {
      base: { L: 0.72, C: 0.18, H: 27 },
      subtle: { L: 0.30, C: 0.06, H: 27 },
      foreground: { L: 0.145, C: 0, H: 0 },
      border: { L: 0.45, C: 0.10, H: 27 },
    },
  },
}

export type Theme = keyof TokenTable

export const FAMILIES: readonly Family[] = ['success', 'warning', 'info', 'danger']
export const THEMES: readonly Theme[] = ['light', 'dark']
