import { execFileSync } from 'child_process';
import * as fs from 'fs';
import { DatabaseManager } from './database';
import { Log } from '../util/log';

const log = Log.create({ service: 'db' });

const SCHEMA_VERSION = 23;

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
  stage       TEXT NOT NULL DEFAULT 'backlog',
  status      TEXT NOT NULL DEFAULT 'active',
  archived_at TEXT DEFAULT NULL,
  created_at  TEXT NOT NULL,
  updated_at  TEXT NOT NULL,
  UNIQUE(project_id, number)
);
`;

const CREATE_SKILLS_TABLE = `
CREATE TABLE IF NOT EXISTS skills (
  id          TEXT PRIMARY KEY,
  name        TEXT NOT NULL,
  project_id  TEXT NOT NULL,
  description TEXT NOT NULL,
  prompt      TEXT NOT NULL,
  dir_path    TEXT NOT NULL,
  created_at  TEXT NOT NULL,
  updated_at  TEXT NOT NULL
);
`;

const CREATE_INDEXES = [
  'CREATE INDEX IF NOT EXISTS idx_issues_project_stage ON issues(project_id, stage);',
  'CREATE INDEX IF NOT EXISTS idx_issues_project_status ON issues(project_id, status);',
  'CREATE INDEX IF NOT EXISTS idx_issues_archived ON issues(archived_at);',
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
  log.info('Running initial migrations', { targetVersion: SCHEMA_VERSION });
  db.transaction(() => {
    db.exec(CREATE_PROJECTS_TABLE);
    db.exec(CREATE_ISSUES_TABLE);
    db.exec(CREATE_CONFIG_TABLE);
    db.exec(CREATE_SKILLS_TABLE);
    
    for (const indexSql of CREATE_INDEXES) {
      db.exec(indexSql);
    }
    
    setSchemaVersion(db, SCHEMA_VERSION);
  });
  log.info('Initial migrations completed', { version: SCHEMA_VERSION });
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
  log.info('Initializing database', { currentVersion, targetVersion: SCHEMA_VERSION });
  
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

  if (currentVersion < 11) {
    migrateToVersion11(db);
  }

  if (currentVersion < 13) {
    migrateToVersion13(db);
  }

  if (currentVersion < 14) {
    migrateToVersion14(db);
  }

  if (currentVersion < 15) {
    migrateToVersion15(db);
  }

  if (currentVersion < 16) {
    migrateToVersion16(db);
  }

  if (currentVersion < 17) {
    migrateToVersion17(db);
  }

  if (currentVersion < 18) {
    migrateToVersion18(db);
  }

  if (currentVersion < 19) {
    migrateToVersion19(db);
  }

  if (currentVersion < 20) {
    migrateToVersion20(db);
  }

  if (currentVersion < 21) {
    migrateToVersion21(db);
  }

  if (currentVersion < 22) {
    migrateToVersion22(db);
  }

  if (currentVersion < 23) {
    migrateToVersion23(db);
  }

  const finalVersion = getSchemaVersion(db);
  log.info('Database initialization completed', { version: finalVersion });
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

    try {
      const ref = execFileSync(
        'git',
        ['symbolic-ref', 'refs/remotes/origin/HEAD'],
        { cwd: projectPath, encoding: 'utf-8', timeout: 5000 }
      ).trim();
      const match = ref.match(/^refs\/remotes\/origin\/(.+)$/);
      if (match) return match[1];
    } catch {}

    try {
      execFileSync('git', ['rev-parse', '--verify', 'origin/main'], {
        cwd: projectPath, encoding: 'utf-8', timeout: 5000,
      });
      return 'main';
    } catch {}

    try {
      execFileSync('git', ['rev-parse', '--verify', 'origin/master'], {
        cwd: projectPath, encoding: 'utf-8', timeout: 5000,
      });
      return 'master';
    } catch {}

    try {
      const headBranch = execFileSync('git', ['rev-parse', '--abbrev-ref', 'HEAD'], {
        cwd: projectPath, encoding: 'utf-8', timeout: 5000,
      }).trim();
      if (headBranch && headBranch !== 'HEAD') return headBranch;
    } catch {}

    return 'main';
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

function migrateToVersion11(db: DatabaseManager): void {
  db.transaction(() => {
    const sessions = db.all<{ id: string; model: string }>(
      "SELECT id, model FROM explore_sessions WHERE model IS NOT NULL"
    );
    for (const session of sessions) {
      if (!session.model.includes('/')) {
        db.run(
          'UPDATE explore_sessions SET model = NULL WHERE id = ?',
          [session.id]
        );
      }
    }
    setSchemaVersion(db, 11);
  });
}

const CREATE_CODER_SESSION_TABLE = `
CREATE TABLE IF NOT EXISTS coder_session (
  id                TEXT PRIMARY KEY,
  issue_id          TEXT NOT NULL REFERENCES issues(id) ON DELETE CASCADE,
  acp_session_id    TEXT NOT NULL,
  execution_id      TEXT,
  task_description  TEXT,
  status            TEXT NOT NULL DEFAULT 'running',
  created_at        TEXT NOT NULL,
  completed_at      TEXT
);
`;

const CREATE_CODER_SESSION_INDEXES = [
  'CREATE INDEX IF NOT EXISTS idx_coder_session_issue_id ON coder_session(issue_id);',
];

function migrateToVersion13(db: DatabaseManager): void {
  db.transaction(() => {
    db.exec(CREATE_CODER_SESSION_TABLE);
    for (const indexSql of CREATE_CODER_SESSION_INDEXES) {
      db.exec(indexSql);
    }
    setSchemaVersion(db, 13);
  });
}

const CREATE_PIPELINE_CHECKPOINT_TABLE = `
CREATE TABLE IF NOT EXISTS pipeline_checkpoint (
  issue_number   INTEGER NOT NULL,
  stage          TEXT NOT NULL,
  completed_steps TEXT NOT NULL DEFAULT '[]',
  next_step      TEXT,
  updated_at     TEXT NOT NULL,
  PRIMARY KEY (issue_number, stage)
);
`;

const PRIORITY_LABEL_MAP: Record<string, string> = {
  'priority:critical': 'p0',
  'priority:p0': 'p0',
  'priority:high': 'p1',
  'priority:p1': 'p1',
  'priority:medium': 'p2',
  'priority:p2': 'p2',
  'priority:low': 'p3',
  'priority:p3': 'p3',
  'priority:backlog': 'p4',
  'priority:p4': 'p4',
};

function migrateToVersion14(db: DatabaseManager): void {
  db.transaction(() => {
    const tableInfo = db.all<{ name: string }>(
      "PRAGMA table_info(issues)"
    );
    const hasMergeState = tableInfo.some(col => col.name === 'merge_state');

    if (!hasMergeState) {
      db.exec("ALTER TABLE issues ADD COLUMN merge_state TEXT");
    }

    db.exec(CREATE_PIPELINE_CHECKPOINT_TABLE);

    const hasPriority = tableInfo.some(col => col.name === 'priority');

    if (!hasPriority) {
      db.exec("ALTER TABLE issues ADD COLUMN priority TEXT NOT NULL DEFAULT 'p2'");
      db.exec('CREATE INDEX IF NOT EXISTS idx_issues_project_priority ON issues(project_id, priority)');
    }

    const issues = db.all<{ id: string; labels: string }>(
      'SELECT id, labels FROM issues WHERE labels IS NOT NULL'
    );

    for (const issue of issues) {
      let labels: string[];
      try {
        labels = JSON.parse(issue.labels || '[]');
      } catch {
        continue;
      }

      if (!Array.isArray(labels)) continue;

      let priority: string | null = null;
      const matchedIndices: number[] = [];

      for (let i = 0; i < labels.length; i++) {
        const label = labels[i];
        if (typeof label === 'string' && label in PRIORITY_LABEL_MAP) {
          if (priority === null) {
            priority = PRIORITY_LABEL_MAP[label];
          }
          matchedIndices.push(i);
        }
      }

      if (matchedIndices.length > 0) {
        const remainingLabels = labels.filter((_, i) => !matchedIndices.includes(i));
        db.run(
          'UPDATE issues SET priority = ?, labels = ? WHERE id = ?',
          [priority, JSON.stringify(remainingLabels), issue.id]
        );
      }
    }

    const hasConflictRetryCount = tableInfo.some(col => col.name === 'conflict_retry_count');

    if (!hasConflictRetryCount) {
      db.exec('ALTER TABLE issues ADD COLUMN conflict_retry_count INTEGER DEFAULT 0');
    }

    setSchemaVersion(db, 14);
  });
}

function migrateToVersion15(db: DatabaseManager): void {
  db.transaction(() => {
    const tableInfo = db.all<{ name: string }>(
      "PRAGMA table_info(coder_session)"
    );
    const hasCoderModel = tableInfo.some(col => col.name === 'model');
    const hasCoderType = tableInfo.some(col => col.name === 'coder_type');
    const hasCoderStage = tableInfo.some(col => col.name === 'stage');

    if (!hasCoderModel) {
      db.exec("ALTER TABLE coder_session ADD COLUMN model TEXT");
    }
    if (!hasCoderType) {
      db.exec("ALTER TABLE coder_session ADD COLUMN coder_type TEXT");
    }
    if (!hasCoderStage) {
      db.exec("ALTER TABLE coder_session ADD COLUMN stage TEXT");
    }

    const issueTableInfo = db.all<{ name: string }>(
      "PRAGMA table_info(issues)"
    );
    const hasIssueModel = issueTableInfo.some(col => col.name === 'model');

    if (!hasIssueModel) {
      db.exec("ALTER TABLE issues ADD COLUMN model TEXT");
    }

    setSchemaVersion(db, 15);
  });
}

const CREATE_SKILL_RUNS_TABLE = `
CREATE TABLE IF NOT EXISTS skill_runs (
  id          TEXT PRIMARY KEY,
  skill_id    TEXT NOT NULL,
  project_id  TEXT NOT NULL,
  status      TEXT NOT NULL,
  output      TEXT,
  error       TEXT,
  issue_id    TEXT,
  started_at  TEXT NOT NULL,
  completed_at TEXT
);
`;

const CREATE_AGENT_SKILL_SCHEDULES_TABLE = `
CREATE TABLE IF NOT EXISTS agent_skill_schedules (
  id              TEXT PRIMARY KEY,
  skill_id        TEXT NOT NULL REFERENCES agent_skills(id) ON DELETE CASCADE,
  schedule_type   TEXT NOT NULL,
  schedule_value  TEXT NOT NULL,
  anchor          TEXT,
  next_run_at     TEXT NOT NULL,
  last_run_at     TEXT,
  enabled         INTEGER NOT NULL DEFAULT 1,
  created_at      TEXT NOT NULL,
  updated_at      TEXT NOT NULL
);
`;

const CREATE_AGENT_SKILL_SCHEDULES_INDEXES = [
  'CREATE INDEX IF NOT EXISTS idx_agent_skill_schedules_skill_id ON agent_skill_schedules(skill_id);',
  'CREATE INDEX IF NOT EXISTS idx_agent_skill_schedules_enabled_next_run ON agent_skill_schedules(enabled, next_run_at);',
];

const CREATE_SKILLS_TABLE_V16 = `
CREATE TABLE IF NOT EXISTS skills (
  id          TEXT PRIMARY KEY,
  name        TEXT UNIQUE NOT NULL,
  project_id  TEXT NOT NULL REFERENCES projects(id),
  description TEXT NOT NULL DEFAULT '',
  prompt      TEXT NOT NULL DEFAULT '',
  dir_path    TEXT NOT NULL DEFAULT '',
  created_at  TEXT NOT NULL,
  updated_at  TEXT NOT NULL
);
`;

const CREATE_SKILLS_INDEXES = [
  'CREATE INDEX IF NOT EXISTS idx_skills_project_id ON skills(project_id);',
];

const CREATE_SKILL_RUNS_TABLE_V16 = `
CREATE TABLE IF NOT EXISTS skill_runs (
  id            TEXT PRIMARY KEY,
  skill_id      TEXT NOT NULL REFERENCES skills(id) ON DELETE CASCADE,
  project_id    TEXT NOT NULL REFERENCES projects(id),
  status        TEXT NOT NULL DEFAULT 'running',
  output        TEXT,
  error         TEXT,
  issue_id      TEXT REFERENCES issues(id),
  started_at    TEXT NOT NULL,
  completed_at  TEXT
);
`;

const CREATE_SKILL_RUNS_INDEXES = [
  'CREATE INDEX IF NOT EXISTS idx_skill_runs_skill_id ON skill_runs(skill_id);',
  'CREATE INDEX IF NOT EXISTS idx_skill_runs_project_id ON skill_runs(project_id);',
];

const CREATE_AGENT_SKILLS_TABLE = `
CREATE TABLE IF NOT EXISTS agent_skills (
  id          TEXT PRIMARY KEY,
  name        TEXT UNIQUE NOT NULL,
  project_id  TEXT NOT NULL REFERENCES projects(id),
  description TEXT NOT NULL DEFAULT '',
  prompt      TEXT NOT NULL DEFAULT '',
  dir_path    TEXT NOT NULL DEFAULT '',
  created_at  TEXT NOT NULL,
  updated_at  TEXT NOT NULL
);
`;

function migrateToVersion16(db: DatabaseManager): void {
  db.transaction(() => {
    db.exec(CREATE_SKILL_RUNS_TABLE);
    db.exec(CREATE_AGENT_SKILL_SCHEDULES_TABLE);
    for (const indexSql of CREATE_AGENT_SKILL_SCHEDULES_INDEXES) {
      db.exec(indexSql);
    }

    const tableInfo = db.all<{ name: string }>("PRAGMA table_info(issues)");
    const hasArchivedAt = tableInfo.some(col => col.name === 'archived_at');
    if (!hasArchivedAt) {
      db.exec("ALTER TABLE issues ADD COLUMN archived_at TEXT DEFAULT NULL");
      db.exec("CREATE INDEX IF NOT EXISTS idx_issues_archived ON issues(archived_at)");
    }

    db.exec(`
CREATE TABLE IF NOT EXISTS skills (
  id          TEXT PRIMARY KEY,
  name        TEXT UNIQUE NOT NULL,
  project_id  TEXT NOT NULL REFERENCES projects(id),
  description TEXT NOT NULL DEFAULT '',
  prompt      TEXT NOT NULL DEFAULT '',
  dir_path    TEXT NOT NULL,
  created_at  TEXT NOT NULL,
  updated_at  TEXT NOT NULL
);
`);

    db.exec(`
CREATE TABLE IF NOT EXISTS skill_runs (
  id            TEXT PRIMARY KEY,
  skill_id      TEXT NOT NULL REFERENCES skills(id) ON DELETE CASCADE,
  project_id    TEXT NOT NULL,
  status        TEXT NOT NULL DEFAULT 'running',
  output        TEXT,
  error         TEXT,
  issue_id      TEXT REFERENCES issues(id),
  started_at    TEXT NOT NULL,
  completed_at  TEXT
);
`);

    db.exec('CREATE INDEX IF NOT EXISTS idx_skill_runs_skill_id ON skill_runs(skill_id);');

    const hasBlockedReason = tableInfo.some(col => col.name === 'blocked_reason');
    if (!hasBlockedReason) {
      db.exec('ALTER TABLE issues ADD COLUMN blocked_reason TEXT DEFAULT NULL');
    }

    const hasRetryCount = tableInfo.some(col => col.name === 'retry_count');
    if (!hasRetryCount) {
      db.exec('ALTER TABLE issues ADD COLUMN retry_count INTEGER DEFAULT 0');
    }

    db.exec(CREATE_AGENT_SKILLS_TABLE);
    db.exec(CREATE_SKILLS_TABLE_V16);
    for (const indexSql of CREATE_SKILLS_INDEXES) {
      db.exec(indexSql);
    }
    db.exec(CREATE_SKILL_RUNS_TABLE_V16);
    for (const indexSql of CREATE_SKILL_RUNS_INDEXES) {
      db.exec(indexSql);
    }

    setSchemaVersion(db, 16);
  });
}

const CREATE_SKILLS_TABLE_V17 = `
CREATE TABLE IF NOT EXISTS skills (
  id          TEXT PRIMARY KEY,
  name        TEXT NOT NULL,
  project_id  TEXT NOT NULL REFERENCES projects(id),
  description TEXT NOT NULL,
  prompt      TEXT NOT NULL,
  dir_path    TEXT NOT NULL,
  created_at  TEXT NOT NULL,
  updated_at  TEXT NOT NULL
);
`;

const CREATE_SKILLS_INDEXES_V17 = [
  'CREATE INDEX IF NOT EXISTS idx_skills_project ON skills(project_id);',
  'CREATE UNIQUE INDEX IF NOT EXISTS idx_skills_name ON skills(name);',
];

const CREATE_SKILL_RUNS_TABLE_V17 = `
CREATE TABLE IF NOT EXISTS skill_runs (
  id            TEXT PRIMARY KEY,
  skill_id      TEXT NOT NULL REFERENCES skills(id) ON DELETE CASCADE,
  project_id    TEXT NOT NULL REFERENCES projects(id),
  status        TEXT NOT NULL DEFAULT 'running',
  output        TEXT,
  error         TEXT,
  issue_id      TEXT,
  started_at    TEXT NOT NULL,
  completed_at  TEXT
);
`;

const CREATE_SKILL_RUNS_INDEXES_V17 = [
  'CREATE INDEX IF NOT EXISTS idx_skill_runs_skill_id ON skill_runs(skill_id);',
  'CREATE INDEX IF NOT EXISTS idx_skill_runs_project_id ON skill_runs(project_id);',
];

function migrateToVersion17(db: DatabaseManager): void {
  db.transaction(() => {
    db.exec(CREATE_SKILLS_TABLE_V17);
    for (const indexSql of CREATE_SKILLS_INDEXES_V17) {
      db.exec(indexSql);
    }
    db.exec(CREATE_SKILL_RUNS_TABLE_V17);
    for (const indexSql of CREATE_SKILL_RUNS_INDEXES_V17) {
      db.exec(indexSql);
    }
    setSchemaVersion(db, 17);
  });
}

const CREATE_ISSUE_TASK_QUEUE_TABLE = `
CREATE TABLE IF NOT EXISTS issue_task_queue (
  id            TEXT PRIMARY KEY,
  issue_id      TEXT NOT NULL REFERENCES issues(id) ON DELETE CASCADE,
  issue_number  INTEGER NOT NULL,
  project_id    TEXT NOT NULL REFERENCES projects(id),
  task_type     TEXT NOT NULL,
  payload       TEXT NOT NULL DEFAULT '{}',
  priority      INTEGER NOT NULL DEFAULT 0,
  status        TEXT NOT NULL DEFAULT 'pending',
  enqueued_at   TEXT NOT NULL,
  started_at    TEXT,
  result        TEXT,
  completed_at  TEXT
);
`;

const CREATE_ISSUE_TASK_QUEUE_INDEXES = [
  'CREATE INDEX IF NOT EXISTS idx_issue_task_queue_issue_id ON issue_task_queue(issue_id);',
  'CREATE INDEX IF NOT EXISTS idx_issue_task_queue_status ON issue_task_queue(status);',
  'CREATE INDEX IF NOT EXISTS idx_issue_task_queue_issue_status ON issue_task_queue(issue_id, status);',
];

const CREATE_CHECK_SUITES_TABLE = `
CREATE TABLE IF NOT EXISTS check_suites (
  id            TEXT PRIMARY KEY,
  issue_id      TEXT NOT NULL REFERENCES issues(id) ON DELETE CASCADE,
  snapshot_sha  TEXT NOT NULL,
  status        TEXT NOT NULL DEFAULT 'running',
  checks        TEXT NOT NULL DEFAULT '{}',
  created_at    TEXT NOT NULL,
  updated_at    TEXT NOT NULL
);
`;

const CREATE_CHECK_SUITES_INDEXES = [
  'CREATE INDEX IF NOT EXISTS idx_check_suites_issue_status ON check_suites(issue_id, status);',
];

function migrateToVersion18(db: DatabaseManager): void {
  db.transaction(() => {
    db.exec(CREATE_ISSUE_TASK_QUEUE_TABLE);
    for (const indexSql of CREATE_ISSUE_TASK_QUEUE_INDEXES) {
      db.exec(indexSql);
    }
    db.exec(CREATE_CHECK_SUITES_TABLE);
    for (const indexSql of CREATE_CHECK_SUITES_INDEXES) {
      db.exec(indexSql);
    }
    setSchemaVersion(db, 18);
  });
}

const CREATE_SESSION_STREAM_LOG_TABLE = `
CREATE TABLE IF NOT EXISTS session_stream_log (
  id          TEXT PRIMARY KEY,
  session_id  TEXT NOT NULL,
  issue_id    TEXT NOT NULL REFERENCES issues(id) ON DELETE CASCADE,
  event_type  TEXT NOT NULL,
  data        TEXT NOT NULL DEFAULT '{}',
  created_at  TEXT NOT NULL DEFAULT (datetime('now'))
);
`;

const CREATE_SESSION_STREAM_LOG_INDEXES = [
  'CREATE INDEX IF NOT EXISTS idx_session_stream_log_session ON session_stream_log(session_id, created_at);',
  'CREATE INDEX IF NOT EXISTS idx_session_stream_log_issue ON session_stream_log(issue_id, created_at);',
];

function migrateToVersion19(db: DatabaseManager): void {
  db.transaction(() => {
    db.exec(CREATE_SESSION_STREAM_LOG_TABLE);
    for (const indexSql of CREATE_SESSION_STREAM_LOG_INDEXES) {
      db.exec(indexSql);
    }
    setSchemaVersion(db, 19);
  });
}

const CREATE_STAGE_EXECUTIONS_TABLE = `
CREATE TABLE IF NOT EXISTS stage_executions (
  id            TEXT PRIMARY KEY,
  issue_id      TEXT NOT NULL REFERENCES issues(id) ON DELETE CASCADE,
  stage         TEXT NOT NULL,
  status        TEXT NOT NULL DEFAULT 'running',
  task_results  TEXT NOT NULL DEFAULT '[]',
  check_results TEXT NOT NULL DEFAULT '[]',
  created_at    TEXT NOT NULL,
  updated_at    TEXT NOT NULL
);
`;

const CREATE_STAGE_EXECUTIONS_INDEXES = [
  'CREATE INDEX IF NOT EXISTS idx_stage_executions_issue_id ON stage_executions(issue_id);',
  'CREATE INDEX IF NOT EXISTS idx_stage_executions_issue_status ON stage_executions(issue_id, status);',
];

function migrateToVersion20(db: DatabaseManager): void {
  db.transaction(() => {
    db.exec(CREATE_STAGE_EXECUTIONS_TABLE);
    for (const indexSql of CREATE_STAGE_EXECUTIONS_INDEXES) {
      db.exec(indexSql);
    }
    setSchemaVersion(db, 20);
  });
}

function migrateToVersion21(db: DatabaseManager): void {
  db.transaction(() => {
    const tableInfo = db.all<{ name: string }>("PRAGMA table_info(coder_session)");
    const hasTitle = tableInfo.some(col => col.name === 'title');

    if (!hasTitle) {
      db.exec("ALTER TABLE coder_session ADD COLUMN title TEXT");
    }

    setSchemaVersion(db, 21);
  });
}

function migrateToVersion22(db: DatabaseManager): void {
  db.transaction(() => {
    db.exec('DROP INDEX IF EXISTS idx_agent_session_message_issue_step');
    db.exec('DROP TABLE IF EXISTS agent_session_message');
    setSchemaVersion(db, 22);
  });
}

function migrateToVersion23(db: DatabaseManager): void {
  db.transaction(() => {
    const tableInfo = db.all<{ name: string }>("PRAGMA table_info(coder_session)");
    const hasProcessPid = tableInfo.some(col => col.name === 'process_pid');

    if (!hasProcessPid) {
      db.exec('ALTER TABLE coder_session ADD COLUMN process_pid INTEGER');
    }

    setSchemaVersion(db, 23);
  });
}
