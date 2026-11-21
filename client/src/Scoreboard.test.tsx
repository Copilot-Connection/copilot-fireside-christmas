import { fireEvent, render, screen, cleanup, waitFor } from '@testing-library/react';
import { afterEach, expect, it, vi } from 'vitest';
import { Scoreboard } from './Scoreboard';
import { Audience } from './App';
import { Admin } from './Admin';
import * as api from './api';
import { useBoard } from './useBoard';
import type { Board, Scoreboard as Scores, Session } from './types';

vi.mock('./useBoard', () => ({ useBoard: vi.fn() }));
afterEach(() => { cleanup(); vi.restoreAllMocks(); });
const scores: Scores = {
  speakers: [{ id: 's', name: 'Sample speaker', points: 12, removed: false }],
  questions: [{ id: 'q', text: 'Sample question', scores: [{ speakerId: 's', points: 12 }], unassignedPoints: 0 }],
};

it('shows per-question and total points, with edit actions only for the host', () => {
  const edit = vi.fn();
  const { rerender } = render(<Scoreboard scores={scores} />);
  expect(screen.getByText('Speaker totals')).toBeVisible();
  expect(screen.getByLabelText('Sample speaker: 12 points')).toBeVisible();
  expect(screen.queryByRole('button')).not.toBeInTheDocument();
  rerender(<Scoreboard scores={scores} onEdit={edit} />);
  fireEvent.click(screen.getByRole('button', { name: 'Edit points for Sample speaker: Sample question' }));
  expect(edit).toHaveBeenCalledWith(scores.questions[0], scores.speakers[0], 12);
});

it('uses uploaded portraits beside names and handles broken images accessibly', () => {
  render(<Scoreboard scores={scores} people={[{ id: 's', name: 'Sample speaker', role: '', type: 'guest', bio: '',
    visible: true, sortOrder: 0, photoUrl: '/api/panellists/s/photo?v=1' }]} />);
  const portraits = screen.getAllByRole('img', { name: 'Portrait of Sample speaker' });
  expect(portraits).toHaveLength(2);
  expect(portraits[0]).toHaveAttribute('src', '/api/panellists/s/photo?v=1');
  fireEvent.error(portraits[0]);
  expect(screen.getByRole('img', { name: 'Photo could not load for Sample speaker' })).toBeVisible();
});

it('allows a score draft to be cleared and changed to a negative total', async () => {
  const board: Board = {
    title: 'Test', subtitle: '', votingOpen: false, currentQuestionId: null, revision: 1,
    panellists: [], questions: [], landingImageUrl: '', landingImageDescription: '',
    hasCustomLandingImage: false, speakerSeconds: 120, groupSeconds: 300, groupDiscussion: false,
    timerStartedAt: null, timerDurationSeconds: null, serverTime: '2026-01-01T00:00:00Z',
    scoreboardVisible: false, scoreboard: scores,
  };
  const refresh = vi.fn().mockResolvedValue(undefined);
  vi.mocked(useBoard).mockReturnValue({ board, error: '', connection: 'Live', refresh, reactions: [] });
  const write = vi.spyOn(api, 'write').mockResolvedValue(undefined);
  render(<Admin session={{ participantName: null, isAdmin: true, signedIn: true, canVote: true,
    adminName: 'Test host', demo: true, csrfToken: 'test' }} refreshSession={refresh} />);
  fireEvent.click(screen.getByRole('heading', { name: /Speaker scores/ }));
  fireEvent.click(screen.getByRole('button', { name: 'Edit points for Sample speaker: Sample question' }));
  const points = screen.getByLabelText('Points', { exact: true });
  fireEvent.change(points, { target: { value: '' } });
  expect(points).toHaveDisplayValue('');
  fireEvent.change(points, { target: { value: '-2' } });
  fireEvent.change(screen.getByLabelText('Reason for change'), { target: { value: 'Correction' } });
  fireEvent.click(screen.getByRole('button', { name: 'Save changes' }));
  await waitFor(() => expect(write).toHaveBeenCalledWith('/api/admin/questions/q/scores/s', 'PUT', 'test',
    { points: -2, reason: 'Correction' }, 1));
});

it('requires confirmation before resetting all scores', async () => {
  const board: Board = {
    title: 'Test', subtitle: '', votingOpen: false, currentQuestionId: null, revision: 1,
    panellists: [], questions: [], landingImageUrl: '', landingImageDescription: '',
    hasCustomLandingImage: false, speakerSeconds: 120, groupSeconds: 300, groupDiscussion: false,
    timerStartedAt: null, timerDurationSeconds: null, serverTime: '2026-01-01T00:00:00Z',
    scoreboardVisible: true, scoreboard: scores,
  };
  const refresh = vi.fn().mockResolvedValue(undefined);
  vi.mocked(useBoard).mockReturnValue({ board, error: '', connection: 'Live', refresh, reactions: [] });
  const write = vi.spyOn(api, 'write').mockResolvedValue(undefined);
  const confirm = vi.spyOn(window, 'confirm').mockReturnValueOnce(false).mockReturnValueOnce(true);
  render(<Admin session={{ participantName: null, isAdmin: true, signedIn: true, canVote: false,
    adminName: 'Test host', demo: true, csrfToken: 'test' }} refreshSession={refresh} />);
  fireEvent.click(screen.getByRole('heading', { name: /Speaker scores/ }));
  fireEvent.click(screen.getByRole('button', { name: 'Reset scores' }));
  expect(write).not.toHaveBeenCalled();
  fireEvent.click(screen.getByRole('button', { name: 'Reset scores' }));
  expect(confirm).toHaveBeenLastCalledWith(expect.stringContaining('personal points'));
  await waitFor(() => expect(write).toHaveBeenCalledWith('/api/admin/scores/reset', 'POST', 'test', undefined, 1));
});

it('removes a hidden scoreboard and immediately returns its viewers to Live', () => {
  const board: Board = {
    title: 'Test event', subtitle: '', votingOpen: false, currentQuestionId: null, revision: 1,
    panellists: [], questions: [], landingImageUrl: '/poster.png', landingImageDescription: 'Poster',
    hasCustomLandingImage: false, speakerSeconds: 120, groupSeconds: 300, groupDiscussion: false,
    timerStartedAt: null, timerDurationSeconds: null, serverTime: '2026-01-01T00:00:00Z',
    scoreboardVisible: true, scoreboard: scores,
  };
  const session: Session = { participantName: 'Viewer', isAdmin: false, signedIn: false, canVote: false,
    adminName: null, demo: false, csrfToken: 'test' };
  const refresh = vi.fn().mockResolvedValue(undefined);
  vi.mocked(useBoard).mockReturnValue({ board, error: '', connection: 'Live', refresh, reactions: [] });
  const { rerender } = render(<Audience session={session} refreshSession={refresh} />);
  fireEvent.click(screen.getByRole('tab', { name: 'Scoreboard' }));
  expect(screen.getByRole('tabpanel', { name: 'Scoreboard' })).toBeVisible();
  vi.mocked(useBoard).mockReturnValue({ board: { ...board, scoreboardVisible: false, scoreboard: undefined },
    error: '', connection: 'Live', refresh, reactions: [] });
  rerender(<Audience session={session} refreshSession={refresh} />);
  expect(screen.queryByRole('tab', { name: 'Scoreboard' })).not.toBeInTheDocument();
  expect(screen.queryByText('Sample speaker')).not.toBeInTheDocument();
  expect(screen.getByRole('tab', { name: 'Live' })).toHaveAttribute('aria-selected', 'true');
  expect(screen.getByRole('tab', { name: 'Live' })).toHaveFocus();
});
