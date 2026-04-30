import CronExpressionParser from 'cron-parser';

export type ScheduleConfig =
  | { type: 'every'; intervalMs: number; anchor?: string }
  | { type: 'cron'; expression: string }
  | { type: 'at'; timestamp: string };

export type RawScheduleInput = {
  every?: string;
  cron?: string;
  at?: string;
  anchor?: string;
};

const DURATION_REGEX = /^(\d+)(s|m|h|d)$/;

const UNIT_MS: Record<string, number> = {
  s: 1000,
  m: 60 * 1000,
  h: 60 * 60 * 1000,
  d: 24 * 60 * 60 * 1000,
};

export function parseDuration(input: string): number | null {
  const match = DURATION_REGEX.exec(input.trim());
  if (!match) return null;
  const value = parseInt(match[1], 10);
  const unit = match[2];
  return value * UNIT_MS[unit];
}

function nextAnchorOccurrence(anchor: string, now: Date): Date {
  const parts = /^(\d{1,2}):(\d{2})$/.exec(anchor.trim());
  if (!parts) return now;
  const hours = parseInt(parts[1], 10);
  const minutes = parseInt(parts[2], 10);
  if (hours < 0 || hours > 23 || minutes < 0 || minutes > 59) return now;

  const candidate = new Date(now);
  candidate.setHours(hours, minutes, 0, 0);
  if (candidate.getTime() <= now.getTime()) {
    candidate.setDate(candidate.getDate() + 1);
  }
  return candidate;
}

export function computeNextRun(config: ScheduleConfig, now: Date = new Date()): string {
  switch (config.type) {
    case 'every': {
      if (config.anchor) {
        const next = nextAnchorOccurrence(config.anchor, now);
        return next.toISOString();
      }
      return new Date(now.getTime() + config.intervalMs).toISOString();
    }
    case 'cron': {
      const interval = CronExpressionParser.parse(config.expression, { currentDate: now });
      const next = interval.next();
      return next.toISOString() ?? new Date(next.getTime()).toISOString();
    }
    case 'at': {
      return config.timestamp;
    }
  }
}

export function parseScheduleConfig(raw: unknown): ScheduleConfig | null {
  if (!raw || typeof raw !== 'object') return null;
  const input = raw as RawScheduleInput;

  const types = [input.every, input.cron, input.at].filter(Boolean);
  if (types.length !== 1) return null;

  if (input.every) {
    const intervalMs = parseDuration(input.every);
    if (intervalMs === null || intervalMs <= 0) return null;
    if (input.anchor) {
      const parts = /^(\d{1,2}):(\d{2})$/.exec(input.anchor.trim());
      if (!parts) return null;
      const h = parseInt(parts[1], 10);
      const m = parseInt(parts[2], 10);
      if (h < 0 || h > 23 || m < 0 || m > 59) return null;
      return { type: 'every', intervalMs, anchor: input.anchor };
    }
    return { type: 'every', intervalMs };
  }

  if (input.cron) {
    try {
      CronExpressionParser.parse(input.cron);
    } catch {
      return null;
    }
    return { type: 'cron', expression: input.cron };
  }

  if (input.at) {
    const ts = Date.parse(input.at);
    if (isNaN(ts)) return null;
    return { type: 'at', timestamp: input.at };
  }

  return null;
}
