import { act, renderHook, waitFor } from '@testing-library/react';
import { afterEach, expect, it, vi } from 'vitest';
import { HubConnectionBuilder } from '@microsoft/signalr';
import * as api from './api';
import type { Board } from './types';
import { useBoard } from './useBoard';

afterEach(() => vi.restoreAllMocks());

function deferred() {
  let resolve!: (value: Board) => void;
  const promise = new Promise<Board>(done => { resolve = done; });
  return { promise, resolve };
}

const board: Board = {
  title: 'Test', subtitle: '', votingOpen: true, currentQuestionId: null, revision: 1,
  panellists: [], questions: [], landingImageUrl: '', landingImageDescription: '',
  hasCustomLandingImage: false, speakerSeconds: 120, groupSeconds: 300, groupDiscussion: false,
  timerStartedAt: null, timerDurationSeconds: null, serverTime: '2026-01-01T00:00:00Z',
  scoreboardVisible: false,
};

it('applies a saved host revision even when a hub refresh starts before its response completes', async () => {
  const hub = new HubConnectionBuilder().withUrl('http://localhost/live').build();
  vi.spyOn(hub, 'start').mockResolvedValue();
  vi.spyOn(hub, 'stop').mockResolvedValue();
  vi.spyOn(HubConnectionBuilder.prototype, 'build').mockReturnValue(hub);
  const request = vi.spyOn(api, 'request').mockResolvedValue(board);
  const { result, unmount } = renderHook(() => useBoard(true, true));
  await waitFor(() => expect(result.current.board?.revision).toBe(1));
  const saveRefresh = deferred();
  const hubRefresh = deferred();
  request.mockReturnValueOnce(saveRefresh.promise).mockReturnValueOnce(hubRefresh.promise);
  let saved!: Promise<void>;
  let newer!: Promise<void>;
  act(() => { saved = result.current.refresh(); newer = result.current.refresh(); });
  await act(async () => { saveRefresh.resolve({ ...board, revision: 3 }); await saved; });
  expect(result.current.board?.revision).toBe(3);
  await act(async () => { hubRefresh.resolve({ ...board, revision: 2 }); await newer; });
  expect(result.current.board?.revision).toBe(3);
  unmount();
});
