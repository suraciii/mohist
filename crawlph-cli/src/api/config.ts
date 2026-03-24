import { Router, Request, Response } from 'express';
import { ApiResponse, Config } from '../types';

export function createConfigRoutes(getConfig: () => Config, setConfig: (key: string, value: any) => void): Router {
  const router = Router();

  router.get('/', (_req: Request, res: Response): void => {
    try {
      const config = getConfig();
      const safeConfig = { ...config };
      if (safeConfig.githubToken) {
        safeConfig.githubToken = '***';
      }

      const response: ApiResponse<Partial<Config>> = {
        success: true,
        data: safeConfig
      };
      res.json(response);
    } catch (error) {
      const response: ApiResponse = {
        success: false,
        error: error instanceof Error ? error.message : 'Unknown error'
      };
      res.status(500).json(response);
    }
  });

  router.put('/:key', (req: Request, res: Response): void => {
    try {
      const key = req.params.key;
      const { value } = req.body;

      if (value === undefined) {
        const response: ApiResponse = {
          success: false,
          error: 'value is required'
        };
        res.status(400).json(response);
        return;
      }

      setConfig(key, value);

      const response: ApiResponse = {
        success: true,
        data: { key, value }
      };
      res.json(response);
    } catch (error) {
      const response: ApiResponse = {
        success: false,
        error: error instanceof Error ? error.message : 'Unknown error'
      };
      res.status(500).json(response);
    }
  });

  return router;
}
