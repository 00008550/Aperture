import { getAccessToken } from './auth';

export class ApiError extends Error {
  constructor(
    readonly status: number,
    message: string,
  ) {
    super(message);
  }
}

export async function api<T>(path: string, init?: RequestInit): Promise<T> {
  const token = getAccessToken();

  const res = await fetch(path, {
    ...init,
    headers: {
      'content-type': 'application/json',
      // No token, no Authorization header — the request goes out anonymous and the API
      // answers 401. Sending an empty bearer would be indistinguishable from a malformed one.
      ...(token ? { authorization: `Bearer ${token}` } : {}),
      ...(init?.headers ?? {}),
    },
  });

  if (!res.ok) throw new ApiError(res.status, `${init?.method ?? 'GET'} ${path} -> ${res.status}`);
  return (await res.json()) as T;
}

/** One data scope, in the shape `GET /api/me` returns it (`ScopeResponse` in MeEndpoints.cs). */
export interface SessionScope {
  kind: string;
  targetId: string | null;
}

/** The `MeResponse` contract. Kept field-for-field — the API is the source of truth. */
export interface Session {
  tenantId: string;
  userId: string;
  email: string;
  displayName: string;
  permissions: string[];
  scopes: SessionScope[];
}
