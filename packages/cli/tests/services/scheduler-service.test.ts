import { describe, it, expect, beforeEach, afterEach, vi, type Mock } from 'vitest';
import { DatabaseManager } from '../../src/db/database';
import { initializeDatabase } from '../../src/db/migrations';
import { ScheduleRepo } from '../../src/db/schedule-repo';
import { SchedulerService, type SkillRunner } from '../../src/services/scheduler-service';
import { EventBus } from '../../src/services/event-bus';

function ensureAgentSkillsTable(db: DatabaseManager): void {
  db.exec(`
    CREATE TABLE IF NOT EXISTS agent_skills (
      id TEXT PRIMARY KEY,
      name TEXT NOT NULL,
      description TEXT,
      prompt TEXT,
      created_at TEXT NOT NULL,
      updated_at TEXT NOT NULL
    )
  `);
}

function insertTestSkill(db: DatabaseManager, skillId: string): void {
  const now = new Date().toISOString();
  db.run(
    `INSERT OR IGNORE INTO agent_skills (id, name, description, prompt, created_at, updated_at)
     VALUES (?, ?, 'test', 'test', ?, ?)`,
    [skillId, skillId, now, now],
  );
}

async function flushAsync(): Promise<void> {
  for (let i = 0; i < 10; i++) {
    await Promise.resolve();
  }
}

describe('SchedulerService', () => {
  let db: DatabaseManager;
  let scheduleRepo: ScheduleRepo;
  let eventBus: EventBus;
  let runSkillMock: Mock;
  let skillRunner: SkillRunner;

  beforeEach(() => {
    db = new DatabaseManager({ inMemory: true });
    initializeDatabase(db);
    ensureAgentSkillsTable(db);
    scheduleRepo = new ScheduleRepo(db);
    eventBus = new EventBus();
    runSkillMock = vi.fn().mockResolvedValue(undefined);
    skillRunner = { runSkill: runSkillMock };
  });

  afterEach(() => {
    db.close();
    vi.useRealTimers();
  });

  describe('start()', () => {
    it('should complete without error when no schedules exist', () => {
      const service = new SchedulerService(scheduleRepo, skillRunner, eventBus);
      expect(() => service.start()).not.toThrow();
      expect(runSkillMock).not.toHaveBeenCalled();
    });

    it('should trigger catch-up execution for overdue schedule on start', async () => {
      vi.useFakeTimers();
      vi.setSystemTime(new Date('2026-01-15T12:00:00.000Z'));

      insertTestSkill(db, 'audit-skill');
      scheduleRepo.upsert({
        skillId: 'audit-skill',
        scheduleType: 'every',
        scheduleValue: '1h',
        nextRunAt: new Date('2026-01-15T11:00:00.000Z').toISOString(),
      });

      const service = new SchedulerService(scheduleRepo, skillRunner, eventBus);
      service.start();

      await flushAsync();

      expect(runSkillMock).toHaveBeenCalledWith('audit-skill');
    });
  });

  describe('timer-based execution', () => {
    it('should call SkillRunner.runSkill when timer fires', async () => {
      vi.useFakeTimers();
      vi.setSystemTime(new Date('2026-01-15T12:00:00.000Z'));

      insertTestSkill(db, 'cron-skill');
      scheduleRepo.upsert({
        skillId: 'cron-skill',
        scheduleType: 'every',
        scheduleValue: '1h',
        nextRunAt: new Date('2026-01-15T12:00:05.000Z').toISOString(),
      });

      const service = new SchedulerService(scheduleRepo, skillRunner, eventBus);
      service.start();

      expect(runSkillMock).not.toHaveBeenCalled();

      vi.advanceTimersByTime(5000);
      await flushAsync();

      expect(runSkillMock).toHaveBeenCalledWith('cron-skill');
    });

    it('should recompute and persist next_run_at after execution', async () => {
      vi.useFakeTimers();
      vi.setSystemTime(new Date('2026-01-15T12:00:00.000Z'));

      insertTestSkill(db, 'hourly-skill');
      scheduleRepo.upsert({
        skillId: 'hourly-skill',
        scheduleType: 'every',
        scheduleValue: '1h',
        nextRunAt: new Date('2026-01-15T11:00:00.000Z').toISOString(),
      });

      const service = new SchedulerService(scheduleRepo, skillRunner, eventBus);
      service.start();

      await flushAsync();

      const updated = scheduleRepo.getBySkillId('hourly-skill');
      expect(updated).not.toBeNull();
      expect(updated!.enabled).toBe(true);

      const expectedNextRun = new Date('2026-01-15T13:00:00.000Z').toISOString();
      expect(updated!.nextRunAt).toBe(expectedNextRun);
    });
  });

  describe('one-shot schedules', () => {
    it('should disable one-shot (at) schedule after firing', async () => {
      vi.useFakeTimers();
      vi.setSystemTime(new Date('2026-01-15T12:00:00.000Z'));

      insertTestSkill(db, 'oneshot-skill');
      const pastTimestamp = new Date('2026-01-15T11:00:00.000Z').toISOString();
      scheduleRepo.upsert({
        skillId: 'oneshot-skill',
        scheduleType: 'at',
        scheduleValue: pastTimestamp,
        nextRunAt: pastTimestamp,
      });

      const service = new SchedulerService(scheduleRepo, skillRunner, eventBus);
      service.start();

      await flushAsync();

      expect(runSkillMock).toHaveBeenCalledWith('oneshot-skill');

      const updated = scheduleRepo.getBySkillId('oneshot-skill');
      expect(updated).not.toBeNull();
      expect(updated!.enabled).toBe(false);
    });
  });

  describe('failure handling', () => {
    it('should still schedule next run after execution failure', async () => {
      vi.useFakeTimers();
      vi.setSystemTime(new Date('2026-01-15T12:00:00.000Z'));

      insertTestSkill(db, 'flaky-skill');
      runSkillMock.mockRejectedValue(new Error('skill execution failed'));

      scheduleRepo.upsert({
        skillId: 'flaky-skill',
        scheduleType: 'every',
        scheduleValue: '30m',
        nextRunAt: new Date('2026-01-15T11:30:00.000Z').toISOString(),
      });

      const events: string[] = [];
      eventBus.on('schedule_failed' as any, () => events.push('failed'));

      const service = new SchedulerService(scheduleRepo, skillRunner, eventBus);
      service.start();

      await flushAsync();

      expect(runSkillMock).toHaveBeenCalledWith('flaky-skill');

      const updated = scheduleRepo.getBySkillId('flaky-skill');
      expect(updated).not.toBeNull();
      expect(updated!.enabled).toBe(true);
      expect(updated!.lastRunAt).not.toBeNull();

      const expectedNextRun = new Date('2026-01-15T12:30:00.000Z').toISOString();
      expect(updated!.nextRunAt).toBe(expectedNextRun);
    });
  });

  describe('concurrency', () => {
    it('should respect maxConcurrentRuns limit', async () => {
      vi.useFakeTimers();
      vi.setSystemTime(new Date('2026-01-15T12:00:00.000Z'));

      insertTestSkill(db, 'skill-a');
      insertTestSkill(db, 'skill-b');

      const past = new Date('2026-01-15T11:00:00.000Z').toISOString();
      scheduleRepo.upsert({
        skillId: 'skill-a',
        scheduleType: 'every',
        scheduleValue: '1h',
        nextRunAt: past,
      });
      scheduleRepo.upsert({
        skillId: 'skill-b',
        scheduleType: 'every',
        scheduleValue: '1h',
        nextRunAt: past,
      });

      let resolveA: () => void;
      const promiseA = new Promise<void>(r => {
        resolveA = r;
      });
      const executionLog: string[] = [];

      runSkillMock.mockImplementation(async (skillId: string) => {
        executionLog.push(`start:${skillId}`);
        if (skillId === 'skill-a') {
          await promiseA;
        }
        executionLog.push(`end:${skillId}`);
      });

      const service = new SchedulerService(
        scheduleRepo,
        skillRunner,
        eventBus,
        1,
      );
      service.start();

      await flushAsync();

      expect(executionLog).toEqual(['start:skill-a']);

      resolveA!();
      await flushAsync();

      expect(executionLog).toContain('end:skill-a');
      expect(executionLog).toContain('start:skill-b');
      expect(executionLog).toContain('end:skill-b');
    });
  });

  describe('stop()', () => {
    it('should clear all active timers', async () => {
      vi.useFakeTimers();
      vi.setSystemTime(new Date('2026-01-15T12:00:00.000Z'));

      insertTestSkill(db, 'timer-skill');
      scheduleRepo.upsert({
        skillId: 'timer-skill',
        scheduleType: 'every',
        scheduleValue: '1h',
        nextRunAt: new Date('2026-01-15T12:00:05.000Z').toISOString(),
      });

      const service = new SchedulerService(scheduleRepo, skillRunner, eventBus);
      service.start();

      service.stop();

      vi.advanceTimersByTime(120000);
      await flushAsync();

      expect(runSkillMock).not.toHaveBeenCalled();
    });
  });
});
