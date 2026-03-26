import { DatabaseManager } from './database';

interface ConfigRow {
  key: string;
  value: string;
}

export class ConfigRepo {
  constructor(private db: DatabaseManager) {}

  get(key: string): string | null {
    const row = this.db.get<ConfigRow>(
      'SELECT value FROM config WHERE key = ?',
      [key]
    );
    return row?.value ?? null;
  }

  getNumber(key: string, defaultValue: number): number {
    const value = this.get(key);
    if (value === null) return defaultValue;
    const parsed = parseInt(value, 10);
    return isNaN(parsed) ? defaultValue : parsed;
  }

  getBoolean(key: string, defaultValue: boolean): boolean {
    const value = this.get(key);
    if (value === null) return defaultValue;
    return value === 'true';
  }

  set(key: string, value: string | number | boolean): void {
    this.db.run(
      'INSERT OR REPLACE INTO config (key, value) VALUES (?, ?)',
      [key, String(value)]
    );
  }

  delete(key: string): boolean {
    const result = this.db.run('DELETE FROM config WHERE key = ?', [key]);
    return result.changes > 0;
  }

  getAll(): Record<string, string> {
    const rows = this.db.all<ConfigRow>('SELECT key, value FROM config');
    const config: Record<string, string> = {};
    for (const row of rows) {
      config[row.key] = row.value;
    }
    return config;
  }

  getMultiple(keys: string[]): Record<string, string | null> {
    const placeholders = keys.map(() => '?').join(', ');
    const rows = this.db.all<ConfigRow>(
      `SELECT key, value FROM config WHERE key IN (${placeholders})`,
      keys
    );
    
    const result: Record<string, string | null> = {};
    for (const key of keys) {
      result[key] = null;
    }
    for (const row of rows) {
      result[row.key] = row.value;
    }
    return result;
  }

  setMultiple(config: Record<string, string | number | boolean>): void {
    this.db.transaction(() => {
      for (const [key, value] of Object.entries(config)) {
        this.set(key, value);
      }
    });
  }

  exists(key: string): boolean {
    const row = this.db.get<{ count: number }>(
      'SELECT COUNT(*) as count FROM config WHERE key = ?',
      [key]
    );
    return (row?.count || 0) > 0;
  }

  clear(): void {
    this.db.run("DELETE FROM config WHERE key != 'schema_version'");
  }
}

export const DEFAULT_CONFIG = {
  'server.port': '3456',
  'agent.timeout': '1800000',  // 30 minutes in ms
  'agent.maxConcurrent': '8',
  'poll.interval': '30000',  // 30 seconds
};

export function initializeDefaultConfig(configRepo: ConfigRepo): void {
  for (const [key, value] of Object.entries(DEFAULT_CONFIG)) {
    if (!configRepo.exists(key)) {
      configRepo.set(key, value);
    }
  }
}
