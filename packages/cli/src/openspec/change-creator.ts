import * as fs from 'fs';
import * as path from 'path';
import { slugify } from '../utils/slugify';

export interface ChangeMetadata {
  name: string;
  issue_id: string;
  issue_number: number;
  status: string;
  created_at: string;
}

export interface CreateChangeResult {
  changePath: string;
  changeName: string;
  isNew: boolean;
}

function generateSlug(title: string): string {
  const slug = slugify(title);
  return slug.length > 50 ? slug.substring(0, 50).replace(/-+$/, '') : slug;
}

function findNextVersion(changesDir: string, baseName: string): string {
  const existing = fs.readdirSync(changesDir);

  const exactMatch = new RegExp(`^${baseName}(-v\\d+)?$`);
  const versions = existing
    .filter(name => exactMatch.test(name))
    .map(name => {
      const match = name.match(/-v(\d+)$/);
      return match ? parseInt(match[1], 10) : 1;
    });

  const maxVersion = versions.length > 0 ? Math.max(...versions) : 0;
  const nextName = maxVersion === 0 ? baseName : `${baseName}-v${maxVersion + 1}`;

  if (!fs.existsSync(path.join(changesDir, nextName))) {
    return nextName;
  }
  let v = maxVersion + 1;
  while (fs.existsSync(path.join(changesDir, `${baseName}-v${v}`))) {
    v++;
  }
  return `${baseName}-v${v}`;
}

function findExistingChange(changesDir: string, issueNumber: number): string | null {
  if (!fs.existsSync(changesDir)) {
    return null;
  }

  const entries = fs.readdirSync(changesDir, { withFileTypes: true });
  const prefix = `${issueNumber}-`;
  const match = entries.find(e => e.isDirectory() && e.name.startsWith(prefix));
  return match ? match.name : null;
}

export function createChange(
  worktreePath: string,
  issueNumber: number,
  issueTitle: string,
  issueId: string,
  force: boolean = false,
): CreateChangeResult {
  const specsDir = path.join(worktreePath, 'openspec');
  const changesDir = path.join(specsDir, 'changes');

  if (!fs.existsSync(specsDir)) {
    fs.mkdirSync(specsDir, { recursive: true });
  }
  if (!fs.existsSync(changesDir)) {
    fs.mkdirSync(changesDir, { recursive: true });
  }

  const slug = generateSlug(issueTitle);
  const baseName = `${issueNumber}-${slug}`;

  const existingName = findExistingChange(changesDir, issueNumber);
  let changeName: string;
  let isNew: boolean;

  if (existingName) {
    if (force) {
      const existingPath = path.join(changesDir, existingName);
      fs.rmSync(existingPath, { recursive: true, force: true });
      changeName = baseName;
      isNew = true;
    } else {
      changeName = findNextVersion(changesDir, baseName);
      isNew = true;
    }
  } else {
    changeName = baseName;
    isNew = true;
  }

  const changePath = path.join(changesDir, changeName);
  fs.mkdirSync(changePath, { recursive: true });
  fs.mkdirSync(path.join(changePath, 'specs'), { recursive: true });
  fs.mkdirSync(path.join(changePath, 'session-memories'), { recursive: true });

  const metadata: ChangeMetadata = {
    name: changeName,
    issue_id: issueId,
    issue_number: issueNumber,
    status: 'planning',
    created_at: new Date().toISOString(),
  };
  fs.writeFileSync(
    path.join(changePath, '.change.json'),
    JSON.stringify(metadata, null, 2),
  );

  if (!fs.existsSync(path.join(changePath, 'proposal.md'))) {
    fs.writeFileSync(path.join(changePath, 'proposal.md'), '');
  }
  if (!fs.existsSync(path.join(changePath, 'design.md'))) {
    fs.writeFileSync(path.join(changePath, 'design.md'), '');
  }

  return { changePath, changeName, isNew };
}
