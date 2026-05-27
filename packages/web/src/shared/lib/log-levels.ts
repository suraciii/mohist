export const LEVEL_COLORS: Record<string, string> = {
  ERROR: 'text-red-600 bg-red-50',
  WARN: 'text-yellow-600 bg-yellow-50',
  INFO: 'text-blue-600 bg-blue-50',
  DEBUG: 'text-gray-500 bg-gray-100',
}

export const LEVEL_CHIP_COLORS: Record<string, string> = {
  ERROR: 'bg-red-100 text-red-700 border-red-200',
  WARN: 'bg-yellow-100 text-yellow-700 border-yellow-200',
  INFO: 'bg-blue-100 text-blue-700 border-blue-200',
  DEBUG: 'bg-gray-100 text-gray-600 border-gray-200',
}

export const ALL_LEVELS = ['DEBUG', 'INFO', 'WARN', 'ERROR'] as const
export type LogLevel = (typeof ALL_LEVELS)[number]
