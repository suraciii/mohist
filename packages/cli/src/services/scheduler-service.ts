import { Log } from '../util/log';
import { ScheduleRepo, type SkillSchedule, type CreateScheduleData } from '../db/schedule-repo';
import type { EventBus } from './event-bus';
import { computeNextRun, parseDuration, parseScheduleConfig, type ScheduleConfig } from './schedule-parser';
import * as fs from 'node:fs';
import * as path from 'node:path';
import * as os from 'node:os';
import * as yaml from 'yaml';

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

  enableSchedule(skillId: string): SkillSchedule | null {
    const schedule = this.scheduleRepo.getBySkillId(skillId);
    if (!schedule) return null;

    this.scheduleRepo.setEnabled(schedule.id, true);

    const config = this.reconstructConfig(schedule);
    const nextRunAt = computeNextRun(config);
    const lastRunAt = schedule.lastRunAt ?? new Date().toISOString();
    this.scheduleRepo.updateNextRun(schedule.id, nextRunAt, lastRunAt);

    const updated = this.scheduleRepo.getBySkillId(skillId);
    if (updated) {
      this.armTimer(updated);
    }
    return updated;
  }

  disableSchedule(skillId: string): SkillSchedule | null {
    const schedule = this.scheduleRepo.getBySkillId(skillId);
    if (!schedule) return null;

    this.clearTimer(schedule.id);
    this.scheduleRepo.setEnabled(schedule.id, false);
    return this.scheduleRepo.getBySkillId(skillId);
  }

  refreshSchedules(projectPath?: string): { created: number; updated: number; removed: number } {
    const scanDirs = this.getSkillScanDirs(projectPath);
    const discovered = new Map<string, { every?: string; cron?: string; at?: string; anchor?: string }>();

    for (const dir of scanDirs) {
      if (!fs.existsSync(dir)) continue;
      const entries = fs.readdirSync(dir, { withFileTypes: true });
      for (const entry of entries) {
        if (!entry.isDirectory()) continue;
        const skillFile = path.join(dir, entry.name, 'SKILL.md');
        if (!fs.existsSync(skillFile)) continue;
        const parsed = this.parseScheduleFromSkillFile(skillFile);
        if (parsed) {
          discovered.set(entry.name, parsed);
        }
      }
    }

    const existing = this.scheduleRepo.getAll();
    const existingBySkillId = new Map(existing.map(s => [s.skillId, s]));

    let created = 0;
    let updated = 0;
    let removed = 0;

    for (const [skillId, rawSchedule] of discovered) {
      const config = parseScheduleConfig(rawSchedule);
      if (!config) continue;

      const nextRunAt = computeNextRun(config);
      const existingSchedule = existingBySkillId.get(skillId);

      const scheduleValue = config.type === 'every'
        ? rawSchedule.every!
        : config.type === 'cron'
          ? config.expression
          : config.timestamp;

      const data: CreateScheduleData = {
        skillId,
        scheduleType: config.type,
        scheduleValue,
        anchor: config.type === 'every' && 'anchor' in config ? (config as ScheduleConfig & { anchor: string }).anchor : undefined,
        nextRunAt,
      };

      if (!existingSchedule) {
        this.scheduleRepo.upsert(data);
        created++;
      } else {
        const changed = existingSchedule.scheduleType !== data.scheduleType
          || existingSchedule.scheduleValue !== data.scheduleValue
          || existingSchedule.anchor !== (data.anchor ?? null);
        if (changed) {
          this.scheduleRepo.upsert(data);
          updated++;
        }
      }

      this.refreshSchedule(skillId);
      existingBySkillId.delete(skillId);
    }

    for (const [skillId, schedule] of existingBySkillId) {
      this.clearTimer(schedule.id);
      this.scheduleRepo.deleteBySkillId(skillId);
      removed++;
    }

    log.info('Schedules refreshed', { created, updated, removed, total: discovered.size });
    return { created, updated, removed };
  }

  private getSkillScanDirs(projectPath?: string): string[] {
    const dirs: string[] = [];
    if (projectPath) {
      dirs.push(path.join(projectPath, '.opencode', 'skills'));
    }
    dirs.push(path.join(os.homedir(), '.config', 'opencode', 'skills'));
    return dirs;
  }

  private parseScheduleFromSkillFile(filePath: string): { every?: string; cron?: string; at?: string; anchor?: string } | null {
    try {
      const content = fs.readFileSync(filePath, 'utf-8');
      const frontmatterMatch = /^---\s*\n([\s\S]*?)\n---/.exec(content);
      if (!frontmatterMatch) return null;
      const parsed = yaml.parse(frontmatterMatch[1]);
      if (!parsed || typeof parsed !== 'object') return null;
      const schedule = (parsed as any).mohist?.schedule ?? (parsed as any).schedule;
      if (!schedule || typeof schedule !== 'object') return null;
      return schedule as { every?: string; cron?: string; at?: string; anchor?: string };
    } catch {
      return null;
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
