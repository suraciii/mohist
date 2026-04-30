import * as fs from 'fs';
import * as path from 'path';

export function parseVerdict(content: string): 'pass' | 'fail' | null {
  const passMatch = content.match(/^##\s*Result:\s*PASS/m);
  const failMatch = content.match(/^##\s*Result:\s*FAIL/m);

  if (failMatch) return 'fail';
  if (passMatch) return 'pass';
  return null;
}

export function extractFixSuggestions(content: string): Array<{ file: string; line?: number; description: string }> {
  const suggestions: Array<{ file: string; line?: number; description: string }> = [];

  const fixSectionMatch = content.match(/^##\s*Fix\s*Suggestions$\s*^((?:(?!\n^##).)+)/m);

  if (!fixSectionMatch) return suggestions;

  const lines = fixSectionMatch[1].split('\n');

  for (const line of lines) {
    const trimmed = line.trim();
    if (!trimmed || trimmed.startsWith('#')) continue;

    const match = trimmed.match(/^\d+\.\s*\[([^\]:]+)(?::(\d+))?\]\s*(.+)/);
    if (match) {
      suggestions.push({
        file: match[1],
        line: match[2] ? parseInt(match[2], 10) : undefined,
        description: match[3],
      });
    }
  }

  return suggestions;
}

export function readReportFile(changeDir: string, filename: string): string | null {
  const filePath = path.join(changeDir, filename);
  try {
    if (!fs.existsSync(filePath)) return null;
    const content = fs.readFileSync(filePath, 'utf-8').trim();
    return content.length > 0 ? content : null;
  } catch {
    return null;
  }
}