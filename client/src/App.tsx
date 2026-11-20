import { useCallback, useEffect, useState, type FormEvent, type ReactNode } from 'react';
import { ArrowRight, ArrowUpRight, Check, Flame, Radio, ShieldCheck, Snowflake } from 'lucide-react';
import { request, write, errorMessage } from './api';
import type { Panellist, Question, ReactionKind, Session } from './types';
import { DiscussionTimer, type TimerState } from './DiscussionTimer';
import { useBoard } from './useBoard';
import { Admin } from './Admin';
import { ReactionButtons, ReactionOverlay } from './Reactions';

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
  if (session && !sessionError && !adminPage && window.location.pathname !== '/privacy' && session.participantName)
    return <Audience session={session} refreshSession={refreshSession} />;
  return <PageFrame session={session} adminPage={adminPage} fillViewport={!adminPage && window.location.pathname !== '/privacy'}>
    {sessionError ? <div className="center-state"><ErrorNotice message={sessionError} />
      <button onClick={() => void refreshSession()}>Try again</button></div>
      : !session ? <Loading /> : adminPage
        ? <Admin session={session} refreshSession={refreshSession} />
        : window.location.pathname === '/privacy' ? <Privacy />
          : <Welcome session={session} onJoin={refreshSession} />}
  </PageFrame>;
}

function PageFrame({ session, adminPage = false, fillViewport = false, audienceBar, children }: {
  session: Session | null; adminPage?: boolean; fillViewport?: boolean; audienceBar?: ReactNode; children: ReactNode;
}) {
  return <div className={`site-shell ${adminPage ? '' : 'fireside-surface'}`}>
    {!adminPage && <svg className="fireside-mesh" viewBox="0 0 1400 900" preserveAspectRatio="xMidYMid slice" aria-hidden="true">
      <path d="M0 80 260 10 180 310 0 460M260 10 530 180 180 310 380 590 0 460M530 180 780 20 900 350 530 180 380 590 800 720 900 350 1250 120 1400 260M780 20 1250 120 1400 0M900 350 1220 620 1400 420M380 590 240 900M800 720 1220 620 1400 900M800 720 780 900" />
    </svg>}
    <a className="skip-link" href="#main">Skip to content</a>
    <div className={fillViewport ? 'viewport-frame' : undefined}>
    <header className="site-header">
      <a className="brand" href="/" aria-label="Copilot Fireside Chat home">
        <Flame className="brand-symbol" size={28} aria-hidden="true" />
        <span>Copilot<span className="brand-sub">Fireside Chat</span></span>
      </a>
      {session?.demo && <div className="demo-strip">
        <strong><Snowflake size={15} aria-hidden="true" /> LOCAL DEMO</strong>
        <span>Sample panellists &amp; questions. Not a scheduled event.</span>
      </div>}
      <nav aria-label="Main navigation">
        <a href="/admin" aria-current={adminPage ? 'page' : undefined}><ShieldCheck size={16} aria-hidden="true" /> Host desk</a>
      </nav>
      {audienceBar}
    </header>
    <main id="main">{children}</main>
    </div>
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
    <p className="fine-print">No account needed to view. To react, sign in with a work or school Microsoft Entra account.
      An essential cookie remembers your display name for 30 days; your name is not public.</p>
  </form>;
}

function Welcome({ session, onJoin }: { session: Session; onJoin: () => Promise<void> }) {
  const { board, error, refresh, reactions } = useBoard(false, true);
  if (!board) return error ? <div className="center-state"><ErrorNotice message={error} />
    <button onClick={() => void refresh()}>Try again</button></div> : <Loading />;
  return <section className="welcome">
    <ReactionOverlay reactions={reactions} />
    <LandingArtwork source={board.landingImageUrl} description={board.landingImageDescription} />
    <div className="welcome-copy">
      <h1>Come in.<br />Join the fireside.</h1>
      <p className="intro">The Christmas panel, one conversation at a time.
        Set your name to follow what is happening now.</p>
      <NameForm session={session} onSaved={onJoin} />
    </div>
  </section>;
}

function LandingArtwork({ source, description }: { source: string; description: string }) {
  const [failed, setFailed] = useState(false);
  useEffect(() => setFailed(false), [source]);
  return <div className="landing-artwork">
    {failed ? <ErrorNotice message="The event image could not load. Refresh the page or contact the host." />
      : <a href={source} target="_blank" rel="noreferrer" aria-label="Open full-size event image">
        <img src={source} alt={description} width={1920} height={1080} onError={() => setFailed(true)} />
      </a>}
  </div>;
}

function Audience({ session, refreshSession }: { session: Session; refreshSession: () => Promise<void> }) {
  const { board, error, connection, refresh, reactions } = useBoard(false, true);
  const [filter, setFilter] = useState<'all' | 'answered'>('all');
  const [view, setView] = useState<'live' | 'questions' | 'panel'>('live');
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
  async function vote(id: string, kind: ReactionKind) {
    setPending(id);
    setVoteError('');
    try {
      await write(`/api/questions/${id}/vote`, 'POST', session.csrfToken, { requestId: crypto.randomUUID(), kind });
      await refresh();
    } catch (error) { setVoteError(errorMessage(error)); }
    finally { setPending(null); }
  }
  if (!board) return <PageFrame session={session} fillViewport>
    {error ? <div className="center-state"><ErrorNotice message={error} />
      <button onClick={() => void refresh()}>Try again</button></div> : <Loading />}
  </PageFrame>;
  const current = board.questions.find(q => q.id === board.currentQuestionId);
  const questions = board.questions.filter(q => filter === 'answered' ? q.answered : !q.answered)
    .sort((a, b) => b.score - a.score || a.sortOrder - b.sortOrder);
  const views = [
    { id: 'live', label: 'Live' }, { id: 'questions', label: 'Questions & votes' }, { id: 'panel', label: 'Panellists' },
  ] as const;
  return <PageFrame session={session} fillViewport audienceBar={
    <div className="audience-bar">
      <span className={`connection ${connection === 'Live' ? 'connected' : ''}`} role="status">
        <span className="status-dot" /> {connection === 'Live' ? 'Connected to the fireside' : connection}
      </span>
      <div className="audience-account"><button className="text-button" onClick={() => setEditName(!editName)}>Hi, {session.participantName} <span>Change name</span></button>
        {session.signedIn && <form action="/auth/logout" method="post">
          <input type="hidden" name="__RequestVerificationToken" value={session.csrfToken} />
          <button className="text-button" type="submit">Sign out of Microsoft</button>
        </form>}</div>
    </div>}>
    <ReactionOverlay reactions={reactions} />
    {editName && <div className="name-edit"><NameForm session={session} initial={session.participantName ?? ''}
      onSaved={async () => { await refreshSession(); setEditName(false); }} /></div>}
    <div className="audience-tabs" role="tablist" aria-label="Fireside views" onKeyDown={event => {
      const index = views.findIndex(item => item.id === view);
      const next = event.key === 'ArrowRight' ? (index + 1) % views.length :
        event.key === 'ArrowLeft' ? (index + views.length - 1) % views.length :
          event.key === 'Home' ? 0 : event.key === 'End' ? views.length - 1 : -1;
      if (next < 0) return;
      event.preventDefault();
      setView(views[next].id);
      event.currentTarget.querySelectorAll<HTMLButtonElement>('[role="tab"]')[next].focus();
    }}>
      {views.map(item => <button key={item.id} id={`tab-${item.id}`} role="tab" aria-selected={view === item.id}
        aria-controls={`panel-${item.id}`} tabIndex={view === item.id ? 0 : -1}
        onClick={() => setView(item.id)}>{item.label}</button>)}
    </div>
    <header className="event-heading"><h1>{board.title}</h1><p>{board.subtitle}</p></header>
    {error && <ErrorNotice message={`${error} The board may be out of date.`} />}
    {new URLSearchParams(location.search).has('authError') && <ErrorNotice message="Microsoft sign-in did not complete. Try again with a work or school account, or contact your organisation's administrator." />}
    <div className="tab-panel activity-panel" role="tabpanel" id="panel-live" aria-labelledby="tab-live" hidden={view !== 'live'}>
    <CurrentActivity question={current} votingOpen={board.votingOpen} canVote={session.canVote}
      speaker={board.panellists.find(person => person.id === current?.speakerId)}
      groupDiscussion={board.groupDiscussion} timer={board}
      imageUrl={board.landingImageUrl} imageDescription={board.landingImageDescription}
      pending={pending !== null} onReact={kind => { if (current) void vote(current.id, kind); }}>
      {!session.canVote && <div className="activity-signin">
        {session.demo ? <button className="button-primary" disabled={pending !== null} onClick={() => void demoVoter()}>
          Sign in as demo voter <ArrowRight size={17} aria-hidden="true" /></button>
          : <a className="button button-primary" href="/auth/login">Sign in with Microsoft to vote <ArrowRight size={17} aria-hidden="true" /></a>}
        <p>{session.demo ? 'Development-only sign-in simulation.' : 'Work or school accounts from any organisation. Viewing is public.'}</p>
      </div>}
    </CurrentActivity>
    </div>
    {voteError && <ErrorNotice message={voteError} />}
    <section className="tab-panel questions-section" role="tabpanel" id="panel-questions" aria-labelledby="tab-questions" hidden={view !== 'questions'}>
        <div className="queue-heading"><h2>Questions &amp; points</h2><span className="voting-state">{board.groupDiscussion ? 'Group discussion' : board.votingOpen ? 'Voting open' : 'Voting paused'}</span></div>
        {!session.canVote && <p className="section-description">Sign in on the Live tab with your organisational Entra account to see your points and react to the current discussion.</p>}
        <div className="tabs" aria-label="Filter questions">
          <button aria-pressed={filter === 'all'} onClick={() => setFilter('all')}>Open questions <span>{board.questions.filter(q => !q.answered).length}</span></button>
          <button aria-pressed={filter === 'answered'} onClick={() => setFilter('answered')}>Answered <Check size={14} aria-hidden="true" /></button>
        </div>
        <div className="question-list">
          {questions.length ? questions.map(question => <QuestionCard key={question.id} question={question} />)
            : <div className="empty-state"><p>{filter === 'answered' ? 'Answered questions will appear here.' : 'The host will share more questions here.'}</p></div>}
        </div>
        <p className="fine-print voting-note"><ShieldCheck size={15} aria-hidden="true" /> React to the current discussion on the Live tab.
          Likes add 1, dislikes subtract 1, and epic points add 3. Your points show your net contribution to each question.</p>
    </section>
    <section className="tab-panel panel-section" role="tabpanel" id="panel-panel" aria-labelledby="tab-panel" hidden={view !== 'panel'}>
          <h2>Meet the panel</h2>
          {board.panellists.length ? board.panellists.map(person => <article className="person-card" key={person.id}>
            {person.photoUrl && <PanellistPhoto person={person} />}
            <h3>{person.name}</h3><span className="person-role">{person.role}</span>
            {person.bio && <p>{person.bio}</p>}
          </article>) : <p className="section-description">The host hasn't announced the panel yet.</p>}
    </section>
  </PageFrame>;
}

export function CurrentActivity({ question, imageUrl, imageDescription, votingOpen, canVote, pending, onReact, children,
  speaker, groupDiscussion = false, timer }: {
  imageUrl: string; imageDescription: string;
  question?: Question; votingOpen: boolean; canVote: boolean; pending: boolean;
  onReact: (kind: ReactionKind) => void; children?: ReactNode;
  speaker?: Panellist; groupDiscussion?: boolean; timer?: TimerState;
}) {
  if (!question) return <section className="activity-stage landing-stage" aria-label="Event landing page">
    <LandingArtwork source={imageUrl} description={imageDescription} />
    <div className="landing-controls">
      <div className="activity-meta"><span><Radio size={17} aria-hidden="true" />Waiting for the host</span></div>
      {canVote ? <p className="ready-note"><Check size={16} aria-hidden="true" /> Signed in and ready to vote.</p> : children}
    </div>
  </section>;
  return <section className="activity-stage current-question" aria-labelledby="activity-title">
    <div className="activity-copy" key={question.id}>
      <h2 id="activity-title" aria-live="polite" aria-atomic="true">{question.text}</h2>
      <p className="activity-description">{question.category || 'The current conversation.'}</p>
      <div className="activity-meta"><span><Radio size={17} aria-hidden="true" />{groupDiscussion ? 'Group discussion' : 'Discussing now'}</span>
        <output aria-live="polite">{question.score} {Math.abs(question.score) === 1 ? 'point' : 'points'}</output></div>
      {canVote && !groupDiscussion && <ReactionButtons question={question} enabled={votingOpen} pending={pending} onReact={onReact} />}
      {canVote && !groupDiscussion && !votingOpen && <p className="section-description">Voting is paused.</p>}
      {!groupDiscussion && children}
    </div>
    {groupDiscussion ? <aside className="discussion-aside">
      <h3>Open floor</h3><p>All panellists can join in. Voting is off during this discussion.</p>
      {timer && <DiscussionTimer state={timer} />}
    </aside> : speaker ? <aside className="speaker-card" aria-label="Current speaker">
      <PanellistPhoto person={speaker} />
      <div><h3>{speaker.name}</h3><p>{speaker.role}</p>{timer && <DiscussionTimer state={timer} />}</div>
    </aside> : <figure className="activity-photo">
      <img src="/branding/fireside-scene.jpg" alt="" width={2280} height={1522} />
      <figcaption>Original show photograph,<br />not this event's confirmed panel.</figcaption>
    </figure>}
  </section>;
}

export function PanellistPhoto({ person }: { person: Panellist }) {
  const [failed, setFailed] = useState(false);
  useEffect(() => setFailed(false), [person.photoUrl]);
  return <div className="speaker-image">
    {person.photoUrl && !failed ? <img src={person.photoUrl} alt={`Portrait of ${person.name}`}
      width={320} height={320} onError={() => setFailed(true)} />
      : <div className="speaker-placeholder"><Flame size={32} aria-hidden="true" />
        <span>{failed ? 'Photo could not load' : 'No photo added'}</span></div>}
  </div>;
}

export function QuestionCard({ question }: { question: Question }) {
  return <article className="question-card">
    <div><div className="question-tags"><span>{question.category || 'Around the fire'}</span>
      {question.answered && <span>Answered</span>}</div>
      <h3>{question.text}</h3></div>
    <dl className="question-points">
      <div><dt>Total points</dt><dd className="total-points" aria-live="polite">{question.score}</dd></div>
      <div><dt>Your points</dt><dd className={`personal-points ${question.myScore === null ? 'score-unavailable' : ''}`}
        aria-live="polite">{question.myScore ?? 'Sign in to see'}</dd></div>
    </dl>
  </article>;
}

export function Surface({ title, children }: { title: string; children: ReactNode }) {
  return <section className="admin-surface"><h2>{title}</h2>{children}</section>;
}

export function Privacy() {
  return <article className="privacy-page">
    <h1>Privacy &amp; participation</h1>
    <p>You don't need a Microsoft account to view the audience page. Set a display name to enter.
      Sending reactions requires a work or school Microsoft Entra account from any organisation.
      Your display name is stored in a protected, HTTP-only essential cookie, not in the vote database or a public attendee list.</p>
    <h2>What we store</h2><p>The display-name cookie lasts 30 days. When you sign in, a separate protected authentication
      cookie holds identity claims for up to two hours. Each accepted reaction stores its question, score change,
      request identifier and a stable hash derived from your Entra tenant/user identifiers. It does not store raw
      identity identifiers, your display name or email. This is pseudonymous personal data, not anonymous data.</p>
    <h2>Repeatable reactions</h2><p>You can react repeatedly while voting is open: likes add 1 point, dislikes subtract
      1 and epic points add 3. Short per-account rate limits protect the live experience. A festive animation is
      broadcast without your name or identity; old animations are not replayed after reconnecting.
      Some organisations require administrator consent before sign-in.</p>
    <h2>Administrators</h2><p>The host desk requires both the configured host tenant and an explicitly assigned
      Fireside.Admin application role. A role assigned in another tenant does not grant host access.
      Microsoft handles sign-in for voters and administrators.</p>
    <h2>Retention and support</h2><p>Event hosts control the questions and vote records and must publish their contact
      and event retention policy before going live. Removing a question also removes its reactions. Uploaded landing
      artwork and its description are public; hosts should remove private metadata before uploading.
      Hosting services may retain operational logs according to the host's Azure settings; this application does not
      log display names or vote bodies.</p><a href="/">Back to the fireside <ArrowRight size={16} aria-hidden="true" /></a>
  </article>;
}
