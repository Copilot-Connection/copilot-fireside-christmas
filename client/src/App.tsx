import { useCallback, useEffect, useState, type FormEvent, type ReactNode } from 'react';
import { ArrowRight, ArrowUpRight, Check, ChevronDown, ChevronUp, Flame, MessagesSquare, Radio, ShieldCheck, Snowflake, Users } from 'lucide-react';
import { request, write, errorMessage } from './api';
import type { Question, Session } from './types';
import { useBoard } from './useBoard';
import { Admin } from './Admin';

export function App() {
  const [session, setSession] = useState<Session | null>(null);
  const [sessionError, setSessionError] = useState('');
  const adminPage = window.location.pathname.replace(/\/$/, '') === '/admin';
  const refreshSession = useCallback(async () => {
    try {
      setSession(await request<Session>('/api/session'));
      setSessionError('');
    } catch (error) { setSessionError(errorMessage(error)); }
  }, []);
  useEffect(() => { void refreshSession(); }, [refreshSession]);
  return <div className={`site-shell ${adminPage ? '' : 'fireside-surface'}`}>
    {!adminPage && <svg className="fireside-mesh" viewBox="0 0 1400 900" preserveAspectRatio="xMidYMid slice" aria-hidden="true">
      <path d="M0 80 260 10 180 310 0 460M260 10 530 180 180 310 380 590 0 460M530 180 780 20 900 350 530 180 380 590 800 720 900 350 1250 120 1400 260M780 20 1250 120 1400 0M900 350 1220 620 1400 420M380 590 240 900M800 720 1220 620 1400 900M800 720 780 900" />
    </svg>}
    <a className="skip-link" href="#main">Skip to content</a>
    <header className="site-header">
      <a className="brand" href="/" aria-label="Copilot Fireside Chat home">
        <Flame className="brand-symbol" size={28} aria-hidden="true" />
        <span>Copilot<span className="brand-sub">Fireside Chat</span></span>
      </a>
      <nav aria-label="Main navigation">
        <a href="/admin" aria-current={adminPage ? 'page' : undefined}><ShieldCheck size={16} aria-hidden="true" /> Host desk</a>
      </nav>
    </header>
    {session?.demo && <div className="demo-strip"><Snowflake size={15} aria-hidden="true" /> LOCAL DEMO
      <span>Sample panellists &amp; questions. Not a scheduled event.</span></div>}
    <main id="main">
      {sessionError ? <div className="center-state"><ErrorNotice message={sessionError} />
        <button onClick={() => void refreshSession()}>Try again</button></div>
        : !session ? <Loading /> : adminPage
          ? <Admin session={session} refreshSession={refreshSession} />
          : window.location.pathname === '/privacy' ? <Privacy />
          : session.participantName
            ? <Audience session={session} refreshSession={refreshSession} />
            : <Welcome session={session} onJoin={refreshSession} />}
    </main>
    <footer className="site-footer">
      <a className="footer-title" href="https://copilotfireside.chat/" target="_blank" rel="noreferrer">Visit Copilot Fireside Chat <ArrowUpRight size={14} aria-hidden="true" /></a>
      <a href="/privacy">Privacy &amp; participation</a>
    </footer>
  </div>;
}

export function ErrorNotice({ message }: { message: string }) {
  return <div className="error-notice" role="alert">{message}</div>;
}

export function Loading() {
  return <div className="center-state" role="status"><Flame className="loading-flame" size={32} aria-hidden="true" />
    <p>Warming things up...</p></div>;
}

function NameForm({ session, onSaved, initial = '' }: { session: Session; onSaved: () => Promise<void>; initial?: string }) {
  const [name, setName] = useState(initial);
  const [pending, setPending] = useState(false);
  const [error, setError] = useState('');
  async function submit(event: FormEvent) {
    event.preventDefault();
    setPending(true);
    setError('');
    try {
      await write('/api/participant', 'PUT', session.csrfToken, { name });
      await onSaved();
    } catch (error) { setError(errorMessage(error)); }
    finally { setPending(false); }
  }
  return <form className="name-form" onSubmit={submit}>
    <label htmlFor="participant-name">What should we call you?</label>
    <div className="input-action">
      <input id="participant-name" name="name" autoComplete="nickname" value={name}
        onChange={event => setName(event.target.value)} minLength={2} maxLength={60}
        required placeholder="Your display name" disabled={pending} />
      <button className="button-primary" disabled={pending || name.trim().length < 2}>
        {pending ? 'Joining...' : initial ? 'Save name' : 'Pull up a chair'} <ArrowRight size={18} aria-hidden="true" />
      </button>
    </div>
    {error && <ErrorNotice message={error} />}
    <p className="fine-print">No account needed to view. To upvote, sign in with a work or school Microsoft Entra account.
      An essential cookie remembers your display name for 30 days; your name is not public.</p>
  </form>;
}

function Welcome({ session, onJoin }: { session: Session; onJoin: () => Promise<void> }) {
  return <section className="welcome">
    <div className="welcome-copy">
      <h1>Come in.<br />Join the fireside.</h1>
      <p className="intro">The Christmas panel, one conversation at a time.
        Set your name to follow what is happening now.</p>
      <NameForm session={session} onSaved={onJoin} />
    </div>
    <figure className="welcome-photo">
      <img src="/branding/fireside-scene.jpg" alt="The existing Copilot Fireside Chat brand photograph, featuring a welcoming fireplace" />
      <figcaption>From the Copilot Fireside Chat community.<br />Brand photograph - not this event's confirmed panel.</figcaption>
    </figure>
  </section>;
}

function Audience({ session, refreshSession }: { session: Session; refreshSession: () => Promise<void> }) {
  const { board, error, connection, refresh } = useBoard(false, true);
  const [filter, setFilter] = useState<'all' | 'mine' | 'answered'>('all');
  const [pending, setPending] = useState<string | null>(null);
  const [voteError, setVoteError] = useState('');
  const [editName, setEditName] = useState(false);
  useEffect(() => { void refresh(); }, [session.canVote, refresh]);
  async function demoVoter() {
    setPending('sign-in');
    setVoteError('');
    try {
      await write('/auth/demo-voter', 'POST', session.csrfToken);
      await refreshSession();
    } catch (error) { setVoteError(errorMessage(error)); }
    finally { setPending(null); }
  }
  async function vote(id: string) {
    setPending(id);
    setVoteError('');
    try {
      await write(`/api/questions/${id}/vote`, 'POST', session.csrfToken);
      await refresh();
    } catch (error) { setVoteError(errorMessage(error)); }
    finally { setPending(null); }
  }
  if (!board) return error ? <div className="center-state"><ErrorNotice message={error} />
    <button onClick={() => void refresh()}>Try again</button></div> : <Loading />;
  const current = board.questions.find(q => q.id === board.currentQuestionId);
  const upNextCount = board.questions.filter(q => !q.answered && q.id !== current?.id).length;
  const questions = board.questions.filter(q => q.id !== current?.id && (filter === 'answered' ? q.answered :
    !q.answered && (filter === 'all' || q.hasVoted))).sort((a, b) => b.votes - a.votes || a.sortOrder - b.sortOrder);
  return <>
    <div className="audience-bar">
      <span className={`connection ${connection === 'Live' ? 'connected' : ''}`} role="status">
        <span className="status-dot" /> {connection === 'Live' ? 'Connected to the fireside' : connection}
      </span>
      <div className="audience-account"><button className="text-button" onClick={() => setEditName(!editName)}>Hi, {session.participantName} <span>Change name</span></button>
        {session.signedIn && <form action="/auth/logout" method="post">
          <input type="hidden" name="__RequestVerificationToken" value={session.csrfToken} />
          <button className="text-button" type="submit">Sign out of Microsoft</button>
        </form>}</div>
    </div>
    {editName && <div className="name-edit"><NameForm session={session} initial={session.participantName ?? ''}
      onSaved={async () => { await refreshSession(); setEditName(false); }} /></div>}
    <header className="event-heading"><h1>{board.title}</h1><p>{board.subtitle}</p></header>
    {error && <ErrorNotice message={`${error} The board may be out of date.`} />}
    {new URLSearchParams(location.search).has('authError') && <ErrorNotice message="Microsoft sign-in did not complete. Try again with a work or school account, or contact your organisation's administrator." />}
    <CurrentActivity question={current} votingOpen={board.votingOpen} canVote={session.canVote}
      pending={pending !== null} onVote={() => { if (current) void vote(current.id); }}>
      {!session.canVote && <div className="activity-signin">
        {session.demo ? <button className="button-primary" disabled={pending !== null} onClick={() => void demoVoter()}>
          Sign in as demo voter <ArrowRight size={17} aria-hidden="true" /></button>
          : <a className="button button-primary" href="/auth/login">Sign in with Microsoft to vote <ArrowRight size={17} aria-hidden="true" /></a>}
        <p>{session.demo ? 'Development-only sign-in simulation.' : 'Work or school accounts from any organisation. Viewing is public.'}</p>
      </div>}
    </CurrentActivity>
    {voteError && <ErrorNotice message={voteError} />}
    <div className="audience-details">
      <details className="questions-section">
        <summary><MessagesSquare size={19} aria-hidden="true" /> Questions &amp; votes
          <span>{upNextCount} up next</span><ChevronDown size={18} aria-hidden="true" /></summary>
        <div className="disclosure-content">
        <div className="queue-heading"><h2>Choose what comes next</h2><span className="voting-state">{board.votingOpen ? 'Voting open' : 'Voting paused'}</span></div>
        {!session.canVote && <p className="section-description">Sign in above to upvote with your organisational Entra account.</p>}
        <div className="tabs" aria-label="Filter questions">
          <button aria-pressed={filter === 'all'} onClick={() => setFilter('all')}>Up next <span>{upNextCount}</span></button>
          <button aria-pressed={filter === 'mine'} onClick={() => setFilter('mine')}>My votes</button>
          <button aria-pressed={filter === 'answered'} onClick={() => setFilter('answered')}>Answered <Check size={14} aria-hidden="true" /></button>
        </div>
        <div className="question-list">
          {questions.length ? questions.map(question => <QuestionCard key={question.id} question={question}
            votingOpen={board.votingOpen && session.canVote}
            pending={pending !== null} onVote={() => void vote(question.id)} />)
            : <div className="empty-state"><p>{filter === 'mine' ? 'Your other voted questions will appear here.' :
              filter === 'answered' ? 'Answered questions will appear here.' : 'The host will share more questions here.'}</p></div>}
        </div>
        <p className="fine-print voting-note"><ShieldCheck size={15} aria-hidden="true" /> One vote per question, per organisational Entra identity.
          Changing your name, clearing cookies or using another browser does not reset votes for that identity.</p>
        </div>
      </details>
      <details className="panel-section">
        <summary><Users size={19} aria-hidden="true" /> Meet the panel
          <span>{board.panellists.length} panellists</span><ChevronDown size={18} aria-hidden="true" /></summary>
        <div className="disclosure-content">
          {board.panellists.length ? board.panellists.map(person => <article className="person-card" key={person.id}>
            <h3>{person.name}</h3><span className="person-role">{person.role}</span>
            {person.bio && <p>{person.bio}</p>}
          </article>) : <p className="section-description">The host hasn't announced the panel yet.</p>}
        </div>
      </details>
    </div>
  </>;
}

export function CurrentActivity({ question, votingOpen, canVote, pending, onVote, children }: {
  question?: Question; votingOpen: boolean; canVote: boolean; pending: boolean;
  onVote: () => void; children?: ReactNode;
}) {
  return <section className={`activity-stage ${question ? 'current-question' : 'waiting-activity'}`} aria-labelledby="activity-title">
    <div className="activity-copy" key={question?.id ?? 'waiting'}>
      <h2 id="activity-title" aria-live="polite" aria-atomic="true">{question?.text ?? 'A seat by the fire.'}</h2>
      <p className="activity-description">{question?.category || (question ? 'The current conversation.' :
        'The host is setting the scene. The next discussion will appear here automatically.')}</p>
      <div className="activity-meta"><span><Radio size={17} aria-hidden="true" />{question ? 'Discussing now' : 'Waiting for the host'}</span>
        {question && <span>{question.votes} {question.votes === 1 ? 'vote' : 'votes'}</span>}</div>
      {canVote && question && <button className="button-primary activity-vote" onClick={onVote}
        disabled={pending || question.hasVoted || !votingOpen || question.answered}
        aria-label={`${question.hasVoted ? 'Voted for' : 'Upvote'}: ${question.text}`} aria-pressed={question.hasVoted}>
        {question.hasVoted ? <Check size={18} aria-hidden="true" /> : <ChevronUp size={20} aria-hidden="true" />}
        {question.hasVoted ? 'Your vote is in' : !votingOpen ? 'Voting paused' : pending ? 'Saving vote...' : 'Upvote this question'}
      </button>}
      {canVote && !question && <p className="ready-note"><Check size={16} aria-hidden="true" /> Signed in and ready to vote.</p>}
      {children}
    </div>
    <figure className="activity-photo">
      <img src="/branding/fireside-scene.jpg" alt="" width={2280} height={1522} />
      <figcaption>Original show photograph,<br />not this event's confirmed panel.</figcaption>
    </figure>
  </section>;
}

export function QuestionCard({ question, votingOpen, pending, onVote }: {
  question: Question; votingOpen: boolean; pending: boolean; onVote: () => void;
}) {
  return <article className="question-card">
    <button className={`vote-button ${question.hasVoted ? 'has-voted' : ''}`}
      onClick={onVote} disabled={pending || question.hasVoted || !votingOpen || question.answered}
      aria-label={`${question.hasVoted ? 'Voted for' : 'Upvote'}: ${question.text}`}
      aria-pressed={question.hasVoted}>
      {question.hasVoted ? <Check size={19} aria-hidden="true" /> : <ChevronUp size={22} aria-hidden="true" />}
      <strong>{question.votes}</strong><span>{question.hasVoted ? 'VOTED' : 'VOTES'}</span>
    </button>
    <div><div className="question-tags"><span>{question.category || 'Around the fire'}</span>
      {question.answered && <span>Answered</span>}</div>
      <h3>{question.text}</h3></div>
  </article>;
}

export function Surface({ title, children }: { title: string; children: ReactNode }) {
  return <section className="admin-surface"><h2>{title}</h2>{children}</section>;
}

export function Privacy() {
  return <article className="privacy-page">
    <h1>Privacy &amp; participation</h1>
    <p>You don't need a Microsoft account to view the audience page. Set a display name to enter.
      Upvoting requires a work or school Microsoft Entra account from any organisation.
      Your display name is stored in a protected, HTTP-only essential cookie, not in the vote database or a public attendee list.</p>
    <h2>What we store</h2><p>The display-name cookie lasts 30 days. When you sign in, a separate protected authentication
      cookie holds identity claims for up to two hours. The vote database stores a stable hash derived from your Entra
      tenant and user object identifiers against each question you upvote; it does not store raw identifiers,
      your display name or email. This is pseudonymous personal data, not anonymous data.</p>
    <h2>Voting follows your account, not your browser</h2><p>The server allows one vote per question per organisational
      Entra tenant/user identity. Signing back in after clearing cookies or on another browser keeps that identity's
      votes. Different organisational accounts or guest identities may represent the same human; the app does not
      claim one vote per verified human. Some organisations require administrator consent before sign-in.</p>
    <h2>Administrators</h2><p>The host desk requires both the configured host tenant and an explicitly assigned
      Fireside.Admin application role. A role assigned in another tenant does not grant host access.
      Microsoft handles sign-in for voters and administrators.</p>
    <h2>Retention and support</h2><p>Event hosts control the questions and vote records and must publish their contact
      and event retention policy before going live. Removing a question also removes its votes.
      Hosting services may retain operational logs according to the host's Azure settings; this application does not
      log display names or vote bodies.</p><a href="/">Back to the fireside <ArrowRight size={16} aria-hidden="true" /></a>
  </article>;
}
