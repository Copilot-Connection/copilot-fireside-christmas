import { useState, type FormEvent } from 'react';
import { Check, Eye, EyeOff, Flame, LogOut, Plus, Radio, ShieldCheck, Trash2 } from 'lucide-react';
import type { Board, Panellist, Question, Session } from './types';
import { ApiError, errorMessage, write } from './api';
import { useBoard } from './useBoard';
import { ErrorNotice, Loading, Surface } from './App';

type PersonDraft = Omit<Panellist, 'id'>;
type QuestionDraft = Omit<Question, 'id' | 'votes' | 'hasVoted'>;
const emptyPerson: PersonDraft = { name: '', role: '', bio: '', visible: false, sortOrder: 0 };
const emptyQuestion: QuestionDraft = { text: '', category: '', visible: false, answered: false, sortOrder: 0 };

export function Admin({ session, refreshSession }: { session: Session; refreshSession: () => Promise<void> }) {
  const { board, error: loadError, connection, refresh } = useBoard(true, session.isAdmin);
  const [error, setError] = useState('');
  const [notice, setNotice] = useState('');
  const [pending, setPending] = useState(false);
  const [editingPerson, setEditingPerson] = useState<{ id?: string; draft: PersonDraft; revision: number } | null>(null);
  const [editingQuestion, setEditingQuestion] = useState<{ id?: string; draft: QuestionDraft; revision: number } | null>(null);
  const [settings, setSettings] = useState<Board | null>(null);

  async function mutation(path: string, method: string, body?: unknown, revision = board?.revision) {
    if (pending) return false;
    setPending(true); setError(''); setNotice('');
    try {
      await write(path, method, session.csrfToken, body, revision);
      await refresh();
      setNotice('Saved. The audience board is up to date.');
      return true;
    } catch (error) {
      setError(errorMessage(error));
      if (error instanceof ApiError && error.status === 409) await refresh();
      if (error instanceof ApiError && error.status === 401) await refreshSession();
      return false;
    } finally { setPending(false); }
  }
  async function enterDemo() {
    setPending(true); setError('');
    try { await write('/auth/demo-admin', 'POST', session.csrfToken); await refreshSession(); }
    catch (error) { setError(errorMessage(error)); }
    finally { setPending(false); }
  }
  if (!session.isAdmin) return <div className="admin-gate">
    <ShieldCheck size={40} strokeWidth={1.5} aria-hidden="true" /><p className="eyebrow">BEHIND THE CONVERSATION</p>
    <h1>The host desk</h1><p>A little organisation makes room for a great conversation.
      Sign in with an authorised Microsoft Entra administrator account to manage the fireside.</p>
    {session.signedIn ? <ErrorNotice message="You're signed in, but this account isn't assigned the Fireside.Admin role. Ask your Entra administrator for access." />
      : new URLSearchParams(location.search).has('authError') && <ErrorNotice message="Microsoft sign-in did not complete. Please try again or contact your administrator." />}
    {error && <ErrorNotice message={error} />}
    {session.demo ? <><div className="demo-explainer">Development-only demo. This local host shortcut does not exist in production.</div>
      <button className="button-primary" disabled={pending} onClick={() => void enterDemo()}>Enter demo host desk</button></>
      : <a className="button button-primary" href="/auth/login">Sign in with Microsoft <ShieldCheck size={17} aria-hidden="true" /></a>}
    {session.signedIn && <SignOut session={session} />}
    <a className="back-link" href="/">Back to the fireside</a>
  </div>;
  if (!board) return loadError ? <div className="center-state"><ErrorNotice message={loadError} />
    <button onClick={() => void refresh()}>Try again</button></div> : <Loading />;

  async function savePerson(event: FormEvent) {
    event.preventDefault();
    if (!editingPerson) return;
    const success = await mutation(`/api/admin/panellists${editingPerson.id ? `/${editingPerson.id}` : ''}`,
      editingPerson.id ? 'PUT' : 'POST', editingPerson.draft, editingPerson.revision);
    if (success) setEditingPerson(null);
  }
  async function saveQuestion(event: FormEvent) {
    event.preventDefault();
    if (!editingQuestion) return;
    const success = await mutation(`/api/admin/questions${editingQuestion.id ? `/${editingQuestion.id}` : ''}`,
      editingQuestion.id ? 'PUT' : 'POST', editingQuestion.draft, editingQuestion.revision);
    if (success) setEditingQuestion(null);
  }
  function updateBoard(changes: Partial<Board>) {
    void mutation('/api/admin/board', 'PUT', { ...board, ...changes });
  }
  return <div className="admin-page">
    <div className="admin-intro"><div><p className="eyebrow"><Flame size={16} aria-hidden="true" /> THE HOST DESK</p>
      <h1>Keep the conversation glowing.</h1><p>Curate the panel, share questions, and guide what the audience sees.</p></div>
      <div className="admin-account"><span>{session.adminName}</span><SignOut session={session} /></div></div>
    <div className="control-strip"><span className="connection" role="status"><span className={`status-dot ${connection === 'Live' ? 'on' : ''}`} /> {connection}</span>
      <span>Audience voting is <strong>{board.votingOpen ? 'open' : 'paused'}</strong></span>
      <button disabled={pending} onClick={() => updateBoard({ votingOpen: !board.votingOpen })}>{board.votingOpen ? 'Pause voting' : 'Open voting'}</button>
      <a className="button" href="/" target="_blank" rel="noreferrer"><Eye size={16} aria-hidden="true" /> View audience board</a></div>
    {(error || loadError) && <ErrorNotice message={error || loadError} />}
    {notice && <p className="success-notice" role="status"><Check size={17} aria-hidden="true" /> {notice}</p>}
    <Surface title="The event">
      {settings ? <form onSubmit={async event => { event.preventDefault();
        if (await mutation('/api/admin/board', 'PUT', settings, settings.revision)) setSettings(null);
      }}>
        <label>Event title<input required maxLength={120} value={settings.title} onChange={event => setSettings({ ...settings, title: event.target.value })} /></label>
        <label>Subtitle<input maxLength={300} value={settings.subtitle} onChange={event => setSettings({ ...settings, subtitle: event.target.value })} /></label>
        <FormActions pending={pending} cancel={() => setSettings(null)} />
      </form> : <div className="row-between"><div><strong>{board.title}</strong><p>{board.subtitle}</p></div>
        <button onClick={() => setSettings({ ...board })}>Edit event details</button></div>}
    </Surface>
    <div className="admin-columns">
      <Surface title="Questions">
        <p className="section-description">New questions start hidden. Publish when ready, then choose one to discuss.</p>
        <button className="add-button" disabled={pending || editingQuestion !== null}
          onClick={() => setEditingQuestion({ draft: { ...emptyQuestion, sortOrder: board.questions.length }, revision: board.revision })}>
          <Plus size={16} aria-hidden="true" /> Add question</button>
        {editingQuestion && <form className="editor-form" onSubmit={saveQuestion}>
          <h3>{editingQuestion.id ? 'Edit question' : 'New question'}</h3>
          <label>Question<textarea required maxLength={240} value={editingQuestion.draft.text}
            onChange={event => setEditingQuestion({ ...editingQuestion, draft: { ...editingQuestion.draft, text: event.target.value } })} /></label>
          <label>Category<input maxLength={60} value={editingQuestion.draft.category}
            onChange={event => setEditingQuestion({ ...editingQuestion, draft: { ...editingQuestion.draft, category: event.target.value } })} /></label>
          <label>Display order<input type="number" min={0} max={10000} required value={editingQuestion.draft.sortOrder}
            onChange={event => setEditingQuestion({ ...editingQuestion, draft: { ...editingQuestion.draft, sortOrder: Number(event.target.value) } })} /></label>
          <label className="checkbox"><input type="checkbox" checked={editingQuestion.draft.visible}
            onChange={event => setEditingQuestion({ ...editingQuestion, draft: { ...editingQuestion.draft, visible: event.target.checked } })} /> Visible to audience</label>
          <label className="checkbox"><input type="checkbox" checked={editingQuestion.draft.answered}
            onChange={event => setEditingQuestion({ ...editingQuestion, draft: { ...editingQuestion.draft, answered: event.target.checked } })} /> Answered</label>
          <FormActions pending={pending} cancel={() => setEditingQuestion(null)} />
          {editingQuestion.revision !== board.revision && <p className="fine-print">The board changed while you were editing. Cancel and reopen to edit the latest version.</p>}
        </form>}
        <div className="admin-list">{board.questions.map(question => <article key={question.id}>
          <div className="question-tags"><span>{question.visible ? 'Published' : 'Hidden'}</span><span>{question.votes} votes</span>
            {question.answered && <span>Answered</span>}
            {board.currentQuestionId === question.id && <span className="current-tag">Discussing now</span>}</div>
          <h3>{question.text}</h3><p className="fine-print">{question.category}</p>
          <div className="action-row">
            <button disabled={pending} onClick={() => void mutation(`/api/admin/questions/${question.id}`, 'PUT', { ...question, visible: !question.visible })}>
              {question.visible ? <EyeOff size={15} aria-hidden="true" /> : <Eye size={15} aria-hidden="true" />}{question.visible ? 'Hide' : 'Publish'}</button>
            <button disabled={pending || editingQuestion !== null} onClick={() => setEditingQuestion({ id: question.id, draft: { ...question }, revision: board.revision })}>Edit</button>
            <button disabled={pending || !question.visible || question.answered}
              onClick={() => updateBoard({ currentQuestionId: board.currentQuestionId === question.id ? null : question.id })}>
              <Radio size={15} aria-hidden="true" /> {board.currentQuestionId === question.id ? 'Stop discussing' : 'Discuss now'}</button>
            <button disabled={pending} onClick={() => void mutation(`/api/admin/questions/${question.id}`, 'PUT', { ...question, answered: !question.answered })}>
              <Check size={15} aria-hidden="true" />{question.answered ? 'Reopen' : 'Mark answered'}</button>
            <button className="danger-button" disabled={pending} aria-label={`Delete question: ${question.text}`}
              onClick={() => { if (confirm('Permanently delete this question and all its votes?')) void mutation(`/api/admin/questions/${question.id}`, 'DELETE'); }}>
              <Trash2 size={15} aria-hidden="true" /></button>
          </div>
        </article>)}</div>
        {!board.questions.length && <p className="empty-state">No questions yet. Add the first spark.</p>}
      </Surface>
      <Surface title="Panellists">
        <p className="section-description">Add confirmed speakers only. Hidden profiles are never sent to the audience.</p>
        <button className="add-button" disabled={pending || editingPerson !== null}
          onClick={() => setEditingPerson({ draft: { ...emptyPerson, sortOrder: board.panellists.length }, revision: board.revision })}>
          <Plus size={16} aria-hidden="true" /> Add panellist</button>
        {editingPerson && <form className="editor-form" onSubmit={savePerson}>
          <h3>{editingPerson.id ? 'Edit panellist' : 'New panellist'}</h3>
          <label>Name<input required maxLength={80} value={editingPerson.draft.name}
            onChange={event => setEditingPerson({ ...editingPerson, draft: { ...editingPerson.draft, name: event.target.value } })} /></label>
          <label>Role / introduction<input maxLength={120} value={editingPerson.draft.role}
            onChange={event => setEditingPerson({ ...editingPerson, draft: { ...editingPerson.draft, role: event.target.value } })} /></label>
          <label>Short bio<textarea maxLength={500} value={editingPerson.draft.bio}
            onChange={event => setEditingPerson({ ...editingPerson, draft: { ...editingPerson.draft, bio: event.target.value } })} /></label>
          <label>Display order<input type="number" min={0} max={10000} required value={editingPerson.draft.sortOrder}
            onChange={event => setEditingPerson({ ...editingPerson, draft: { ...editingPerson.draft, sortOrder: Number(event.target.value) } })} /></label>
          <label className="checkbox"><input type="checkbox" checked={editingPerson.draft.visible}
            onChange={event => setEditingPerson({ ...editingPerson, draft: { ...editingPerson.draft, visible: event.target.checked } })} /> Visible to audience</label>
          <FormActions pending={pending} cancel={() => setEditingPerson(null)} />
          {editingPerson.revision !== board.revision && <p className="fine-print">The board changed while you were editing. Cancel and reopen to edit the latest version.</p>}
        </form>}
        <div className="admin-list">{board.panellists.map(person => <article key={person.id}>
          <div className="question-tags"><span>{person.visible ? 'Published' : 'Hidden'}</span><span>Order {person.sortOrder}</span></div>
          <h3>{person.name}</h3><p>{person.role}</p><p className="fine-print">{person.bio}</p>
          <div className="action-row">
            <button disabled={pending} onClick={() => void mutation(`/api/admin/panellists/${person.id}`, 'PUT', { ...person, visible: !person.visible })}>
              {person.visible ? <EyeOff size={15} aria-hidden="true" /> : <Eye size={15} aria-hidden="true" />}{person.visible ? 'Hide' : 'Publish'}</button>
            <button disabled={pending || editingPerson !== null} onClick={() => setEditingPerson({ id: person.id, draft: { ...person }, revision: board.revision })}>Edit</button>
            <button className="danger-button" disabled={pending} aria-label={`Delete panellist: ${person.name}`}
              onClick={() => { if (confirm(`Permanently delete ${person.name}?`)) void mutation(`/api/admin/panellists/${person.id}`, 'DELETE'); }}>
              <Trash2 size={15} aria-hidden="true" /></button>
          </div>
        </article>)}</div>
        {!board.panellists.length && <p className="empty-state">The chairs are ready. Add your panel.</p>}
      </Surface>
    </div>
  </div>;
}

function FormActions({ pending, cancel }: { pending: boolean; cancel: () => void }) {
  return <div className="action-row"><button className="button-primary" disabled={pending}>{pending ? 'Saving...' : 'Save changes'}</button>
    <button type="button" disabled={pending} onClick={cancel}>Cancel</button></div>;
}

function SignOut({ session }: { session: Session }) {
  return <form action="/auth/logout" method="post">
    <input type="hidden" name="__RequestVerificationToken" value={session.csrfToken} />
    <button className="text-button" type="submit"><LogOut size={15} aria-hidden="true" /> Sign out</button>
  </form>;
}
