import { DatabaseManager } from './database';

const SCHEMA_VERSION = 3;

const CREATE_PROJECTS_TABLE = `
CREATE TABLE IF NOT EXISTS projects (
  id          TEXT PRIMARY KEY,
  name        TEXT UNIQUE NOT NULL,
  path        TEXT NOT NULL,
  created_at  TEXT NOT NULL,
  updated_at  TEXT NOT NULL
);
`;

const CREATE_ISSUES_TABLE = `
CREATE TABLE IF NOT EXISTS issues (
  id          TEXT PRIMARY KEY,
  number      INTEGER NOT NULL,
  project_id  TEXT NOT NULL REFERENCES projects(id),
  title       TEXT NOT NULL,
  body        TEXT,
  stage       TEXT NOT NULL DEFAULT 'draft',
  status      TEXT NOT NULL DEFAULT 'active',
  created_at  TEXT NOT NULL,
  updated_at  TEXT NOT NULL,
  UNIQUE(project_id, number)
);
`;

const CREATE_INDEXES = [
  'CREATE INDEX IF NOT EXISTS idx_issues_project_stage ON issues(project_id, stage);',
  'CREATE INDEX IF NOT EXISTS idx_issues_project_status ON issues(project_id, status);',
];

const CREATE_CONFIG_TABLE = `
CREATE TABLE IF NOT EXISTS config (
  key   TEXT PRIMARY KEY,
  value TEXT NOT NULL
);
`;

const CREATE_COMMENTS_TABLE = `
CREATE TABLE IF NOT EXISTS comments (
  id          TEXT PRIMARY KEY,
  issue_id    TEXT NOT NULL REFERENCES issues(id) ON DELETE CASCADE,
  body        TEXT NOT NULL,
  created_at  TEXT NOT NULL
);
`;

const CREATE_COMMENTS_INDEXES = [
  'CREATE INDEX IF NOT EXISTS idx_comments_issue_id ON comments(issue_id);',
];

export function runMigrations(db: DatabaseManager): void {
  db.transaction(() => {
    db.exec(CREATE_PROJECTS_TABLE);
    db.exec(CREATE_ISSUES_TABLE);
    db.exec(CREATE_CONFIG_TABLE);
    
    for (const indexSql of CREATE_INDEXES) {
      db.exec(indexSql);
    }
    
    setSchemaVersion(db, SCHEMA_VERSION);
  });
}

function setSchemaVersion(db: DatabaseManager, version: number): void {
  db.run(
    'INSERT OR REPLACE INTO config (key, value) VALUES (?, ?)',
    ['schema_version', String(version)]
  );
}

export function getSchemaVersion(db: DatabaseManager): number {
  try {
    const row = db.get<{ value: string }>(
      'SELECT value FROM config WHERE key = ?',
      ['schema_version']
    );
    return row ? parseInt(row.value, 10) : 0;
  } catch {
    return 0;
  }
}

export function initializeDatabase(db: DatabaseManager): void {
  const currentVersion = getSchemaVersion(db);
  
  if (currentVersion === 0) {
    runMigrations(db);
  }
  
  if (currentVersion < 2) {
    migrateToVersion2(db);
  }
  
  if (currentVersion < 3) {
    migrateToVersion3(db);
  }
}

function migrateToVersion2(db: DatabaseManager): void {
  db.transaction(() => {
    const tableInfo = db.all<{ name: string }>(
      "PRAGMA table_info(issues)"
    );
    const hasLabels = tableInfo.some(col => col.name === 'labels');
    
    if (!hasLabels) {
      db.exec("ALTER TABLE issues ADD COLUMN labels TEXT DEFAULT '[]'");
    }
    
    db.exec(CREATE_COMMENTS_TABLE);
    
    for (const indexSql of CREATE_COMMENTS_INDEXES) {
      db.exec(indexSql);
    }
    
    setSchemaVersion(db, 2);
  });
}

function migrateToVersion3(db: DatabaseManager): void {
  db.transaction(() => {
    db.exec('DROP TABLE IF EXISTS tasks');
    db.exec('DROP INDEX IF EXISTS idx_tasks_project_status');
    setSchemaVersion(db, 3);
  });
}
