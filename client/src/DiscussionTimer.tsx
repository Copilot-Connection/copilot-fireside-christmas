import { useEffect, useState } from 'react';
import type { Board } from './types';

export type TimerState = Pick<Board, 'timerStartedAt' | 'timerDurationSeconds' | 'serverTime'>;

export function useTimerRemaining(state?: TimerState) {
  const [clock, setClock] = useState(() => ({ now: Date.now(), offset: state ? Date.parse(state.serverTime) - Date.now() : 0 }));
  useEffect(() => {
    const now = Date.now();
    setClock({ now, offset: state ? Date.parse(state.serverTime) - now : 0 });
    if (!state?.timerStartedAt) return;
    const interval = setInterval(() => setClock(current => ({ ...current, now: Date.now() })), 250);
    return () => clearInterval(interval);
  }, [state?.serverTime, state?.timerStartedAt]);
  if (!state?.timerStartedAt || state.timerDurationSeconds === null) return null;
  const deadline = Date.parse(state.timerStartedAt) + state.timerDurationSeconds * 1000;
  return Math.max(0, deadline - clock.now - clock.offset);
}

export function DiscussionTimer({ state, warnWhenLow = false }: { state: TimerState; warnWhenLow?: boolean }) {
  const remaining = useTimerRemaining(state);
  if (remaining === null) return <p className="timer-idle">Timer not started</p>;
  const seconds = Math.ceil(remaining / 1000);
  const warning = warnWhenLow && remaining > 0 && remaining <= (state.timerDurationSeconds ?? 0) * 1000 * 0.15;
  const formatted = `${Math.floor(seconds / 60).toString().padStart(2, '0')}:${(seconds % 60).toString().padStart(2, '0')}`;
  return <div className={`discussion-timer ${seconds === 0 ? 'timer-expired' : warning ? 'timer-warning' : ''}`}>
    <span aria-live="polite">{seconds === 0 ? "Time's up" : warning ? 'Time running low' : 'Time remaining'}</span>
    <time role="timer" aria-label="Discussion time remaining" dateTime={`PT${seconds}S`}>{formatted}</time>
  </div>;
}
