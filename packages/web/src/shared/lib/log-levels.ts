import { statusTreatment, type StatusTreatment } from '@/shared/status-presentation'

export const ALL_LEVELS = ['DEBUG', 'INFO', 'WARN', 'ERROR'] as const
export type LogLevel = (typeof ALL_LEVELS)[number]

const LEVEL_TO_SEVERITY: Record<LogLevel, 'DEBUG' | 'INFO' | 'WARN' | 'ERROR'> = {
  DEBUG: 'DEBUG',
  INFO: 'INFO',
  WARN: 'WARN',
  ERROR: 'ERROR',
}

function treatmentFor(level: LogLevel | string | null | undefined): StatusTreatment {
  const severity = (ALL_LEVELS as readonly string[]).includes(level ?? '')
    ? LEVEL_TO_SEVERITY[level as LogLevel]
    : 'DEBUG'
  return statusTreatment('severity', severity)
}

/**
 * Log-line level color. Routes through the shared status-presentation
 * layer so ERROR→danger, WARN→warning, INFO→info, DEBUG→muted. The
 * returned string is the treatment's `container` set (background,
 * foreground, and border in one class string) so a log row can apply
 * the whole pill in one className.
 */
export function getLevelColors(level: LogLevel | string | null | undefined): string {
  return treatmentFor(level).container
}

/**
 * Legacy `LEVEL_COLORS` map. Kept as a thin shim over `getLevelColors`
 * so call sites that index by level keep working. New code should call
 * `getLevelColors(level)` directly.
 */
export const LEVEL_COLORS: Record<string, string> = Object.freeze({
  ERROR: getLevelColors('ERROR'),
  WARN: getLevelColors('WARN'),
  INFO: getLevelColors('INFO'),
  DEBUG: getLevelColors('DEBUG'),
})

/**
 * Log-level chip color (bordered variant). Composed from the same
 * treatment as `LEVEL_COLORS` so the chip and the line share a family.
 * Border utilities that are not part of `TREATMENT_BY_FAMILY` are
 * composed via the family's base hue.
 */
export function getLevelChipColors(level: LogLevel | string | null | undefined): string {
  const treatment = treatmentFor(level)
  return `${treatment.container} border ${treatment.border}`
}

/**
 * Legacy `LEVEL_CHIP_COLORS` map. Same shim approach as `LEVEL_COLORS`.
 */
export const LEVEL_CHIP_COLORS: Record<string, string> = Object.freeze({
  ERROR: getLevelChipColors('ERROR'),
  WARN: getLevelChipColors('WARN'),
  INFO: getLevelChipColors('INFO'),
  DEBUG: getLevelChipColors('DEBUG'),
})