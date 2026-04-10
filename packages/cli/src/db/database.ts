import Database from 'better-sqlite3';
import path from 'path';
import fs from 'fs';
import os from 'os';

export type SqlValue = string | number | boolean | null | Buffer;

export interface DatabaseConfig {
  dbPath?: string;
  inMemory?: boolean;
}

const DEFAULT_DB_DIR = path.join(os.homedir(), '.mohist');
const DEFAULT_DB_NAME = 'mohist.db';

export class DatabaseManager {
  private db: Database.Database;
  private dbPath: string;

  constructor(config: DatabaseConfig = {}) {
    if (config.inMemory) {
      this.dbPath = ':memory:';
      this.db = new Database(':memory:');
    } else {
      this.dbPath = config.dbPath || path.join(DEFAULT_DB_DIR, DEFAULT_DB_NAME);
      this.ensureDirectoryExists();
      this.db = new Database(this.dbPath);
    }
    
    this.db.pragma('journal_mode = WAL');
    this.db.pragma('foreign_keys = ON');
    this.db.pragma('busy_timeout = 5000');
  }

  private ensureDirectoryExists(): void {
    const dir = path.dirname(this.dbPath);
    if (!fs.existsSync(dir)) {
      fs.mkdirSync(dir, { recursive: true });
    }
  }

  run(sql: string, params: SqlValue[] = []): Database.RunResult {
    const stmt = this.db.prepare(sql);
    return stmt.run(...params);
  }

  get<T = unknown>(sql: string, params: SqlValue[] = []): T | undefined {
    const stmt = this.db.prepare(sql);
    return stmt.get(...params) as T | undefined;
  }

  all<T = unknown>(sql: string, params: SqlValue[] = []): T[] {
    const stmt = this.db.prepare(sql);
    return stmt.all(...params) as T[];
  }

  transaction<T>(fn: () => T): T {
    return this.db.transaction(fn)();
  }

  prepare(sql: string): Database.Statement {
    return this.db.prepare(sql);
  }

  exec(sql: string): void {
    this.db.exec(sql);
  }

  close(): void {
    this.db.close();
  }

  getDbPath(): string {
    return this.dbPath;
  }

  getRawDb(): Database.Database {
    return this.db;
  }
}


