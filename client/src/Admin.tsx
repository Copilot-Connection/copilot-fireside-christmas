import { useState, type FormEvent } from 'react';
import { ArrowDown, ArrowUp, Check, ChevronLeft, ChevronRight, Eye, EyeOff, Flame, LogOut, Plus, Radio, ShieldCheck, Trash2 } from 'lucide-react';
import type { Board, Panellist, Question, Session } from './types';
import { ApiError, errorMessage, write } from './api';
import { useBoard } from './useBoard';
import { ErrorNotice, Loading, PanellistPhoto, Surface } from './App';
import { DiscussionTimer } from './DiscussionTimer';

type PersonDraft = Omit<Panellist, 'id' | 'photoUrl'>;
type QuestionDraft = Omit<Question, 'id' | 'score' | 'myScore'>;
const emptyPerson: PersonDraft = { name: '', role: '', bio: '', visible: false, sortOrder: 0 };
const emptyQuestion: QuestionDraft = { text: '', category: '', visible: false, answered: false, sortOrder: 0, speakerId: null, speakerIds: [] };

export function Admin({ session, refreshSession }: { session: Session; refreshSession: () => Promise<void> }) {
  const { board, error: loadError, connection, refresh } = useBoard(true, session.isAdmin);
  const [error, setError] = useState('');
  const [notice, setNotice] = useState('');
  const [pending, setPending] = useState(false);
  const [editingPerson, setEditingPerson] = useState<{ id?: string; draft: PersonDraft; revision: number } | null>(null);
  const [editingQuestion, setEditingQuestion] = useState<{ id?: string; draft: QuestionDraft; revision: number } | null>(null);
  const [settings, setSettings] = useState<Board | null>(null);
  const [artwork, setArtwork] = useState<{ file: File | null; description: string; revision: number } | null>(null);
  const [photo, setPhoto] = useState<{ person: Panellist; file: File | null; revision: number } | null>(null);
  const [timings, setTimings] = useState<Pick<Board, 'speakerSeconds' | 'groupSeconds' | 'revision'> | null>(null);

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
    {session.signedIn ? <ErrorNotice message="You're signed in, but host access requires the configured administrative tenant and the Fireside.Admin role. Ask the event's Entra administrator for access." />
      : new URLSearchParams(location.search).has('authError') && <ErrorNotice message="Microsoft sign-in did not complete. Please try again or contact your administrator." />}
    {error && <ErrorNotice message={error} />}
    {session.demo ? <><div className="demo-explainer">Development-only demo. This local host shortcut does not exist in production.</div>
      <button className="button-primary" disabled={pending} onClick={() => void enterDemo()}>Enter demo host desk</button></>
      : <a className="button button-primary" href="/auth/login?destination=admin">Sign in with Microsoft <ShieldCheck size={17} aria-hidden="true" /></a>}
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
  function moveSpeaker(index: number, direction: number) {
    if (!editingQuestion) return;
    const speakerIds = [...editingQuestion.draft.speakerIds];
    const target = index + direction;
    if (target < 0 || target >= speakerIds.length) return;
    [speakerIds[index], speakerIds[target]] = [speakerIds[target], speakerIds[index]];
    setEditingQuestion({ ...editingQuestion, draft: { ...editingQuestion.draft, speakerIds } });
  }
  function selectSpeaker(question: Question, speakerId: string) {
    void mutation(`/api/admin/questions/${question.id}/speaker`, 'PUT', { speakerId });
  }
  function cycleSpeaker(question: Question, direction: number) {
    const ids = question.speakerIds.filter(id => board!.panellists.some(person => person.id === id && person.visible));
    if (!ids.length) return;
    const index = question.speakerId ? ids.indexOf(question.speakerId) : -1;
    selectSpeaker(question, ids[index < 0 ? (direction > 0 ? 0 : ids.length - 1) : (index + direction + ids.length) % ids.length]);
  }
  return <div className="admin-page">
    <div className="admin-intro"><div><p className="eyebrow"><Flame size={16} aria-hidden="true" /> THE HOST DESK</p>
      <h1>Keep the conversation glowing.</h1><p>Curate the panel, share questions, and guide what the audience sees.</p></div>
      <div className="admin-account"><span>{session.adminName}</span><SignOut session={session} /></div></div>
    <div className="control-strip"><span className="connection" role="status"><span className={`status-dot ${connection === 'Live' ? 'on' : ''}`} /> {connection}</span>
      <span>Audience voting is <strong>{board.groupDiscussion ? 'off for group discussion' : board.votingOpen ? 'open' : 'paused'}</strong></span>
      <button disabled={pending || board.groupDiscussion} onClick={() => updateBoard({ votingOpen: !board.votingOpen })}>{board.votingOpen ? 'Pause voting' : 'Open voting'}</button>
      <button disabled={pending || board.currentQuestionId === null} onClick={() => updateBoard({ currentQuestionId: null })}>Show landing page</button>
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
    <Surface title="Discussion timers">
      {timings ? <form className="editor-form timing-form" onSubmit={async event => {
        event.preventDefault();
        if (await mutation('/api/admin/timing', 'PUT', timings, timings.revision)) setTimings(null);
      }}>
        <label>Speaker timer (seconds)<input type="number" min={5} max={3600} required value={timings.speakerSeconds}
          onChange={event => setTimings({ ...timings, speakerSeconds: Number(event.target.value) })} /></label>
        <label>Group discussion timer (seconds)<input type="number" min={5} max={3600} required value={timings.groupSeconds}
          onChange={event => setTimings({ ...timings, groupSeconds: Number(event.target.value) })} /></label>
        <p className="fine-print">Shared by every question. Changes apply when the next timer starts, not to a timer already running.</p>
        <FormActions pending={pending} cancel={() => setTimings(null)} />
      </form> : <div className="row-between"><p>Speaker: {board.speakerSeconds} seconds. Group discussion: {board.groupSeconds} seconds.</p>
        <button disabled={pending} onClick={() => setTimings({ ...board })}>Edit timer lengths</button></div>}
      {board.currentQuestionId && <div className="host-timer">
        <DiscussionTimer state={board} />
        <button disabled={pending || board.timerStartedAt === null}
          onClick={() => void mutation('/api/admin/discussion/timer', 'DELETE')}>Stop timer</button>
        <p className="fine-print">Reaching zero does not advance the question or change voting mode. Stopping a group timer keeps voting off.</p>
      </div>}
    </Surface>
    <Surface title="Landing image">
      <p className="section-description">Shown on arrival and whenever no question is being discussed. Uploads are public and stored durably in the event database.</p>
      <img className="admin-artwork" src={board.landingImageUrl} alt={board.landingImageDescription} />
      {artwork ? <form className="editor-form" onSubmit={async event => {
        event.preventDefault();
        if (!artwork.file) return;
        if (await mutation(`/api/admin/landing-image?description=${encodeURIComponent(artwork.description)}`,
          'PUT', artwork.file, artwork.revision)) setArtwork(null);
      }}>
        <label>Image file<input type="file" accept="image/png,image/jpeg" required disabled={pending}
          onChange={event => setArtwork({ ...artwork, file: event.target.files?.[0] ?? null })} /></label>
        <label>Image description<input required maxLength={300} value={artwork.description}
          onChange={event => setArtwork({ ...artwork, description: event.target.value })} /></label>
        <p className="fine-print">PNG or JPEG, up to 2 MB, 8192 pixels per side and 24 megapixels. Describe important text in the image. Remove private metadata before uploading.</p>
        <FormActions pending={pending} cancel={() => setArtwork(null)} />
        {artwork.revision !== board.revision && <p className="fine-print">The board changed. Cancel and reopen before uploading.</p>}
      </form> : <div className="action-row">
        <button onClick={() => setArtwork({ file: null, description: '', revision: board.revision })}>Change landing image</button>
        <button disabled={pending || !board.hasCustomLandingImage} onClick={() => void mutation('/api/admin/landing-image', 'DELETE')}>Use default artwork</button>
      </div>}
    </Surface>
    <div className="admin-columns">
      <Surface title="Questions">
        <p className="section-description">New questions start hidden. Publish when ready, then choose one to discuss.</p>
        <button className="add-button" disabled={pending || editingQuestion !== null}
          onClick={() => setEditingQuestion({ draft: { ...emptyQuestion, speakerIds: board.panellists.map(person => person.id),
            sortOrder: board.questions.length }, revision: board.revision })}>
          <Plus size={16} aria-hidden="true" /> Add question</button>
        {editingQuestion && <form className="editor-form" onSubmit={saveQuestion}>
          <h3>{editingQuestion.id ? 'Edit question' : 'New question'}</h3>
          <label>Question<textarea required maxLength={240} value={editingQuestion.draft.text}
            onChange={event => setEditingQuestion({ ...editingQuestion, draft: { ...editingQuestion.draft, text: event.target.value } })} /></label>
          <label>Category<input maxLength={60} value={editingQuestion.draft.category}
            onChange={event => setEditingQuestion({ ...editingQuestion, draft: { ...editingQuestion.draft, category: event.target.value } })} /></label>
          <label>Speaker<select value={editingQuestion.draft.speakerId ?? ''}
            onChange={event => setEditingQuestion({ ...editingQuestion, draft: { ...editingQuestion.draft, speakerId: event.target.value || null } })}>
            <option value="">No speaker assigned</option>
            {board.panellists.map(person => <option key={person.id} value={person.id}>{person.name}{person.visible ? '' : ' (hidden)'}</option>)}
          </select></label>
          <fieldset className="speaker-order">
            <legend>Speaker order for this question</legend>
            <p className="fine-print">Previous and next follow this order, skipping hidden panellists. New panellists are appended.</p>
            {editingQuestion.draft.speakerIds.length ? <ol>{editingQuestion.draft.speakerIds.map((id, index) => {
              const person = board.panellists.find(person => person.id === id);
              return <li key={id}><span>{person?.name ?? 'Removed panellist'}{person && !person.visible ? ' (hidden)' : ''}</span>
                <div className="action-row">
                  <button type="button" disabled={pending || index === 0} aria-label={`Move ${person?.name ?? 'speaker'} earlier`}
                    onClick={() => moveSpeaker(index, -1)}><ArrowUp size={16} aria-hidden="true" /></button>
                  <button type="button" disabled={pending || index === editingQuestion.draft.speakerIds.length - 1}
                    aria-label={`Move ${person?.name ?? 'speaker'} later`} onClick={() => moveSpeaker(index, 1)}>
                    <ArrowDown size={16} aria-hidden="true" /></button>
                </div></li>;
            })}</ol> : <p className="fine-print">Add panellists to set their speaking order.</p>}
          </fieldset>
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
          <div className="question-tags"><span>{question.visible ? 'Published' : 'Hidden'}</span><span>{question.score} points</span>
            {question.answered && <span>Answered</span>}
            {board.currentQuestionId === question.id && <span className="current-tag">{board.groupDiscussion ? 'Group discussion' : 'Discussing now'}</span>}</div>
          <h3>{question.text}</h3><p className="fine-print">{question.category}</p>
          <p className="fine-print">Speaker: {board.panellists.find(person => person.id === question.speakerId)?.name ?? 'Not assigned'}</p>
          <div className="speaker-selection">
            <label htmlFor={`speaker-${question.id}`}>Select speaker</label>
            <div className="speaker-switcher">
              <button disabled={pending || !question.visible || question.answered ||
                !board.panellists.some(person => person.visible)} aria-label={`Previous speaker: ${question.text}`}
                onClick={() => cycleSpeaker(question, -1)}><ChevronLeft size={20} aria-hidden="true" /></button>
              <select id={`speaker-${question.id}`} value={question.speakerId ?? ''}
                disabled={pending || !question.visible || question.answered}
                onChange={event => selectSpeaker(question, event.target.value)}>
                <option value="" disabled>No speaker selected</option>
                {question.speakerIds.map(id => board.panellists.find(person => person.id === id)).filter(person => person !== undefined)
                  .map(person => <option key={person.id} value={person.id} disabled={!person.visible}>
                    {person.name}{person.visible ? '' : ' (hidden)'}</option>)}
              </select>
              <button disabled={pending || !question.visible || question.answered ||
                !board.panellists.some(person => person.visible)} aria-label={`Next speaker: ${question.text}`}
                onClick={() => cycleSpeaker(question, 1)}><ChevronRight size={20} aria-hidden="true" /></button>
            </div>
            <p className="fine-print">Selecting puts this question live. The timer waits for Start speaker timer.</p>
          </div>
          <div className="action-row">
            <button disabled={pending} onClick={() => void mutation(`/api/admin/questions/${question.id}`, 'PUT', { ...question, visible: !question.visible })}>
              {question.visible ? <EyeOff size={15} aria-hidden="true" /> : <Eye size={15} aria-hidden="true" />}{question.visible ? 'Hide' : 'Publish'}</button>
            <button disabled={pending || editingQuestion !== null} onClick={() => setEditingQuestion({ id: question.id, draft: { ...question }, revision: board.revision })}>Edit</button>
            <button disabled={pending || !question.visible || question.answered}
              onClick={() => updateBoard({ currentQuestionId: board.currentQuestionId === question.id ? null : question.id })}>
              <Radio size={15} aria-hidden="true" /> {board.currentQuestionId === question.id ? 'Stop discussing' : 'Discuss now'}</button>
            <button disabled={pending || !question.visible || question.answered ||
              !board.panellists.some(person => person.id === question.speakerId && person.visible)}
              onClick={() => void mutation('/api/admin/discussion', 'POST', { questionId: question.id, groupDiscussion: false })}>
              Start speaker timer</button>
            <button disabled={pending || !question.visible || question.answered}
              onClick={() => void mutation('/api/admin/discussion', 'POST', { questionId: question.id, groupDiscussion: true })}>
              Start group discussion</button>
            <button disabled={pending} onClick={() => void mutation(`/api/admin/questions/${question.id}`, 'PUT', { ...question, answered: !question.answered })}>
              <Check size={15} aria-hidden="true" />{question.answered ? 'Reopen' : 'Mark answered'}</button>
            <button className="danger-button" disabled={pending} aria-label={`Delete question: ${question.text}`}
              onClick={() => { if (confirm('Permanently delete this question and all its reactions?')) void mutation(`/api/admin/questions/${question.id}`, 'DELETE'); }}>
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
          {person.photoUrl && <PanellistPhoto person={person} />}
          <div className="action-row">
            <button disabled={pending} onClick={() => void mutation(`/api/admin/panellists/${person.id}`, 'PUT', { ...person, visible: !person.visible })}>
              {person.visible ? <EyeOff size={15} aria-hidden="true" /> : <Eye size={15} aria-hidden="true" />}{person.visible ? 'Hide' : 'Publish'}</button>
            <button disabled={pending || editingPerson !== null} onClick={() => setEditingPerson({ id: person.id, draft: { ...person }, revision: board.revision })}>Edit</button>
            <button disabled={pending || photo !== null} onClick={() => setPhoto({ person, file: null, revision: board.revision })}>
              {person.photoUrl ? 'Change photo' : 'Upload photo'}</button>
            <button disabled={pending || !person.photoUrl} onClick={() => void mutation(`/api/admin/panellists/${person.id}/photo`, 'DELETE')}>Remove photo</button>
            <button className="danger-button" disabled={pending} aria-label={`Delete panellist: ${person.name}`}
              onClick={() => { if (confirm(`Permanently delete ${person.name}?`)) void mutation(`/api/admin/panellists/${person.id}`, 'DELETE'); }}>
              <Trash2 size={15} aria-hidden="true" /></button>
          </div>
          {photo?.person.id === person.id && <form className="editor-form photo-form" onSubmit={async event => {
            event.preventDefault();
            if (photo.file && await mutation(`/api/admin/panellists/${person.id}/photo`, 'PUT', photo.file, photo.revision)) setPhoto(null);
          }}>
            <label>Panellist image<input type="file" accept="image/png,image/jpeg" required disabled={pending}
              onChange={event => setPhoto({ ...photo, file: event.target.files?.[0] ?? null })} /></label>
            <p className="fine-print">PNG/JPEG, up to 2 MB, 8192 pixels per side and 24 megapixels. Published photos are public; remove private metadata first.</p>
            <FormActions pending={pending} cancel={() => setPhoto(null)} />
          </form>}
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
