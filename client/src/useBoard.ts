import { useCallback, useEffect, useRef, useState } from 'react';
import { HubConnectionBuilder, LogLevel } from '@microsoft/signalr';
import { request, errorMessage } from './api';
import type { Board } from './types';

export function useBoard(admin: boolean, enabled: boolean) {
  const [board, setBoard] = useState<Board | null>(null);
  const [error, setError] = useState('');
  const [connection, setConnection] = useState('Connecting');
  const generation = useRef(0);
  const mounted = useRef(false);
  const refresh = useCallback(async () => {
    const current = ++generation.current;
    try {
      const next = await request<Board>(admin ? '/api/admin/board' : '/api/board');
      if (mounted.current && current === generation.current) {
        setBoard(next);
        setError('');
      }
    } catch (error) {
      if (mounted.current && current === generation.current) setError(errorMessage(error));
    }
  }, [admin]);

  useEffect(() => {
    if (!enabled) return;
    mounted.current = true;
    let disposed = false;
    let retry: ReturnType<typeof setTimeout>;
    const hub = new HubConnectionBuilder().withUrl('/live').withAutomaticReconnect()
      .configureLogging(LogLevel.Warning).build();
    hub.on('boardChanged', refresh);
    hub.onreconnecting(() => !disposed && setConnection('Reconnecting'));
    hub.onreconnected(() => { if (!disposed) { setConnection('Live'); void refresh(); } });
    const start = async () => {
      try {
        await hub.start();
        if (!disposed) { setConnection('Live'); await refresh(); }
      } catch (error) {
        if (!disposed) {
          console.warn('Live connection unavailable; retrying', error);
          setConnection('Live updates interrupted');
          retry = setTimeout(() => { void start(); }, 5000);
        }
      }
    };
    hub.onclose(() => {
      if (!disposed) { setConnection('Live updates interrupted'); retry = setTimeout(() => { void start(); }, 5000); }
    });
    void refresh();
    void start();
    // Reconcile after missed notifications, background-tab suspension, and service interruptions.
    const interval = setInterval(() => { void refresh(); }, 15000);
    const onVisible = () => { if (document.visibilityState === 'visible') void refresh(); };
    document.addEventListener('visibilitychange', onVisible);
    return () => {
      disposed = true;
      mounted.current = false;
      generation.current++;
      clearInterval(interval);
      clearTimeout(retry);
      document.removeEventListener('visibilitychange', onVisible);
      void hub.stop().catch(error => console.warn('Live connection cleanup failed', error));
    };
  }, [enabled, refresh]);
  return { board, error, connection, refresh };
}
