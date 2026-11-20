import { fireEvent, render, screen, cleanup } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { CurrentActivity, QuestionCard } from './App';
import type { Question } from './types';

afterEach(cleanup);
const question: Question = {
  id: '1', text: 'What should we build?', category: 'Looking ahead', visible: true,
  answered: false, sortOrder: 0, votes: 4, hasVoted: false,
};
describe('Audience question voting', () => {
  it('lets an eligible participant vote and presents server vote totals', () => {
    const vote = vi.fn();
    render(<QuestionCard question={question} votingOpen pending={false} onVote={vote} />);
    fireEvent.click(screen.getByRole('button', { name: 'Upvote: What should we build?' }));
    expect(vote).toHaveBeenCalledOnce();
    expect(screen.getByText('4')).toBeInTheDocument();
  });
  it.each([
    { hasVoted: true, votingOpen: true, answered: false, pending: false },
    { hasVoted: false, votingOpen: false, answered: false, pending: false },
    { hasVoted: false, votingOpen: true, answered: true, pending: false },
    { hasVoted: false, votingOpen: true, answered: false, pending: true },
  ])('disables ineligible votes: %j', ({ votingOpen, pending, ...flags }) => {
    const vote = vi.fn();
    render(<QuestionCard question={{ ...question, ...flags }}
      votingOpen={votingOpen} pending={pending} onVote={vote} />);
    fireEvent.click(screen.getByRole('button'));
    expect(screen.getByRole('button')).toBeDisabled();
    expect(vote).not.toHaveBeenCalled();
  });
  it('renders untrusted question text as text rather than markup', () => {
    const { container } = render(<QuestionCard question={{ ...question, text: '<img src=x onerror=alert(1)>' }}
      votingOpen pending={false} onVote={vi.fn()} />);
    expect(container.querySelector('img')).toBeNull();
    expect(screen.getByRole('heading', { level: 3 })).toHaveTextContent('<img src=x onerror=alert(1)>');
  });

  describe('Current activity', () => {
    it('lets public viewers read the discussion without enabling a vote', () => {
      render(<CurrentActivity question={question} votingOpen canVote={false} pending={false} onVote={vi.fn()}>
        <a href="/auth/login">Sign in to vote</a>
      </CurrentActivity>);
      expect(screen.getByRole('heading', { name: question.text })).toBeVisible();
      expect(screen.getByText('Discussing now')).toBeVisible();
      expect(screen.queryByRole('button')).not.toBeInTheDocument();
      expect(screen.getByRole('link', { name: 'Sign in to vote' })).toHaveAttribute('href', '/auth/login');
    });

    it('lets an eligible voter upvote the current discussion directly', () => {
      const vote = vi.fn();
      render(<CurrentActivity question={question} votingOpen canVote pending={false} onVote={vote} />);
      fireEvent.click(screen.getByRole('button', { name: `Upvote: ${question.text}` }));
      expect(vote).toHaveBeenCalledOnce();
      expect(screen.getByText('4 votes')).toBeVisible();
    });

    it.each([
      { hasVoted: true, votingOpen: true, pending: false, label: 'Your vote is in' },
      { hasVoted: false, votingOpen: false, pending: false, label: 'Voting paused' },
      { hasVoted: false, votingOpen: true, pending: true, label: 'Saving vote...' },
    ])('makes the current vote state explicit: $label', ({ hasVoted, votingOpen, pending, label }) => {
      render(<CurrentActivity question={{ ...question, hasVoted }} votingOpen={votingOpen}
        canVote pending={pending} onVote={vi.fn()} />);
      expect(screen.getByRole('button')).toBeDisabled();
      expect(screen.getByRole('button')).toHaveTextContent(label);
    });

    it('does not present a queued question as the current discussion while waiting', () => {
      render(<CurrentActivity votingOpen canVote pending={false} onVote={vi.fn()} />);
      expect(screen.getByText('Waiting for the host')).toBeVisible();
      expect(screen.queryByRole('button')).not.toBeInTheDocument();
      expect(screen.getByText('Signed in and ready to vote.')).toBeVisible();
    });
  });
});
