import { Hono } from 'hono';
import { QuestionRepo } from '../db/question-repo';
import { IssueRepo } from '../db/issue-repo';
import { EventBus } from '../services/event-bus';
import { ApiResponse, Question } from '../types';
import { resolveQuestion, hasPendingResolver } from '../tools/ask-user';
import { Log } from '../util/log';

const log = Log.create({ service: 'issue' });

export function createQuestionRoutes(
  questionRepo: QuestionRepo,
  issueRepo: IssueRepo,
  eventBus: EventBus,
): Hono {
  const app = new Hono();

  // GET /api/questions?issueId=xxx - List questions for an issue
  app.get('/', async (c) => {
    try {
      const issueId = c.req.query('issueId');
      
      if (!issueId) {
        const response: ApiResponse = {
          success: false,
          error: 'issueId query parameter is required'
        };
        return c.json(response, 400);
      }

      const questions = questionRepo.findByIssueId(issueId);
      
      const response: ApiResponse<Question[]> = {
        success: true,
        data: questions
      };
      return c.json(response);
    } catch (error) {
      const response: ApiResponse = {
        success: false,
        error: error instanceof Error ? error.message : 'Unknown error'
      };
      return c.json(response, 500);
    }
  });

  // GET /api/questions/:id - Get single question detail
  app.get('/:id', async (c) => {
    try {
      const id = c.req.param('id');
      
      const question = questionRepo.findById(id);
      
      if (!question) {
        const response: ApiResponse = {
          success: false,
          error: `Question ${id} not found`
        };
        return c.json(response, 404);
      }

      const response: ApiResponse<Question> = {
        success: true,
        data: question
      };
      return c.json(response);
    } catch (error) {
      const response: ApiResponse = {
        success: false,
        error: error instanceof Error ? error.message : 'Unknown error'
      };
      return c.json(response, 500);
    }
  });

  // POST /api/questions/:id/reply - Reply to a question
  app.post('/:id/reply', async (c) => {
    try {
      const id = c.req.param('id');
      const { answer } = await c.req.json();
      
      if (!answer || typeof answer !== 'string') {
        const response: ApiResponse = {
          success: false,
          error: 'answer is required and must be a string'
        };
        return c.json(response, 400);
      }

      // Check if question exists
      const question = questionRepo.findById(id);
      if (!question) {
        const response: ApiResponse = {
          success: false,
          error: `Question ${id} not found`
        };
        return c.json(response, 404);
      }

      // Check if question is still pending
      if (question.status !== 'pending') {
        const response: ApiResponse = {
          success: false,
          error: `Question ${id} is already ${question.status}`
        };
        return c.json(response, 409);
      }

      // Check if agent is still waiting (resolver exists in memory)
      if (!hasPendingResolver(id)) {
        const response: ApiResponse = {
          success: false,
          error: `Question ${id} has expired or the agent is no longer waiting for a reply`
        };
        return c.json(response, 410);
      }

      // Update question in DB first
      const updatedQuestion = questionRepo.answer(id, answer);
      if (!updatedQuestion) {
        const response: ApiResponse = {
          success: false,
          error: `Failed to update question ${id}`
        };
        return c.json(response, 500);
      }

      // Now resolve the ask_user tool's Promise to unblock the agent
      const resolved = resolveQuestion(id, answer);
      if (!resolved) {
        log.warn(`Question DB updated but resolver lost`, { questionId: id });
      }

      // Resolve projectId from issue
      const issue = issueRepo.findById(updatedQuestion.issueId);
      const projectId = issue?.projectId ?? '';

      // Emit event
      eventBus.emit('question_answered', {
        issueId: updatedQuestion.issueId,
        projectId,
        questionId: id,
        answer: answer,
      });

      log.info('Question answered', { questionId: id, answer: answer.slice(0, 100) });

      const response: ApiResponse<Question> = {
        success: true,
        data: updatedQuestion
      };
      return c.json(response);
    } catch (error) {
      const response: ApiResponse = {
        success: false,
        error: error instanceof Error ? error.message : 'Unknown error'
      };
      return c.json(response, 500);
    }
  });

  // POST /api/questions/:id/expire - Mark a question as expired
  app.post('/:id/expire', async (c) => {
    try {
      const id = c.req.param('id');

      const question = questionRepo.findById(id);
      if (!question) {
        const response: ApiResponse = {
          success: false,
          error: `Question ${id} not found`
        };
        return c.json(response, 404);
      }

      if (question.status !== 'pending') {
        const response: ApiResponse = {
          success: false,
          error: `Question ${id} is already ${question.status}`
        };
        return c.json(response, 409);
      }

      const updatedQuestion = questionRepo.expire(id);
      if (!updatedQuestion) {
        const response: ApiResponse = {
          success: false,
          error: `Failed to expire question ${id}`
        };
        return c.json(response, 500);
      }

      // If agent is still waiting, resolve with timeout message
      const resolved = resolveQuestion(id, 'Question was manually expired by user. Proceed with your best judgment.');
      if (resolved) {
        log.info('Question manually expired, agent resolver cleared', { questionId: id });
      }

      const response: ApiResponse<Question> = {
        success: true,
        data: updatedQuestion
      };
      return c.json(response);
    } catch (error) {
      const response: ApiResponse = {
        success: false,
        error: error instanceof Error ? error.message : 'Unknown error'
      };
      return c.json(response, 500);
    }
  });

  return app;
}
