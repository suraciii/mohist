import { execFileSync } from 'child_process';
import * as fs from 'fs';
import { DatabaseManager } from './database';

const SCHEMA_VERSION = 10;

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
  
  if (currentVersion < 4) {
    migrateToVersion4(db);
  }
  
  if (currentVersion < 5) {
    migrateToVersion5(db);
  }
  
  if (currentVersion < 6) {
    migrateToVersion6(db);
  }

  if (currentVersion < 7) {
    migrateToVersion7(db);
  }

  if (currentVersion < 8) {
    migrateToVersion8(db);
  }

  if (currentVersion < 9) {
    migrateToVersion9(db);
  }

  if (currentVersion < 10) {
    migrateToVersion10(db);
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

function migrateToVersion4(db: DatabaseManager): void {
  db.transaction(() => {
    db.run("UPDATE issues SET stage = 'plan' WHERE stage = 'designing'");
    db.run("UPDATE issues SET stage = 'build' WHERE stage = 'implementing'");
    setSchemaVersion(db, 4);
  });
}

const CREATE_WORKFLOW_LOG_TABLE = `
CREATE TABLE IF NOT EXISTS workflow_log (
  id          TEXT PRIMARY KEY,
  issue_id    TEXT NOT NULL REFERENCES issues(id) ON DELETE CASCADE,
  session_id  TEXT,
  event_type  TEXT NOT NULL,
  data        TEXT NOT NULL DEFAULT '{}',
  created_at  TEXT NOT NULL DEFAULT (datetime('now'))
);
`;

const CREATE_WORKFLOW_LOG_INDEXES = [
  'CREATE INDEX IF NOT EXISTS idx_workflow_log_issue_created ON workflow_log(issue_id, created_at);',
  'CREATE INDEX IF NOT EXISTS idx_workflow_log_issue_event ON workflow_log(issue_id, event_type);',
];

function migrateToVersion5(db: DatabaseManager): void {
  db.transaction(() => {
    db.exec(CREATE_WORKFLOW_LOG_TABLE);
    for (const indexSql of CREATE_WORKFLOW_LOG_INDEXES) {
      db.exec(indexSql);
    }
    setSchemaVersion(db, 5);
  });
}

const CREATE_QUESTIONS_TABLE = `
CREATE TABLE IF NOT EXISTS questions (
  id          TEXT PRIMARY KEY,
  issue_id    TEXT NOT NULL REFERENCES issues(id) ON DELETE CASCADE,
  question    TEXT NOT NULL,
  answer      TEXT,
  status      TEXT NOT NULL DEFAULT 'pending',
  created_at  TEXT NOT NULL,
  answered_at TEXT
);
`;

const CREATE_QUESTIONS_INDEXES = [
  'CREATE INDEX IF NOT EXISTS idx_questions_issue_id ON questions(issue_id);',
  'CREATE INDEX IF NOT EXISTS idx_questions_status ON questions(status);',
];

function migrateToVersion6(db: DatabaseManager): void {
  db.transaction(() => {
    db.exec(CREATE_QUESTIONS_TABLE);
    for (const indexSql of CREATE_QUESTIONS_INDEXES) {
      db.exec(indexSql);
    }
    setSchemaVersion(db, 6);
  });
}

const CREATE_EXPLORE_SESSIONS_TABLE = `
CREATE TABLE IF NOT EXISTS explore_sessions (
  id          TEXT PRIMARY KEY,
  project_id  TEXT NOT NULL REFERENCES projects(id),
  issue_id    TEXT REFERENCES issues(id),
  title       TEXT NOT NULL,
  status      TEXT NOT NULL DEFAULT 'active',
  created_at  TEXT NOT NULL,
  updated_at  TEXT NOT NULL
);
`;

const CREATE_EXPLORE_MESSAGES_TABLE = `
CREATE TABLE IF NOT EXISTS explore_messages (
  id          TEXT PRIMARY KEY,
  session_id  TEXT NOT NULL REFERENCES explore_sessions(id) ON DELETE CASCADE,
  role        TEXT NOT NULL,
  content     TEXT NOT NULL,
  tool_calls  TEXT,
  created_at  TEXT NOT NULL
);
`;

const CREATE_EXPLORE_INDEXES = [
  'CREATE INDEX IF NOT EXISTS idx_explore_sessions_project ON explore_sessions(project_id);',
  'CREATE INDEX IF NOT EXISTS idx_explore_sessions_updated ON explore_sessions(updated_at);',
  'CREATE INDEX IF NOT EXISTS idx_explore_messages_session ON explore_messages(session_id);',
];

function migrateToVersion7(db: DatabaseManager): void {
  db.transaction(() => {
    db.exec(CREATE_EXPLORE_SESSIONS_TABLE);
    db.exec(CREATE_EXPLORE_MESSAGES_TABLE);
    for (const indexSql of CREATE_EXPLORE_INDEXES) {
      db.exec(indexSql);
    }
    setSchemaVersion(db, 7);
  });
}

function detectBaseBranchSync(projectPath: string): string {
  try {
    if (!fs.existsSync(projectPath)) {
      return 'main';
    }
    const gitDir = execFileSync(
      'git',
      ['rev-parse', '--git-dir'],
      { cwd: projectPath, encoding: 'utf-8', timeout: 5000 }
    ).trim();
    if (!gitDir) {
      return 'main';
    }
    const ref = execFileSync(
      'git',
      ['symbolic-ref', 'refs/remotes/origin/HEAD'],
      { cwd: projectPath, encoding: 'utf-8', timeout: 5000 }
    ).trim();
    const match = ref.match(/^refs\/remotes\/origin\/(.+)$/);
    return match ? match[1] : 'main';
  } catch {
    return 'main';
  }
}

function migrateToVersion8(db: DatabaseManager): void {
  db.transaction(() => {
    const tableInfo = db.all<{ name: string }>(
      "PRAGMA table_info(projects)"
    );
    const hasBaseBranch = tableInfo.some(col => col.name === 'base_branch');

    if (!hasBaseBranch) {
      db.exec("ALTER TABLE projects ADD COLUMN base_branch TEXT DEFAULT 'main'");
    }

    const projects = db.all<{ id: string; path: string; base_branch: string | null }>(
      'SELECT id, path, base_branch FROM projects'
    );
    for (const project of projects) {
      if (project.base_branch == null) {
        const branch = detectBaseBranchSync(project.path);
        db.run(
          'UPDATE projects SET base_branch = ? WHERE id = ?',
          [branch, project.id]
        );
      }
    }

    setSchemaVersion(db, 8);
  });
}

function migrateToVersion9(db: DatabaseManager): void {
  db.transaction(() => {
    const tableInfo = db.all<{ name: string }>(
      "PRAGMA table_info(issues)"
    );
    const hasApprovalState = tableInfo.some(col => col.name === 'approval_state');

    if (!hasApprovalState) {
      db.exec("ALTER TABLE issues ADD COLUMN approval_state TEXT");
    }

    setSchemaVersion(db, 9);
  });
}

function migrateToVersion10(db: DatabaseManager): void {
  db.transaction(() => {
    const tableInfo = db.all<{ name: string }>(
      "PRAGMA table_info(explore_sessions)"
    );
    const hasModel = tableInfo.some(col => col.name === 'model');
    const hasVariant = tableInfo.some(col => col.name === 'variant');

    if (!hasModel) {
      db.exec("ALTER TABLE explore_sessions ADD COLUMN model TEXT");
    }

    if (!hasVariant) {
      db.exec("ALTER TABLE explore_sessions ADD COLUMN variant TEXT");
    }

    setSchemaVersion(db, 10);
  });
}
