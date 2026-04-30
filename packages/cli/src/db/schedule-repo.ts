import { v4 as uuidv4 } from 'uuid';
import { DatabaseManager } from './database';

export interface SkillSchedule {
  id: string;
  skillId: string;
  scheduleType: string;
  scheduleValue: string;
  anchor: string | null;
  nextRunAt: string;
  lastRunAt: string | null;
  enabled: boolean;
  createdAt: string;
  updatedAt: string;
}

export interface CreateScheduleData {
  skillId: string;
  scheduleType: string;
  scheduleValue: string;
  anchor?: string;
  nextRunAt: string;
}

interface ScheduleRow {
  id: string;
  skill_id: string;
  schedule_type: string;
  schedule_value: string;
  anchor: string | null;
  next_run_at: string;
  last_run_at: string | null;
  enabled: number;
  created_at: string;
  updated_at: string;
}

function rowToSchedule(row: ScheduleRow): SkillSchedule {
  return {
    id: row.id,
    skillId: row.skill_id,
    scheduleType: row.schedule_type,
    scheduleValue: row.schedule_value,
    anchor: row.anchor,
    nextRunAt: row.next_run_at,
    lastRunAt: row.last_run_at,
    enabled: row.enabled === 1,
    createdAt: row.created_at,
    updatedAt: row.updated_at,
  };
}

export class ScheduleRepo {
  constructor(private db: DatabaseManager) {}

  upsert(data: CreateScheduleData): SkillSchedule {
    const existing = this.db.get<ScheduleRow>(
      'SELECT * FROM agent_skill_schedules WHERE skill_id = ?',
      [data.skillId]
    );

    if (existing) {
      const now = new Date().toISOString();
      this.db.run(
        `UPDATE agent_skill_schedules
         SET schedule_type = ?, schedule_value = ?, anchor = ?, next_run_at = ?, updated_at = ?
         WHERE skill_id = ?`,
        [data.scheduleType, data.scheduleValue, data.anchor ?? null, data.nextRunAt, now, data.skillId]
      );
      return rowToSchedule({ ...existing, schedule_type: data.scheduleType, schedule_value: data.scheduleValue, anchor: data.anchor ?? null, next_run_at: data.nextRunAt, updated_at: now });
    }

    const id = uuidv4();
    const now = new Date().toISOString();
    this.db.run(
      `INSERT INTO agent_skill_schedules (id, skill_id, schedule_type, schedule_value, anchor, next_run_at, last_run_at, enabled, created_at, updated_at)
       VALUES (?, ?, ?, ?, ?, ?, NULL, 1, ?, ?)`,
      [id, data.skillId, data.scheduleType, data.scheduleValue, data.anchor ?? null, data.nextRunAt, now, now]
    );

    return {
      id,
      skillId: data.skillId,
      scheduleType: data.scheduleType,
      scheduleValue: data.scheduleValue,
      anchor: data.anchor ?? null,
      nextRunAt: data.nextRunAt,
      lastRunAt: null,
      enabled: true,
      createdAt: now,
      updatedAt: now,
    };
  }

  getBySkillId(skillId: string): SkillSchedule | null {
    const row = this.db.get<ScheduleRow>(
      'SELECT * FROM agent_skill_schedules WHERE skill_id = ?',
      [skillId]
    );
    return row ? rowToSchedule(row) : null;
  }

  getAll(): SkillSchedule[] {
    const rows = this.db.all<ScheduleRow>(
      'SELECT * FROM agent_skill_schedules ORDER BY next_run_at ASC'
    );
    return rows.map(rowToSchedule);
  }

  getAllEnabled(): SkillSchedule[] {
    const rows = this.db.all<ScheduleRow>(
      'SELECT * FROM agent_skill_schedules WHERE enabled = 1 ORDER BY next_run_at ASC'
    );
    return rows.map(rowToSchedule);
  }

  updateNextRun(id: string, nextRunAt: string, lastRunAt: string): void {
    const now = new Date().toISOString();
    this.db.run(
      `UPDATE agent_skill_schedules SET next_run_at = ?, last_run_at = ?, updated_at = ? WHERE id = ?`,
      [nextRunAt, lastRunAt, now, id]
    );
  }

  setEnabled(id: string, enabled: boolean): void {
    const now = new Date().toISOString();
    this.db.run(
      'UPDATE agent_skill_schedules SET enabled = ?, updated_at = ? WHERE id = ?',
      [enabled ? 1 : 0, now, id]
    );
  }

  deleteBySkillId(skillId: string): void {
    this.db.run(
      'DELETE FROM agent_skill_schedules WHERE skill_id = ?',
      [skillId]
    );
  }
}
