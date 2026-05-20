import * as fs from 'fs';
import * as path from 'path';
import * as yaml from 'yaml';

export interface AgentConfig {
  context?: string;
  rules?: Record<string, string[]>;
}

function workflowFileCandidates(cwd: string): string[] {
  return [
    path.join(cwd, '.mohist', 'workflow.yaml'),
    path.join(cwd, 'workflow.yaml'),
  ];
}

export function loadAgentConfig(cwd: string): AgentConfig {
  const candidates = workflowFileCandidates(cwd);

  for (const candidate of candidates) {
    try {
      const content = fs.readFileSync(candidate, 'utf-8');
      const parsed = yaml.parse(content);
      if (!parsed || typeof parsed !== 'object') continue;
      if (!parsed.agent || typeof parsed.agent !== 'object') continue;

      const agent = parsed.agent as Record<string, unknown>;
      const result: AgentConfig = {};

      if (typeof agent.context === 'string' && agent.context.length > 0) {
        result.context = agent.context;
      }

      if (agent.rules && typeof agent.rules === 'object') {
        const rules: Record<string, string[]> = {};
        for (const [stage, val] of Object.entries(agent.rules as Record<string, unknown>)) {
          if (Array.isArray(val) && val.every((v) => typeof v === 'string')) {
            rules[stage] = val as string[];
          }
        }
        if (Object.keys(rules).length > 0) {
          result.rules = rules;
        }
      }

      return result;
    } catch {
      continue;
    }
  }

  return {};
}
