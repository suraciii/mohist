import { Log } from '../util/log';
import { ScheduleRepo, type SkillSchedule } from '../db/schedule-repo';
import type { EventBus } from './event-bus';
import { computeNextRun, parseDuration, type ScheduleConfig } from './schedule-parser';

const log = Log.create({ service: 'scheduler-service' });

const MAX_TIMER_DELAY_MS = 60 * 1000;
const DEFAULT_MAX_CONCURRENT_RUNS = 3;

export interface SkillRunner {
  runSkill(skillName: string): Promise<void>;
}

export class SchedulerService {
  private timers = new Map<string, NodeJS.Timeout>();
  private activeRuns = 0;
  private queue: Array<{ schedule: SkillSchedule; resolve: () => void }> = [];

  constructor(
    private readonly scheduleRepo: ScheduleRepo,
    private readonly skillRunner: SkillRunner,
    private readonly eventBus: EventBus,
    private readonly maxConcurrentRuns: number = DEFAULT_MAX_CONCURRENT_RUNS,
  ) {}

  start(): void {
    const schedules = this.scheduleRepo.getAllEnabled();
    this.recover(schedules);
    log.info('Scheduler started', { scheduleCount: schedules.length });
  }

  recover(schedules: SkillSchedule[]): void {
    const now = Date.now();
    let catchUpCount = 0;

    for (const schedule of schedules) {
      const nextRunAt = new Date(schedule.nextRunAt).getTime();

      if (nextRunAt <= now) {
        const overdueMs = now - nextRunAt;
        log.info('Catch-up execution for overdue schedule', {
          skillId: schedule.skillId,
          scheduleType: schedule.scheduleType,
          overdueMs,
        });

        this.executeCatchUp(schedule);
        catchUpCount++;
      } else {
        this.armTimer(schedule);
      }
    }

    if (catchUpCount > 0) {
      log.info('Recovery complete', { catchUpCount, totalSchedules: schedules.length });
    }
  }

  stop(): void {
    for (const [, timer] of this.timers) {
      clearTimeout(timer);
    }
    this.timers.clear();

    for (const entry of this.queue) {
      entry.resolve();
    }
    this.queue.length = 0;

    log.info('Scheduler stopped');
  }

  refreshSchedule(skillId: string): void {
    const schedule = this.scheduleRepo.getBySkillId(skillId);
    if (schedule && schedule.enabled) {
      this.armTimer(schedule);
    } else {
      this.clearTimer(schedule?.id ?? skillId);
    }
  }

  private armTimer(schedule: SkillSchedule): void {
    this.clearTimer(schedule.id);

    const delay = new Date(schedule.nextRunAt).getTime() - Date.now();
    const clampedDelay = Math.max(0, Math.min(delay, MAX_TIMER_DELAY_MS));

    const timer = setTimeout(() => {
      this.onTimerFire(schedule.id);
    }, clampedDelay);

    this.timers.set(schedule.id, timer);
  }

  private clearTimer(scheduleId: string): void {
    const timer = this.timers.get(scheduleId);
    if (timer != null) {
      clearTimeout(timer);
      this.timers.delete(scheduleId);
    }
  }

  private onTimerFire(scheduleId: string): void {
    this.timers.delete(scheduleId);

    const schedule = this.scheduleRepo.getAllEnabled().find(s => s.id === scheduleId);
    if (!schedule) return;

    const now = Date.now();
    const nextRunAt = new Date(schedule.nextRunAt).getTime();
    if (nextRunAt > now) {
      this.armTimer(schedule);
      return;
    }

    this.tryExecute(schedule);
  }

  private async tryExecute(schedule: SkillSchedule): Promise<void> {
    if (this.activeRuns >= this.maxConcurrentRuns) {
      log.info('Schedule queued (concurrent limit reached)', { skillId: schedule.skillId });
      await new Promise<void>(resolve => {
        this.queue.push({ schedule, resolve });
      });
    }

    this.activeRuns++;
    try {
      await this.executeSchedule(schedule);
    } finally {
      this.activeRuns--;
      this.processQueue();
    }
  }

  private async executeSchedule(schedule: SkillSchedule): Promise<void> {
    const triggeredAt = new Date().toISOString();

    this.eventBus.emit('schedule_triggered' as any, {
      scheduleId: schedule.id,
      skillId: schedule.skillId,
      scheduledAt: schedule.nextRunAt,
      triggeredAt,
    } as any);

    let failed = false;
    try {
      await this.skillRunner.runSkill(schedule.skillId);
    } catch (err) {
      failed = true;
      log.error('Scheduled execution failed', { skillId: schedule.skillId, error: String(err) });

      this.eventBus.emit('schedule_failed' as any, {
        scheduleId: schedule.id,
        skillId: schedule.skillId,
        triggeredAt,
        failedAt: new Date().toISOString(),
        error: String(err),
      } as any);
    }

    if (!failed) {
      this.eventBus.emit('schedule_completed' as any, {
        scheduleId: schedule.id,
        skillId: schedule.skillId,
        triggeredAt,
        completedAt: new Date().toISOString(),
      } as any);
    }

    const completedAt = new Date().toISOString();

    if (schedule.scheduleType === 'at') {
      this.scheduleRepo.setEnabled(schedule.id, false);
      this.scheduleRepo.updateNextRun(schedule.id, schedule.nextRunAt, completedAt);
      this.clearTimer(schedule.id);
      return;
    }

    const config = this.reconstructConfig(schedule);
    const nextRun = computeNextRun(config);
    this.scheduleRepo.updateNextRun(schedule.id, nextRun, completedAt);

    const updated = this.scheduleRepo.getBySkillId(schedule.skillId);
    if (updated && updated.enabled) {
      this.armTimer(updated);
    }
  }

  private executeCatchUp(schedule: SkillSchedule): void {
    this.tryExecute(schedule);
  }

  private processQueue(): void {
    while (this.queue.length > 0 && this.activeRuns < this.maxConcurrentRuns) {
      const entry = this.queue.shift()!;
      entry.resolve();
    }
  }

  private reconstructConfig(schedule: SkillSchedule): ScheduleConfig {
    switch (schedule.scheduleType) {
      case 'every': {
        const intervalMs = parseDuration(schedule.scheduleValue);
        const base: ScheduleConfig = intervalMs != null
          ? { type: 'every', intervalMs }
          : { type: 'every', intervalMs: 60 * 60 * 1000 };
        if (schedule.anchor) {
          return { ...base, type: 'every', anchor: schedule.anchor } as ScheduleConfig;
        }
        return base;
      }
      case 'cron':
        return { type: 'cron', expression: schedule.scheduleValue };
      case 'at':
        return { type: 'at', timestamp: schedule.scheduleValue };
      default:
        return { type: 'every', intervalMs: 60 * 60 * 1000 };
    }
  }
}
