import { fireEvent, render, screen, cleanup } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { QuestionCard } from './App';
import type { Question } from './types';

afterEach(cleanup);
const question: Question = {
  id: '1', text: 'What should we build?', category: 'Looking ahead', visible: true,
  answered: false, sortOrder: 0, votes: 4, hasVoted: false,
};
describe('Audience question voting', () => {
  it('lets an eligible participant vote and presents server vote totals', () => {
    const vote = vi.fn();
    render(<QuestionCard question={question} current={false} votingOpen pending={false} onVote={vote} />);
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
    render(<QuestionCard question={{ ...question, ...flags }} current={false}
      votingOpen={votingOpen} pending={pending} onVote={vote} />);
    fireEvent.click(screen.getByRole('button'));
    expect(screen.getByRole('button')).toBeDisabled();
    expect(vote).not.toHaveBeenCalled();
  });
  it('renders untrusted question text as text rather than markup', () => {
    const { container } = render(<QuestionCard question={{ ...question, text: '<img src=x onerror=alert(1)>' }}
      current={false} votingOpen pending={false} onVote={vi.fn()} />);
    expect(container.querySelector('img')).toBeNull();
    expect(screen.getByRole('heading', { level: 3 })).toHaveTextContent('<img src=x onerror=alert(1)>');
  });
});
