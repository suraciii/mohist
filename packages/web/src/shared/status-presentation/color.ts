import type { Oklch } from './tokens'

/**
 * Convert `oklch(L C H)` to linear-light sRGB channels in `[0, 1]`.
 *
 * Implements the CSS Color 4 spec (Oklab → LMS → linear sRGB). The output may
 * be out-of-gamut for some inputs; callers (contrast computation, color
 * utilities) typically clamp to `[0, 1]` before further processing.
 */
export function oklchToLinearSrgb(color: Oklch): [number, number, number] {
  const { L, C, H } = color
  const hRad = (H * Math.PI) / 180
  const a = C * Math.cos(hRad)
  const b = C * Math.sin(hRad)

  const l_ = L + 0.3963377774 * a + 0.2158037573 * b
  const m_ = L - 0.1055613458 * a - 0.0638541728 * b
  const s_ = L - 0.0894841775 * a - 1.2914855480 * b

  const l = l_ * l_ * l_
  const m = m_ * m_ * m_
  const s = s_ * s_ * s_

  return [
    4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s,
    -1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s,
    -0.0041960863 * l - 0.7034186147 * m + 1.7076147010 * s,
  ]
}

/**
 * Apply the sRGB companding (linear → gamma-encoded) curve.
 */
function srgbCompand(u: number): number {
  const abs = Math.abs(u)
  if (abs <= 0.0031308) return 12.92 * u
  return Math.sign(u) * (1.055 * Math.pow(abs, 1 / 2.4) - 0.055)
}

/**
 * Convert `oklch(L C H)` to an `oklch(L% C H)` string suitable for embedding in
 * CSS (matches what `index.css` ships).
 */
export function oklchToCss(color: Oklch): string {
  const L = color.L.toFixed(3).replace(/\.?0+$/, '')
  const C = color.C.toFixed(3).replace(/\.?0+$/, '')
  const H = Number.isInteger(color.H) ? color.H.toString() : color.H.toFixed(3).replace(/\.?0+$/, '')
  return `oklch(${L} ${C} ${H})`
}

/**
 * Convert an Oklch color to a sRGB triple in `[0, 1]`, gamma-encoded (i.e.
 * ready for sRGB channel extraction used in WCAG luminance computation).
 */
export function oklchToSrgb(color: Oklch): [number, number, number] {
  const [r, g, b] = oklchToLinearSrgb(color).map((u) => Math.max(0, Math.min(1, u)))
  return [srgbCompand(r), srgbCompand(g), srgbCompand(b)]
}

function srgbToLinearChannel(c: number): number {
  return c <= 0.03928 ? c / 12.92 : Math.pow((c + 0.055) / 1.055, 2.4)
}

/**
 * WCAG 2.1 relative luminance for a sRGB triple in `[0, 1]`.
 */
export function relativeLuminance([r, g, b]: [number, number, number]): number {
  return (
    0.2126 * srgbToLinearChannel(r) +
    0.7152 * srgbToLinearChannel(g) +
    0.0722 * srgbToLinearChannel(b)
  )
}

/**
 * WCAG 2.1 contrast ratio between two sRGB triples.
 */
export function contrastRatio(a: [number, number, number], b: [number, number, number]): number {
  const la = relativeLuminance(a)
  const lb = relativeLuminance(b)
  const [hi, lo] = la >= lb ? [la, lb] : [lb, la]
  return (hi + 0.05) / (lo + 0.05)
}