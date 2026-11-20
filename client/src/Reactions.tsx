import type { LiveReaction, Question, ReactionKind } from './types';

const choices: { kind: ReactionKind; label: string; delta: string }[] = [
  { kind: 'like', label: 'Like the point', delta: '+1' },
  { kind: 'dislike', label: "Don't like this point", delta: '-1' },
  { kind: 'epic', label: 'EPIC POINT!', delta: '+3' },
];

export function ReactionButtons({ question, enabled, pending, onReact }: {
  question: Question; enabled: boolean; pending: boolean; onReact: (kind: ReactionKind) => void;
}) {
  return <div className="reaction-buttons" role="group" aria-label={`React to: ${question.text}`} aria-busy={pending}>
    {choices.map(choice => <button key={choice.kind} className={choice.kind === 'epic' ? 'button-primary' : ''}
      disabled={!enabled || pending || question.answered} aria-label={`${choice.label}: ${question.text}`}
      onClick={() => onReact(choice.kind)}>
      <span>{choice.label}</span><span className="reaction-delta" aria-hidden="true">{choice.delta}</span>
    </button>)}
  </div>;
}

export function ReactionOverlay({ reactions }: { reactions: LiveReaction[] }) {
  return <div className="reaction-overlay" aria-hidden="true">
    {reactions.map(reaction => <div key={reaction.id} className={`reaction-burst reaction-${reaction.kind}`}
      data-kind={reaction.kind} data-reaction-id={reaction.id}
      style={{ left: `${8 + (parseInt(reaction.id.replaceAll('-', '').slice(0, 8), 16) / 0xffffffff) * 84}%` }}>
      <ReactionArt kind={reaction.kind} />
    </div>)}
  </div>;
}

function ReactionArt({ kind }: { kind: ReactionKind }) {
  if (kind === 'like') return <svg viewBox="0 0 100 110">
    <path d="M50 18V7c0-7 12-7 12 0" fill="none" stroke="var(--cp-warning)" strokeWidth="5" />
    <rect x="37" y="16" width="26" height="13" rx="3" fill="var(--cp-warning)" />
    <circle cx="50" cy="65" r="37" fill="var(--cp-danger)" stroke="var(--cp-warning)" strokeWidth="3" />
    <path d="M17 56q16 18 33 0t33 0M17 75q16 18 33 0t33 0" fill="none" stroke="var(--cp-warning)" strokeWidth="5" />
    <path d="m36 38 3 6 7 1-5 5 1 7-6-3-6 3 1-7-5-5 7-1Z" fill="var(--cp-text)" />
  </svg>;
  if (kind === 'dislike') return <svg viewBox="0 0 110 120">
    <path d="m20 58-17-9 10 29 20-2m57-18 17-9-10 29-20-2" fill="var(--cp-warning)" />
    <path d="M23 56q0-30 32-30t32 30v22q-5 27-32 27T23 78Z" fill="var(--cp-warning)" />
    <path d="M17 49 51 3q5-7 15 0l28 24-19-3 10 25Z" fill="var(--cp-success)" />
    <path d="M19 48h68v12H19Z" fill="var(--cp-danger)" />
    <circle cx="94" cy="27" r="7" fill="var(--cp-text)" />
    <path d="m32 69 14 4m19 0 14-7" stroke="var(--cp-surface)" strokeWidth="4" strokeLinecap="round" />
    <circle cx="40" cy="78" r="3" fill="var(--cp-surface)" /><circle cx="72" cy="78" r="3" fill="var(--cp-surface)" />
    <path d="M39 88q18 21 37-3" fill="none" stroke="var(--cp-surface)" strokeWidth="4" strokeLinecap="round" />
    <path d="m30 104 25 8 25-8 9 16H21Z" fill="var(--cp-danger)" />
  </svg>;
  return <svg viewBox="0 0 120 125">
    <path d="M59 35C15 31 18-3 41 5q16 5 19 30C104-2 112 27 60 35" fill="none" stroke="var(--cp-warning)" strokeWidth="9" strokeLinejoin="round" />
    <rect x="13" y="52" width="94" height="68" rx="5" fill="var(--cp-danger)" />
    <rect x="7" y="33" width="106" height="25" rx="4" fill="var(--cp-danger)" stroke="var(--cp-warning)" strokeWidth="3" />
    <path d="M49 34h22v86H49Z" fill="var(--cp-warning)" />
    <path d="m6 11 2 6 7 2-7 2-2 7-2-7-4-2 4-2m106 58 2 6 6 2-6 2-2 6-2-6-6-2 6-2" fill="var(--cp-warning)" />
  </svg>;
}
