const SENSITIVE_FIELDS = ['apiKey', 'secret', 'token'] as const;

type SensitiveField = (typeof SENSITIVE_FIELDS)[number];

function maskValue(value: string): string {
  if (!value || value.length <= 7) {
    return '********';
  }
  const prefix = value.slice(0, 4);
  const suffix = value.slice(-3);
  const middleLength = Math.max(value.length - 7, 8);
  return `${prefix}${'*'.repeat(middleLength)}${suffix}`;
}

function isObject(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

export function maskSensitiveData<T extends Record<string, unknown>>(obj: T): T {
  const clone = JSON.parse(JSON.stringify(obj)) as T;

  function maskRecursive(target: Record<string, unknown>): Record<string, unknown> {
    for (const key of Object.keys(target)) {
      const value = target[key];

      if (SENSITIVE_FIELDS.includes(key as SensitiveField) && typeof value === 'string') {
        target[key] = maskValue(value);
      } else if (isObject(value)) {
        target[key] = maskRecursive(value as Record<string, unknown>);
      } else if (Array.isArray(value)) {
        target[key] = value.map((item) =>
          isObject(item) ? maskRecursive(item as Record<string, unknown>) : item
        );
      }
    }
    return target;
  }

  return maskRecursive(clone) as T;
}