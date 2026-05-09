import type { SessionState } from './session-observer';
import { Log } from '../util/log';

const log = Log.create({ service: 'session-state' });

export const VALID_TRANSITIONS: Record<SessionState, SessionState[]> = {
  initializing: ['running', 'failed', 'timeout'],
  running: ['completed', 'failed', 'timeout', 'cancelled', 'probing'],
  probing: ['running', 'failed', 'timeout', 'cancelled'],
  completed: ['closed'],
  failed: ['closed'],
  timeout: ['closed'],
  cancelled: ['closed'],
  closed: [],
};

export class SessionStateMachine {
  private _current: SessionState;

  constructor(initial: SessionState) {
    this._current = initial;
  }

  get current(): SessionState {
    return this._current;
  }

  canTransition(to: SessionState): boolean {
    return VALID_TRANSITIONS[this._current].includes(to);
  }

  transition(to: SessionState): void {
    if (!this.canTransition(to)) {
      throw new Error(
        `Invalid session state transition: ${this._current} → ${to}. ` +
        `Valid transitions from '${this._current}': [${VALID_TRANSITIONS[this._current].join(', ')}]`,
      );
    }

    const from = this._current;
    this._current = to;
    log.info('Session state transition', { from, to });
  }
}
