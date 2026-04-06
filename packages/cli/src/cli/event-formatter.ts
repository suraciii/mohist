import chalk from 'chalk';

interface EventData {
  issueId?: string;
  projectId?: string;
  from?: string;
  to?: string;
  commentId?: string;
  body?: string;
  createdAt?: string;
  error?: string;
  stage?: string;
}

interface EventConfig {
  symbol: string;
  color: (text: string) => string;
  description: string;
}

const EVENT_CONFIGS: Record<string, EventConfig> = {
  agent_started: { symbol: '>>', color: chalk.green, description: 'agent started' },
  agent_completed: { symbol: 'ok', color: chalk.green, description: 'agent completed' },
  agent_paused: { symbol: '||', color: chalk.yellow, description: 'agent paused' },
  agent_error: { symbol: '!!', color: chalk.red, description: 'agent error' },
  stage_changed: { symbol: '->', color: chalk.cyan, description: 'stage changed' },
  comment_added: { symbol: '##', color: chalk.white, description: 'comment added' },
  approval_requested: { symbol: '??', color: chalk.yellow, description: 'approval requested' },
};

function formatTimestamp(): string {
  const now = new Date();
  const hh = String(now.getHours()).padStart(2, '0');
  const mm = String(now.getMinutes()).padStart(2, '0');
  const ss = String(now.getSeconds()).padStart(2, '0');
  return `${hh}:${mm}:${ss}`;
}

function formatIssueSuffix(data: EventData): string {
  const parts: string[] = [];

  if (data.issueId) {
    const prefix = `issue #${data.issueId}`;

    if (data.from && data.to) {
      return `${prefix}: ${data.from} -> ${data.to}`;
    }
    if (data.error) {
      return `${prefix}: ${data.error}`;
    }
    if (data.body) {
      return `${prefix}: "${data.body}"`;
    }
    if (data.stage) {
      return `${prefix}: ${data.stage}`;
    }

    parts.push(prefix);
  }

  return parts.join(' ');
}

export function formatEvent(eventType: string, dataStr: string): string {
  const timestamp = formatTimestamp();

  let data: EventData;
  try {
    data = JSON.parse(dataStr);
  } catch {
    return `${chalk.gray(`[${timestamp}]`)} ${chalk.gray(eventType)} ${dataStr}`;
  }

  const config = EVENT_CONFIGS[eventType];
  if (!config) {
    return `${chalk.gray(`[${timestamp}]`)} ${chalk.gray(eventType)} ${dataStr}`;
  }

  const coloredSymbol = config.color(config.symbol);
  const coloredDescription = config.color(config.description);
  const suffix = formatIssueSuffix(data);

  return `${chalk.gray(`[${timestamp}]`)} ${coloredSymbol} ${coloredDescription}  ${suffix}`;
}
