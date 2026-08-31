import { useState, type FormEvent } from 'react';
import { setAccessToken } from './auth';

export interface SignInProps {
  /** Why the previous attempt ended, when it ended badly. */
  message?: string;
}

/**
 * Sign-in against the contract 001-P3 actually shipped: the API authenticates a bearer token
 * (`Authentication:Issuer` / `Audience` / `SigningKey`) and resolves the caller from the
 * access schema on every request. There is no token-issuing endpoint yet, so the console
 * takes the token it is given rather than inventing a second, parallel auth contract —
 * a `POST /api/auth/token` is a server portion, not this one.
 */
export function SignIn({ message }: SignInProps) {
  const [token, setToken] = useState('');
  const trimmed = token.trim();

  function onSubmit(event: FormEvent) {
    event.preventDefault();
    if (trimmed.length === 0) return;
    setAccessToken(trimmed);
  }

  return (
    <main className="signin">
      <form className="card" onSubmit={onSubmit}>
        <div className="brand">
          Aperture
          <small>order &amp; deal desk</small>
        </div>

        <h1>Sign in</h1>
        <p className="sub">
          Paste the access token issued for your tenant. The console keeps it for this tab only.
        </p>

        {message && (
          <p className="warn" role="alert">
            {message}
          </p>
        )}

        <label htmlFor="token">Access token</label>
        <textarea
          id="token"
          className="mono"
          rows={4}
          value={token}
          spellCheck={false}
          autoComplete="off"
          onChange={(e) => setToken(e.target.value)}
        />

        <button type="submit" disabled={trimmed.length === 0}>
          Sign in
        </button>
      </form>
    </main>
  );
}
