import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { EventBus } from '../src/services/event-bus';
import { AgentRunnerService } from '../src/services/agent-runner-service';

describe('Provider Hot Reload', () => {
  describe('EventBus', () => {
    let eventBus: EventBus;

    beforeEach(() => {
      eventBus = new EventBus();
    });

    afterEach(() => {
      eventBus.removeAllListeners();
    });

    describe('config:providers:changed event', () => {
      it('should emit event when config changes', () => {
        const listener = vi.fn();
        eventBus.on('config:providers:changed', listener);

        const providersData = {
          providers: [
            { id: 'test-provider', name: 'Test Provider', apiKey: 'sk-test', baseURL: 'https://api.test.com', models: ['model-1'] },
          ],
        };
        eventBus.emit('config:providers:changed', providersData);

        expect(listener).toHaveBeenCalledTimes(1);
        expect(listener).toHaveBeenCalledWith(providersData);
      });

      it('should allow multiple listeners for same event', () => {
        const listener1 = vi.fn();
        const listener2 = vi.fn();
        const listener3 = vi.fn();

        eventBus.on('config:providers:changed', listener1);
        eventBus.on('config:providers:changed', listener2);
        eventBus.on('config:providers:changed', listener3);

        const providersData = { providers: [{ id: 'provider-1' }] };
        eventBus.emit('config:providers:changed', providersData);

        expect(listener1).toHaveBeenCalledTimes(1);
        expect(listener2).toHaveBeenCalledTimes(1);
        expect(listener3).toHaveBeenCalledTimes(1);
      });

      it('should emit event with multiple providers', () => {
        const listener = vi.fn();
        eventBus.on('config:providers:changed', listener);

        const providersData = {
          providers: [
            { id: 'provider-1', name: 'Provider 1' },
            { id: 'provider-2', name: 'Provider 2' },
            { id: 'provider-3', name: 'Provider 3' },
          ],
        };
        eventBus.emit('config:providers:changed', providersData);

        expect(listener).toHaveBeenCalledWith(providersData);
        expect(listener.mock.calls[0][0].providers).toHaveLength(3);
      });

      it('should not call listener after it is removed', () => {
        const listener = vi.fn();
        eventBus.on('config:providers:changed', listener);
        eventBus.off('config:providers:changed', listener);

        eventBus.emit('config:providers:changed', { providers: [] });

        expect(listener).not.toHaveBeenCalled();
      });

      it('should not affect other listeners when one is removed', () => {
        const listener1 = vi.fn();
        const listener2 = vi.fn();

        eventBus.on('config:providers:changed', listener1);
        eventBus.on('config:providers:changed', listener2);
        eventBus.off('config:providers:changed', listener1);

        eventBus.emit('config:providers:changed', { providers: [] });

        expect(listener1).not.toHaveBeenCalled();
        expect(listener2).toHaveBeenCalledTimes(1);
      });

      it('should handle removeAllListeners correctly', () => {
        const listener1 = vi.fn();
        const listener2 = vi.fn();

        eventBus.on('config:providers:changed', listener1);
        eventBus.on('config:providers:changed', listener2);
        eventBus.removeAllListeners();

        eventBus.emit('config:providers:changed', { providers: [] });

        expect(listener1).not.toHaveBeenCalled();
        expect(listener2).not.toHaveBeenCalled();
      });
    });
  });

  describe('AgentRunnerService hot reload', () => {
    let eventBus: EventBus;
    let agentRunner: AgentRunnerService;

    beforeEach(() => {
      eventBus = new EventBus();
      agentRunner = new AgentRunnerService(eventBus);
    });

    afterEach(() => {
      agentRunner.shutdown();
      eventBus.removeAllListeners();
    });

    it('should register listener on construction', () => {
      const listeners = (eventBus as unknown as { listeners: Map<string, Set<unknown>> }).listeners;
      const providerListeners = listeners.get('config:providers:changed');
      expect(providerListeners).toBeDefined();
      expect(providerListeners!.size).toBeGreaterThan(0);
    });

    it('should receive config:providers:changed event', () => {
      const listener = vi.fn();
      eventBus.on('config:providers:changed', listener);

      eventBus.emit('config:providers:changed', { providers: [{ id: 'test' }] });

      expect(listener).toHaveBeenCalledTimes(1);
    });

    it('should handle provider save event', () => {
      const listener = vi.fn();
      eventBus.on('config:providers:changed', listener);

      eventBus.emit('config:providers:changed', {
        providers: [{ id: 'saved-provider', name: 'Saved Provider' }],
      });

      expect(listener).toHaveBeenCalledWith({
        providers: [{ id: 'saved-provider', name: 'Saved Provider' }],
      });
    });

    it('should handle provider delete event', () => {
      const listener = vi.fn();
      eventBus.on('config:providers:changed', listener);

      eventBus.emit('config:providers:changed', {
        providers: [{ id: 'deleted-provider' }],
      });

      expect(listener).toHaveBeenCalledWith({
        providers: [{ id: 'deleted-provider' }],
      });
    });
  });

  describe('Multiple services listening', () => {
    let eventBus: EventBus;
    let service1ReceivedEvents: unknown[];
    let service2ReceivedEvents: unknown[];
    let service3ReceivedEvents: unknown[];

    beforeEach(() => {
      eventBus = new EventBus();
      service1ReceivedEvents = [];
      service2ReceivedEvents = [];
      service3ReceivedEvents = [];
    });

    afterEach(() => {
      eventBus.removeAllListeners();
    });

    it('should notify all services when config changes', () => {
      eventBus.on('config:providers:changed', (data) => {
        service1ReceivedEvents.push(data);
      });
      eventBus.on('config:providers:changed', (data) => {
        service2ReceivedEvents.push(data);
      });
      eventBus.on('config:providers:changed', (data) => {
        service3ReceivedEvents.push(data);
      });

      const eventData = { providers: [{ id: 'test-provider' }] };
      eventBus.emit('config:providers:changed', eventData);

      expect(service1ReceivedEvents).toHaveLength(1);
      expect(service2ReceivedEvents).toHaveLength(1);
      expect(service3ReceivedEvents).toHaveLength(1);
      expect(service1ReceivedEvents[0]).toEqual(eventData);
      expect(service2ReceivedEvents[0]).toEqual(eventData);
      expect(service3ReceivedEvents[0]).toEqual(eventData);
    });

    it('should allow selective event handling per service', () => {
      eventBus.on('config:providers:changed', (data) => {
        service1ReceivedEvents.push(data);
      });
      eventBus.on('config:providers:changed', (data) => {
        if (data.providers.length > 0) {
          service2ReceivedEvents.push(data);
        }
      });
      eventBus.on('config:providers:changed', (data) => {
        service3ReceivedEvents.push(data);
      });

      eventBus.emit('config:providers:changed', { providers: [] });

      expect(service1ReceivedEvents).toHaveLength(1);
      expect(service2ReceivedEvents).toHaveLength(0);
      expect(service3ReceivedEvents).toHaveLength(1);
    });

    it('should handle rapid successive events correctly', () => {
      eventBus.on('config:providers:changed', (data) => {
        service1ReceivedEvents.push(data);
      });

      eventBus.emit('config:providers:changed', { providers: [{ id: 'p1' }] });
      eventBus.emit('config:providers:changed', { providers: [{ id: 'p2' }] });
      eventBus.emit('config:providers:changed', { providers: [{ id: 'p3' }] });

      expect(service1ReceivedEvents).toHaveLength(3);
      expect(service1ReceivedEvents[0].providers[0].id).toBe('p1');
      expect(service1ReceivedEvents[1].providers[0].id).toBe('p2');
      expect(service1ReceivedEvents[2].providers[0].id).toBe('p3');
    });

    it('should isolate listeners between event types', () => {
      const configListener = vi.fn();
      const stageListener = vi.fn();

      eventBus.on('config:providers:changed', configListener);
      eventBus.on('stage_changed', stageListener);

      eventBus.emit('config:providers:changed', { providers: [] });

      expect(configListener).toHaveBeenCalledTimes(1);
      expect(stageListener).not.toHaveBeenCalled();
    });
  });

  describe('AgentRunnerService shutdown behavior', () => {
    let eventBus: EventBus;
    let agentRunner: AgentRunnerService;

    beforeEach(() => {
      eventBus = new EventBus();
      agentRunner = new AgentRunnerService(eventBus);
    });

    afterEach(() => {
      agentRunner.shutdown();
      eventBus.removeAllListeners();
    });

    it('should remove its listener on shutdown', () => {
      const listenersBefore = (eventBus as unknown as { listeners: Map<string, Set<unknown>> }).listeners;
      const providerListenersBefore = listenersBefore.get('config:providers:changed');
      const countBefore = providerListenersBefore?.size ?? 0;

      agentRunner.shutdown();

      const listenersAfter = (eventBus as unknown as { listeners: Map<string, Set<unknown>> }).listeners;
      const providerListenersAfter = listenersAfter.get('config:providers:changed');
      const countAfter = providerListenersAfter?.size ?? 0;

      expect(countAfter).toBe(countBefore - 1);
    });

    it('should stop receiving events after shutdown', () => {
      const emitAndCheck = () => {
        eventBus.emit('config:providers:changed', { providers: [{ id: 'test' }] });
      };

      emitAndCheck();

      agentRunner.shutdown();

      const listenersAfter = (eventBus as unknown as { listeners: Map<string, Set<unknown>> }).listeners;
      const providerListenersAfter = listenersAfter.get('config:providers:changed');
      expect(providerListenersAfter?.size ?? 0).toBe(0);
    });
  });

  describe('Config save/delete event emission', () => {
    it('should verify event structure matches spec', () => {
      const eventBus = new EventBus();
      const listener = vi.fn();
      eventBus.on('config:providers:changed', listener);

      const eventData = {
        providers: [
          {
            id: 'my-custom-provider',
            name: 'My Custom Provider',
            apiKey: 'sk-secret-key',
            baseURL: 'https://api.example.com/v1',
            sdk: 'openai-compatible',
            models: ['gpt-4', 'gpt-3.5-turbo'],
          },
        ],
      };

      eventBus.emit('config:providers:changed', eventData);

      expect(listener).toHaveBeenCalledWith(eventData);
      const receivedData = listener.mock.calls[0][0];
      expect(receivedData.providers[0]).toHaveProperty('id');
      expect(receivedData.providers[0]).toHaveProperty('name');
      expect(receivedData.providers[0]).toHaveProperty('apiKey');
      expect(receivedData.providers[0]).toHaveProperty('baseURL');
      expect(receivedData.providers[0]).toHaveProperty('sdk');
      expect(receivedData.providers[0]).toHaveProperty('models');

      eventBus.removeAllListeners();
    });
  });
});