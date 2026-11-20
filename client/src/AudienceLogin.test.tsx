import { fireEvent, render, screen, waitFor, cleanup } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { AudienceLogin } from './AudienceLogin';
import { write } from './api';
import type { Session } from './types';

vi.mock('./api', async () => ({ ...await vi.importActual('./api'), write: vi.fn() }));
afterEach(() => { cleanup(); vi.clearAllMocks(); });
const session: Session = { participantName: 'Viewer', isAdmin: false, canVote: false,
  signedIn: false, adminName: null, demo: false, csrfToken: 'csrf' };

describe('Audience accounts', () => {
  it('registers with CSRF and password-manager fields, then refreshes the session', async () => {
    vi.mocked(write).mockResolvedValue(undefined);
    const refresh = vi.fn().mockResolvedValue(undefined);
    render(<AudienceLogin session={session} onSignedIn={refresh} />);
    fireEvent.click(screen.getByRole('button', { name: 'New here? Register' }));
    fireEvent.change(screen.getByLabelText('Username'), { target: { value: 'winter-guest' } });
    const password = screen.getByLabelText('Password');
    expect(password).toHaveAttribute('type', 'password');
    expect(password).toHaveAttribute('autocomplete', 'new-password');
    fireEvent.change(password, { target: { value: 'A memorable winter passphrase!' } });
    fireEvent.submit(screen.getByRole('form'));
    await waitFor(() => expect(refresh).toHaveBeenCalledOnce());
    expect(write).toHaveBeenCalledWith('/api/account/register', 'POST', 'csrf',
      { username: 'winter-guest', password: 'A memorable winter passphrase!' });
    expect(password).toHaveValue('');
  });

  it('shows a rejected login without treating it as success or retaining the password', async () => {
    vi.mocked(write).mockRejectedValue(new Error('Username or password is incorrect.'));
    const refresh = vi.fn();
    render(<AudienceLogin session={session} onSignedIn={refresh} />);
    fireEvent.change(screen.getByLabelText('Username'), { target: { value: 'winter-guest' } });
    fireEvent.change(screen.getByLabelText('Password'), { target: { value: 'incorrect' } });
    fireEvent.submit(screen.getByRole('form'));
    expect(await screen.findByRole('alert')).toHaveTextContent('Username or password is incorrect.');
    expect(refresh).not.toHaveBeenCalled();
    expect(screen.getByLabelText('Password')).toHaveValue('');
  });
});
