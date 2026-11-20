import { fireEvent, render, screen, cleanup } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { CurrentActivity, QuestionCard } from './App';
import type { Question } from './types';

afterEach(cleanup);
const question: Question = {
  id: '1', text: 'What should we build?', category: 'Looking ahead', visible: true,
  answered: false, sortOrder: 0, score: 4, myScore: -1,
};
const artwork = { imageUrl: '/branding/christmas-fireside.png', imageDescription: 'Event poster' };

describe('Repeatable audience reactions', () => {
  it.each([
    ['Like the point', 'like'], ["Don't like this point", 'dislike'], ['EPIC POINT!', 'epic'],
  ])('sends the selected action: %s', (label, kind) => {
    const react = vi.fn();
    render(<CurrentActivity {...artwork} question={question} votingOpen canVote pending={false} onReact={react} />);
    fireEvent.click(screen.getByRole('button', { name: `${label}: ${question.text}` }));
    expect(react).toHaveBeenCalledWith(kind);
    expect(screen.getByText('4 points')).toBeInTheDocument();
  });

  it.each([
    { votingOpen: false, answered: false, pending: false },
    { votingOpen: true, answered: true, pending: false },
    { votingOpen: true, answered: false, pending: true },
  ])('disables all three actions when ineligible: %j', ({ votingOpen, pending, answered }) => {
    const react = vi.fn();
    render(<CurrentActivity {...artwork} question={{ ...question, answered }} votingOpen={votingOpen} canVote pending={pending} onReact={react} />);
    for (const button of screen.getAllByRole('button')) {
      expect(button).toBeDisabled();
      fireEvent.click(button);
    }
    expect(react).not.toHaveBeenCalled();
  });

  it('renders untrusted question text as text rather than markup', () => {
    const { container } = render(<QuestionCard question={{ ...question, text: '<img src=x onerror=alert(1)>' }} />);
    expect(container.querySelector('img')).toBeNull();
    expect(screen.getByRole('heading', { level: 3 })).toHaveTextContent('<img src=x onerror=alert(1)>');
  });
});

describe('Read-only question totals', () => {
  it('separates the total from this viewer net points without exposing vote controls', () => {
    render(<QuestionCard question={question} />);
    expect(screen.getByText('Total points').parentElement).toHaveTextContent('4');
    expect(screen.getByText('Your points').parentElement).toHaveTextContent('-1');
    expect(screen.queryByRole('button')).not.toBeInTheDocument();
  });

  it('does not present an unauthenticated viewer as having contributed zero', () => {
    render(<QuestionCard question={{ ...question, myScore: null }} />);
    expect(screen.getByText('Sign in to see')).toBeVisible();
    expect(screen.queryByRole('button')).not.toBeInTheDocument();
  });
});

describe('Current activity', () => {
  it('lets public viewers read the discussion without enabling reactions', () => {
    render(<CurrentActivity {...artwork} question={question} votingOpen canVote={false} pending={false} onReact={vi.fn()}>
      <a href="/auth/login">Sign in to vote</a>
    </CurrentActivity>);
    expect(screen.getByRole('heading', { name: question.text })).toBeVisible();
    expect(screen.getByText('Discussing now')).toBeVisible();
    expect(screen.queryByRole('button')).not.toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Sign in to vote' })).toHaveAttribute('href', '/auth/login');
  });

  it('allows the same reaction again instead of permanently disabling a voted question', () => {
    const react = vi.fn();
    render(<CurrentActivity {...artwork} question={question} votingOpen canVote pending={false} onReact={react} />);
    const like = screen.getByRole('button', { name: `Like the point: ${question.text}` });
    fireEvent.click(like);
    fireEvent.click(like);
    expect(react).toHaveBeenNthCalledWith(1, 'like');
    expect(react).toHaveBeenNthCalledWith(2, 'like');
    expect(like).toBeEnabled();
  });

  it('shows the configured landing image rather than pretending a queued question is current', () => {
    render(<CurrentActivity {...artwork} votingOpen canVote pending={false} onReact={vi.fn()} />);
    expect(screen.getByText('Waiting for the host')).toBeVisible();
    expect(screen.getByRole('img', { name: 'Event poster' })).toHaveAttribute('src', artwork.imageUrl);
    expect(screen.queryByRole('button')).not.toBeInTheDocument();
    expect(screen.getByText('Signed in and ready to vote.')).toBeVisible();
  });
});
