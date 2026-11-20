import { useEffect, useState } from 'react';
import type { Board } from './types';

export type TimerState = Pick<Board, 'timerStartedAt' | 'timerDurationSeconds' | 'serverTime'>;

export function DiscussionTimer({ state }: { state: TimerState }) {
  const [clock, setClock] = useState(() => ({ now: Date.now(), offset: Date.parse(state.serverTime) - Date.now() }));
  useEffect(() => {
    const now = Date.now();
    setClock({ now, offset: Date.parse(state.serverTime) - now });
    if (!state.timerStartedAt) return;
    const interval = setInterval(() => setClock(current => ({ ...current, now: Date.now() })), 250);
    return () => clearInterval(interval);
  }, [state.serverTime, state.timerStartedAt]);
  if (!state.timerStartedAt || state.timerDurationSeconds === null)
    return <p className="timer-idle">Timer not started</p>;
  const deadline = Date.parse(state.timerStartedAt) + state.timerDurationSeconds * 1000;
  const seconds = Math.max(0, Math.ceil((deadline - clock.now - clock.offset) / 1000));
  const formatted = `${Math.floor(seconds / 60).toString().padStart(2, '0')}:${(seconds % 60).toString().padStart(2, '0')}`;
  return <div className={`discussion-timer ${seconds === 0 ? 'timer-expired' : ''}`}>
    <span aria-live="polite">{seconds === 0 ? "Time's up" : 'Time remaining'}</span>
    <time role="timer" aria-label="Discussion time remaining" dateTime={`PT${seconds}S`}>{formatted}</time>
  </div>;
}
