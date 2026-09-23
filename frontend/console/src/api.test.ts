import { afterEach, describe, expect, it, vi } from 'vitest';
import { ApiError, apiAuthed, serverMessageOf, updateAccount } from './api';
import { getAccessToken, setAccessToken } from './auth';

function answer(status: number, body: string | null, contentType = 'application/json') {
  vi.stubGlobal(
    'fetch',
    vi.fn(async () => new Response(body, { status, headers: { 'content-type': contentType } })),
  );
}

async function failure(promise: Promise<unknown>): Promise<ApiError> {
  try {
    await promise;
  } catch (error) {
    if (error instanceof ApiError) return error;
    throw error;
  }
  throw new Error('expected the call to fail');
}

const update = { ownerUserId: 'o', name: 'n', creditLimit: 0, paymentTermsDays: 0, regionId: null, teamId: null, expectedVersion: 7 };

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('ApiError keeps the response body', () => {
  it('Given a 409 with a JSON body, when the write fails, then the error keeps the status and the parsed body', async () => {
    setAccessToken('t');
    answer(409, JSON.stringify({ error: 'The account was modified by someone else; reload and retry.' }));

    const error = await failure(updateAccount('a1', update));

    expect(error.status).toBe(409);
    expect(error.body).toEqual({ error: 'The account was modified by someone else; reload and retry.' });
    expect(error.serverMessage).toBe('The account was modified by someone else; reload and retry.');
    // A 409 is not an auth failure: the token survives.
    expect(getAccessToken()).toBe('t');
  });

  it('Given a non-JSON error body, when the call fails, then the raw text is kept', async () => {
    answer(502, 'Bad gateway', 'text/plain');
    const error = await failure(apiAuthed('/api/accounts'));
    expect(error.body).toBe('Bad gateway');
    expect(error.serverMessage).toBe('Bad gateway');
  });

  it('Given an empty error body, when the call fails, then body and serverMessage are null', async () => {
    answer(404, null);
    const error = await failure(apiAuthed('/api/accounts/x'));
    expect(error.status).toBe(404);
    expect(error.body).toBeNull();
    expect(error.serverMessage).toBeNull();
  });

  it('Given a 401 with a body, when an authed call fails, then the single token-drop path still runs', async () => {
    setAccessToken('t');
    answer(401, JSON.stringify({ title: 'Unauthorized' }));
    const error = await failure(apiAuthed('/api/accounts'));
    expect(error.status).toBe(401);
    expect(error.body).toEqual({ title: 'Unauthorized' });
    expect(getAccessToken()).toBeNull();
  });
});

describe('serverMessageOf', () => {
  it('prefers error, then detail, then validation errors, then title', () => {
    expect(serverMessageOf({ error: 'e', detail: 'd' })).toBe('e');
    expect(serverMessageOf({ title: 't', detail: 'd' })).toBe('d');
    expect(serverMessageOf({ title: 't', errors: { Name: ['too long'], TaxId: ['required'] } })).toBe(
      'too long required',
    );
    expect(serverMessageOf({ title: 't' })).toBe('t');
  });

  it('yields null for bodies without a message rather than guessing', () => {
    expect(serverMessageOf(null)).toBeNull();
    expect(serverMessageOf({})).toBeNull();
    expect(serverMessageOf(42)).toBeNull();
    expect(serverMessageOf('   ')).toBeNull();
  });
});
