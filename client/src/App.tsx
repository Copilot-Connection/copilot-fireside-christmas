import { useCallback, useEffect, useState, type FormEvent, type ReactNode } from 'react';
import { ArrowDown, ArrowRight, ArrowUp, Check, ChevronUp, Flame, Radio, ShieldCheck, Snowflake, Users } from 'lucide-react';
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
  return <div className="site-shell">
    <a className="skip-link" href="#main">Skip to content</a>
    <header className="site-header">
      <a className="brand" href="/" aria-label="Copilot Fireside Chat home">
        <span className="brand-symbol"><Flame size={25} aria-hidden="true" /></span>
        <span>copilot<span className="brand-sub">FIRESIDE CHAT</span></span>
      </a>
      <nav aria-label="Main navigation">
        <a href="/" aria-current={!adminPage ? 'page' : undefined}>The fireside</a>
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
      <a className="footer-title" href="https://copilotfireside.chat/" target="_blank" rel="noreferrer">Copilot Fireside Chat <ArrowUp size={14} aria-hidden="true" /></a>
      <span>Good company. Curious minds. A warmer kind of conversation.</span>
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
      <p className="eyebrow"><Snowflake size={16} aria-hidden="true" /> THE CHRISTMAS EDITION</p>
      <h1>A little warmth.<br />A lot of <em>possibilities.</em></h1>
      <p className="intro">Welcome to the Copilot Fireside Chat. Settle in, meet the panel,
        and help shape the conversation - one good question at a time.</p>
      <div className="welcome-steps">
        <span><Users size={18} aria-hidden="true" /> Meet the panel</span>
        <span><ChevronUp size={20} aria-hidden="true" /> Vote for your favourites</span>
        <span><Radio size={18} aria-hidden="true" /> Follow along live</span>
      </div>
      <NameForm session={session} onSaved={onJoin} />
    </div>
    <figure className="welcome-photo">
      <img src="/branding/fireside-scene.jpg" alt="The existing Copilot Fireside Chat brand photograph, featuring a welcoming fireplace" />
      <figcaption>From the Copilot Fireside Chat community.<br />Brand photograph - not this event's confirmed panel.</figcaption>
      <span className="photo-stamp" aria-hidden="true"><Flame size={20} /> A seat by the fire</span>
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
  const questions = board.questions.filter(q => filter === 'answered' ? q.answered :
    !q.answered && (filter === 'all' || q.hasVoted)).sort((a, b) => b.votes - a.votes || a.sortOrder - b.sortOrder);
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
    <section className="event-hero">
      <div>
        <p className="eyebrow"><Snowflake size={16} aria-hidden="true" /> THE CHRISTMAS EDITION</p>
        <h1>{board.title}<span>Pull up a chair.</span></h1>
        <p>{board.subtitle}</p>
      </div>
      <div className="fireside-note"><Flame size={32} strokeWidth={1.5} aria-hidden="true" />
        <p>A good conversation starts<br />with a little curiosity.</p><span>MAKE YOUR VOICE HEARD</span></div>
    </section>
    {error && <ErrorNotice message={`${error} The board may be out of date.`} />}
    {new URLSearchParams(location.search).has('authError') && <ErrorNotice message="Microsoft sign-in did not complete. Try again with a work or school account, or contact your organisation's administrator." />}
    <div className="audience-grid">
      <section className="questions-section" aria-labelledby="questions-title">
        {current && <article className="current-question">
          <div className="eyebrow"><Radio size={16} aria-hidden="true" /> AROUND THE FIRE RIGHT NOW</div>
          <h2>{current.text}</h2>
          <div className="current-meta"><span>{current.category || 'The conversation'}</span><span>{current.votes} votes from the room</span></div>
        </article>}
        <div className="section-heading">
          <div><p className="eyebrow">YOUR CURIOSITY, OUR CONVERSATION</p><h2 id="questions-title">What should we talk about?</h2></div>
          <span className={`pill ${board.votingOpen ? 'pill-live' : ''}`}>{board.votingOpen ? 'Voting open' : 'Voting paused'}</span>
        </div>
        <p className="section-description">Give the questions you love a little lift. The host chooses what comes next.</p>
        {!session.canVote && <div className="voter-signin">
          <ShieldCheck size={22} aria-hidden="true" /><div><strong>Here to listen? You're all set.</strong>
            <p>To upvote, sign in with a work or school Microsoft Entra account from any organisation.</p>
            {session.demo ? <><span className="fine-print">Local demo only: this simulates a signed-in audience account.</span>
              <button disabled={pending !== null} onClick={() => void demoVoter()}>Sign in as demo voter</button></>
              : <a className="button button-primary" href="/auth/login">Sign in with Microsoft to vote</a>}</div>
        </div>}
        <div className="tabs" aria-label="Filter questions">
          <button aria-pressed={filter === 'all'} onClick={() => setFilter('all')}>Up next <span>{board.questions.filter(q => !q.answered).length}</span></button>
          <button aria-pressed={filter === 'mine'} onClick={() => setFilter('mine')}>My votes</button>
          <button aria-pressed={filter === 'answered'} onClick={() => setFilter('answered')}>Answered <Check size={14} aria-hidden="true" /></button>
        </div>
        {voteError && <ErrorNotice message={voteError} />}
        <div className="question-list">
          {questions.length ? questions.map(question => <QuestionCard key={question.id} question={question}
            current={question.id === board.currentQuestionId} votingOpen={board.votingOpen && session.canVote}
            pending={pending !== null} onVote={() => void vote(question.id)} />)
            : <div className="empty-state"><Flame size={24} aria-hidden="true" /><h3>{filter === 'mine' ? 'Your favourites live here.' : 'Room for more conversation.'}</h3>
              <p>{filter === 'mine' ? 'Upvote a question to save it to this view.' : 'The host will share questions here when they are ready.'}</p></div>}
        </div>
        <p className="fine-print voting-note"><ShieldCheck size={15} aria-hidden="true" /> One vote per question, per organisational Entra identity.
          Changing your name, clearing cookies or using another browser does not reset votes for that identity.</p>
      </section>
      <aside className="panel-section" aria-labelledby="panel-title">
        <p className="eyebrow">GOOD COMPANY</p>
        <h2 id="panel-title">By the fireside</h2>
        <p className="section-description">The people behind the conversation.</p>
        {board.panellists.length ? board.panellists.map((person, index) => <article className="person-card" key={person.id}>
          <div className="person-avatar" aria-hidden="true">{person.name.split(/\s+/).slice(0, 2).map(s => s[0]).join('')}</div>
          <div className="person-copy"><span className="person-number">PANELLIST {String(index + 1).padStart(2, '0')}</span>
            <h3>{person.name}</h3><span className="person-role">{person.role}</span>
            {person.bio && <p>{person.bio}</p>}</div>
        </article>) : <div className="empty-state"><Users size={24} aria-hidden="true" /><p>The host hasn't announced the panel yet.</p></div>}
        <div className="how-it-works"><Snowflake size={22} aria-hidden="true" /><h3>A little fireside etiquette</h3>
          <p>Stay curious. Be kind. Vote for a question you'd love the panel to explore.</p>
          <a href="#questions-title">Find your next question <ArrowDown size={16} aria-hidden="true" /></a></div>
      </aside>
    </div>
  </>;
}

export function QuestionCard({ question, current, votingOpen, pending, onVote }: {
  question: Question; current: boolean; votingOpen: boolean; pending: boolean; onVote: () => void;
}) {
  return <article className={`question-card ${current ? 'is-current' : ''}`}>
    <button className={`vote-button ${question.hasVoted ? 'has-voted' : ''}`}
      onClick={onVote} disabled={pending || question.hasVoted || !votingOpen || question.answered}
      aria-label={`${question.hasVoted ? 'Voted for' : 'Upvote'}: ${question.text}`}
      aria-pressed={question.hasVoted}>
      {question.hasVoted ? <Check size={19} aria-hidden="true" /> : <ChevronUp size={22} aria-hidden="true" />}
      <strong>{question.votes}</strong><span>{question.hasVoted ? 'VOTED' : 'VOTES'}</span>
    </button>
    <div><div className="question-tags"><span>{question.category || 'Around the fire'}</span>
      {current && <span className="current-tag">Discussing now</span>}
      {question.answered && <span>Answered</span>}</div>
      <h3>{question.text}</h3></div>
  </article>;
}

export function Surface({ title, children }: { title: string; children: ReactNode }) {
  return <section className="admin-surface"><h2>{title}</h2>{children}</section>;
}

export function Privacy() {
  return <article className="privacy-page">
    <p className="eyebrow">A LITTLE TRANSPARENCY</p><h1>Privacy &amp; participation</h1>
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
