import { z } from 'zod';
import { Tool, type ToolInstance } from '../agent-runtime/tool';
import type { QuestionRepo } from '../db/question-repo';
import type { IssueRepo } from '../db/issue-repo';
import type { EventBus } from '../services/event-bus';
import { Log } from '../util/log';

const log = Log.create({ service: 'spawn-coder' });

const DEFAULT_TIMEOUT_MS = 24 * 60 * 60 * 1000;

interface PendingResolver {
  resolve: (answer: string) => void;
  timer: NodeJS.Timeout;
}

const pendingResolvers = new Map<string, PendingResolver>();

export interface AskUserContext {
  questionRepo: QuestionRepo;
  issueRepo?: IssueRepo;
  eventBus: EventBus;
  issueId?: string;
  projectId?: string;
  timeoutMs?: number;
  onWaitingChange?: (issueId: string, questionId: string | null, question?: string) => void;
}

export function hasPendingResolver(questionId: string): boolean {
  return pendingResolvers.has(questionId);
}

export function resolveQuestion(questionId: string, answer: string): boolean {
  const entry = pendingResolvers.get(questionId);
  if (!entry) return false;

  clearTimeout(entry.timer);
  pendingResolvers.delete(questionId);
  entry.resolve(answer);
  return true;
}

// eslint-disable-next-line @typescript-eslint/no-explicit-any
export function createAskUserTool(
  context: AskUserContext
): ToolInstance<any> {
  const timeoutMs = context.timeoutMs ?? DEFAULT_TIMEOUT_MS;

  return Tool.define('ask_user', {
    description:
      'Ask the user a question and wait for their reply. ' +
      'Use this when requirements are ambiguous, you need a user decision, ' +
      'or there are multiple valid approaches and you need the user to choose. ' +
      'Do NOT use this when you can solve the problem with available tools or ' +
      'when there is a clear best practice. Ask one question at a time. ' +
      'Make questions specific and actionable.',
    parameters: z.object({
      question: z
        .string()
        .describe('The question to ask the user'),
    }),
    execute: async (params) => {
      const issueId = context.issueId;
      if (!issueId) {
        return 'Error: no issue context available for ask_user tool.';
      }

      const q = context.questionRepo.create(issueId, params.question);

      let projectId = context.projectId;
      if (!projectId && context.issueRepo) {
        const issue = context.issueRepo.findById(issueId);
        if (issue) {
          projectId = issue.projectId;
        }
      }

      context.eventBus.emit('question_asked', {
        issueId,
        projectId: projectId ?? '',
        questionId: q.id,
        question: params.question,
      });

      context.onWaitingChange?.(issueId, q.id, params.question);

      log.info('ask_user question created', { issueId, questionId: q.id, question: params.question.slice(0, 100) });

      const answer = await new Promise<string>((resolve) => {
        const cleanup = (resolvedQuestionId?: string) => {
          context.onWaitingChange?.(issueId, null, resolvedQuestionId);
        };
        const timer = setTimeout(() => {
          pendingResolvers.delete(q.id);
          context.questionRepo.expire(q.id);
          log.warn('ask_user question expired', { questionId: q.id, timeoutMs });
          cleanup(q.id);
          resolve('No answer received within timeout. Proceed with your best judgment.');
        }, timeoutMs);

        const wrappedResolve = (answer: string) => {
          cleanup(q.id);
          resolve(answer);
        };

        pendingResolvers.set(q.id, { resolve: wrappedResolve, timer });
      });

      return `用户回答: ${answer}`;
    },
  });
}
