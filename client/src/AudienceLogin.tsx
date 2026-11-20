import { useState, type FormEvent } from 'react';
import { errorMessage, write } from './api';
import type { Session } from './types';
import { ErrorNotice } from './App';

export function AudienceLogin({ session, onSignedIn }: { session: Session; onSignedIn: () => Promise<void> }) {
  const [register, setRegister] = useState(false);
  const [username, setUsername] = useState('');
  const [password, setPassword] = useState('');
  const [pending, setPending] = useState(false);
  const [error, setError] = useState('');
  async function submit(event: FormEvent) {
    event.preventDefault();
    setPending(true); setError('');
    try {
      await write(`/api/account/${register ? 'register' : 'login'}`, 'POST', session.csrfToken, { username, password });
      setPassword('');
      await onSignedIn();
    } catch (error) { setPassword(''); setError(errorMessage(error)); }
    finally { setPending(false); }
  }
  return <form className="audience-login" onSubmit={submit} aria-labelledby="account-title">
    <h3 id="account-title">{register ? 'Create your audience account' : 'Sign in to vote'}</h3>
    <p>Viewing is public. Use an audience username and password to react; no Microsoft account is needed.</p>
    <label htmlFor="audience-username">Username</label>
    <input id="audience-username" name="username" autoComplete="username" required minLength={3} maxLength={32}
      pattern="[A-Za-z0-9._\-]{3,32}" value={username} disabled={pending}
      onChange={event => setUsername(event.target.value)} aria-describedby="username-help" />
    <span id="username-help" className="fine-print">3-32 letters, numbers, dots, underscores or hyphens. Not case-sensitive.</span>
    <label htmlFor="audience-password">Password</label>
    <input id="audience-password" name="password" type="password" autoComplete={register ? 'new-password' : 'current-password'}
      required minLength={register ? 15 : 1} maxLength={128} value={password} disabled={pending}
      onChange={event => setPassword(event.target.value)} />
    {register && <p className="fine-print">Use a password or passphrase of 15-128 characters. There is no self-service
      password reset. A new account starts with its own voting history; existing event totals are kept.</p>}
    {error && <ErrorNotice message={error} />}
    <div className="account-actions">
      <button className="button-primary" disabled={pending}>{pending ? 'Please wait...' : register ? 'Create account & sign in' : 'Sign in'}</button>
      <button type="button" className="text-button" disabled={pending} onClick={() => {
        setRegister(!register); setError(''); setPassword('');
      }}>{register ? 'Already registered? Sign in' : 'New here? Register'}</button>
    </div>
  </form>;
}
