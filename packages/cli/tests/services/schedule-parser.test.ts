import { describe, it, expect } from 'vitest';
import {
  parseDuration,
  computeNextRun,
  parseScheduleConfig,
} from '../../src/services/schedule-parser';

describe('parseDuration', () => {
  it('should parse seconds', () => {
    expect(parseDuration('30s')).toBe(30_000);
  });

  it('should parse minutes', () => {
    expect(parseDuration('5m')).toBe(5 * 60 * 1000);
  });

  it('should parse hours', () => {
    expect(parseDuration('24h')).toBe(24 * 60 * 60 * 1000);
  });

  it('should parse days', () => {
    expect(parseDuration('1d')).toBe(24 * 60 * 60 * 1000);
  });

  it('should parse multi-digit values', () => {
    expect(parseDuration('120m')).toBe(120 * 60 * 1000);
  });

  it('should handle leading/trailing whitespace', () => {
    expect(parseDuration('  30m  ')).toBe(30 * 60 * 1000);
  });

  it('should return null for empty string', () => {
    expect(parseDuration('')).toBeNull();
  });

  it('should return null for bare number without unit', () => {
    expect(parseDuration('30')).toBeNull();
  });

  it('should return null for unknown unit', () => {
    expect(parseDuration('5w')).toBeNull();
  });

  it('should return null for non-numeric input', () => {
    expect(parseDuration('abc')).toBeNull();
  });

  it('should return null for zero value', () => {
    expect(parseDuration('0s')).toBe(0);
  });

  it('should return null for decimal input', () => {
    expect(parseDuration('1.5h')).toBeNull();
  });
});

describe('computeNextRun', () => {
  describe('every (interval)', () => {
    it('should compute next run as now + interval', () => {
      const now = new Date('2026-01-15T12:00:00.000Z');
      const config = { type: 'every' as const, intervalMs: 60 * 60 * 1000 };
      const result = computeNextRun(config, now);
      expect(result).toBe('2026-01-15T13:00:00.000Z');
    });

    it('should compute next run for 24h interval', () => {
      const now = new Date('2026-01-15T00:00:00.000Z');
      const config = { type: 'every' as const, intervalMs: 24 * 60 * 60 * 1000 };
      const result = computeNextRun(config, now);
      expect(result).toBe('2026-01-16T00:00:00.000Z');
    });

    it('should compute next run for short interval (30m)', () => {
      const now = new Date('2026-06-01T08:30:00.000Z');
      const config = { type: 'every' as const, intervalMs: 30 * 60 * 1000 };
      const result = computeNextRun(config, now);
      expect(result).toBe('2026-06-01T09:00:00.000Z');
    });
  });

  describe('every + anchor', () => {
    it('should compute next occurrence of anchor time if in the future today', () => {
      const now = new Date();
      now.setHours(6, 0, 0, 0);
      const config = {
        type: 'every' as const,
        intervalMs: 24 * 60 * 60 * 1000,
        anchor: '09:00',
      };
      const result = computeNextRun(config, now);
      const nextDate = new Date(result);
      expect(nextDate.getHours()).toBe(9);
      expect(nextDate.getMinutes()).toBe(0);
      expect(nextDate.getDate()).toBe(now.getDate());
    });

    it('should roll to tomorrow if anchor time already passed today', () => {
      const now = new Date('2026-01-15T14:00:00.000Z');
      const config = {
        type: 'every' as const,
        intervalMs: 24 * 60 * 60 * 1000,
        anchor: '09:00',
      };
      const result = computeNextRun(config, now);
      const nextDate = new Date(result);
      expect(nextDate.getDate()).toBe(now.getDate() + 1);
    });

    it('should compute next occurrence at anchor exactly when now equals anchor', () => {
      const now = new Date('2026-01-15T09:00:00.000Z');
      const config = {
        type: 'every' as const,
        intervalMs: 24 * 60 * 60 * 1000,
        anchor: '09:00',
      };
      const result = computeNextRun(config, now);
      const nextDate = new Date(result);
      expect(nextDate.getDate()).toBe(now.getDate() + 1);
    });

    it('should handle anchor at midnight', () => {
      const now = new Date('2026-01-15T15:00:00.000Z');
      const config = {
        type: 'every' as const,
        intervalMs: 24 * 60 * 60 * 1000,
        anchor: '00:00',
      };
      const result = computeNextRun(config, now);
      const nextDate = new Date(result);
      expect(nextDate.getDate()).toBe(now.getDate() + 1);
      expect(nextDate.getHours()).toBe(0);
      expect(nextDate.getMinutes()).toBe(0);
    });
  });

  describe('cron', () => {
    it('should compute next run for every-minute cron', () => {
      const now = new Date('2026-01-15T12:30:00.000Z');
      const config = { type: 'cron' as const, expression: '* * * * *' };
      const result = computeNextRun(config, now);
      const nextDate = new Date(result);
      expect(nextDate.getMinutes()).toBe(31);
    });

    it('should compute next weekday 09:00 for 0 9 * * 1-5', () => {
      const monday = new Date('2026-01-19T00:00:00.000Z');
      const config = { type: 'cron' as const, expression: '0 9 * * 1-5' };
      const result = computeNextRun(config, monday);
      const nextDate = new Date(result);
      expect(nextDate.getDay()).toBeGreaterThanOrEqual(1);
      expect(nextDate.getDay()).toBeLessThanOrEqual(5);
      expect(nextDate.getHours()).toBe(9);
      expect(nextDate.getMinutes()).toBe(0);
    });

    it('should skip weekends for weekday cron', () => {
      const friday = new Date('2026-01-16T10:00:00.000Z');
      const config = { type: 'cron' as const, expression: '0 9 * * 1-5' };
      const result = computeNextRun(config, friday);
      const nextDate = new Date(result);
      expect(nextDate.getDay()).toBe(1);
      expect(nextDate.getDate()).toBe(19);
    });

    it('should compute next run for hourly cron', () => {
      const now = new Date('2026-03-15T14:45:00.000Z');
      const config = { type: 'cron' as const, expression: '0 * * * *' };
      const result = computeNextRun(config, now);
      const nextDate = new Date(result);
      expect(nextDate.getUTCMinutes()).toBe(0);
      expect(nextDate.getTime()).toBeGreaterThan(now.getTime());
    });

    it('should compute next run for daily cron', () => {
      const now = new Date('2026-05-20T08:00:00.000Z');
      const config = { type: 'cron' as const, expression: '30 2 * * *' };
      const result = computeNextRun(config, now);
      const nextDate = new Date(result);
      expect(nextDate.getHours()).toBe(2);
      expect(nextDate.getMinutes()).toBe(30);
      expect(nextDate.getDate()).toBe(21);
    });
  });

  describe('at (one-time)', () => {
    it('should return the timestamp as-is', () => {
      const config = {
        type: 'at' as const,
        timestamp: '2026-06-01T00:00:00.000Z',
      };
      const result = computeNextRun(config, new Date());
      expect(result).toBe('2026-06-01T00:00:00.000Z');
    });

    it('should return past timestamp without modification', () => {
      const config = {
        type: 'at' as const,
        timestamp: '2020-01-01T00:00:00.000Z',
      };
      const result = computeNextRun(config, new Date());
      expect(result).toBe('2020-01-01T00:00:00.000Z');
    });
  });
});

describe('parseScheduleConfig', () => {
  describe('every schedule', () => {
    it('should parse every schedule without anchor', () => {
      const result = parseScheduleConfig({ every: '30m' });
      expect(result).toEqual({
        type: 'every',
        intervalMs: 30 * 60 * 1000,
      });
    });

    it('should parse every schedule with anchor', () => {
      const result = parseScheduleConfig({ every: '24h', anchor: '09:00' });
      expect(result).toEqual({
        type: 'every',
        intervalMs: 24 * 60 * 60 * 1000,
        anchor: '09:00',
      });
    });

    it('should reject every with invalid duration', () => {
      expect(parseScheduleConfig({ every: 'abc' })).toBeNull();
    });

    it('should reject every with invalid anchor format', () => {
      expect(parseScheduleConfig({ every: '24h', anchor: '25:00' })).toBeNull();
    });

    it('should reject every with anchor minutes > 59', () => {
      expect(parseScheduleConfig({ every: '1h', anchor: '10:60' })).toBeNull();
    });
  });

  describe('cron schedule', () => {
    it('should parse valid cron expression', () => {
      const result = parseScheduleConfig({ cron: '0 9 * * 1-5' });
      expect(result).toEqual({
        type: 'cron',
        expression: '0 9 * * 1-5',
      });
    });

    it('should reject invalid cron expression', () => {
      expect(parseScheduleConfig({ cron: 'not-a-cron' })).toBeNull();
    });

    it('should parse every-minute cron', () => {
      const result = parseScheduleConfig({ cron: '* * * * *' });
      expect(result).toEqual({ type: 'cron', expression: '* * * * *' });
    });
  });

  describe('at schedule', () => {
    it('should parse valid ISO timestamp', () => {
      const result = parseScheduleConfig({ at: '2026-06-01T00:00:00.000Z' });
      expect(result).toEqual({
        type: 'at',
        timestamp: '2026-06-01T00:00:00.000Z',
      });
    });

    it('should reject invalid timestamp', () => {
      expect(parseScheduleConfig({ at: 'not-a-date' })).toBeNull();
    });
  });

  describe('validation', () => {
    it('should return null for null input', () => {
      expect(parseScheduleConfig(null)).toBeNull();
    });

    it('should return null for undefined input', () => {
      expect(parseScheduleConfig(undefined)).toBeNull();
    });

    it('should return null for empty object', () => {
      expect(parseScheduleConfig({})).toBeNull();
    });

    it('should return null when multiple schedule types provided', () => {
      expect(parseScheduleConfig({ every: '30m', cron: '* * * * *' })).toBeNull();
    });

    it('should return null when all types provided', () => {
      expect(
        parseScheduleConfig({
          every: '30m',
          cron: '* * * * *',
          at: '2026-01-01',
        }),
      ).toBeNull();
    });

    it('should return null for non-object input', () => {
      expect(parseScheduleConfig('every: 30m')).toBeNull();
    });

    it('should return null for array input', () => {
      expect(parseScheduleConfig(['every', '30m'])).toBeNull();
    });
  });
});
