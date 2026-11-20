export class ApiError extends Error {
  constructor(public status: number, message: string) { super(message); }
}

export async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const response = await fetch(path, { credentials: 'same-origin', ...init });
  if (!response.ok) {
    const body: unknown = await response.json().catch(() => null);
    const detail = body && typeof body === 'object' && 'detail' in body && typeof body.detail === 'string'
      ? body.detail : response.status === 401 ? 'Your session ended. Refresh this page to sign in or set your name again.'
        : response.status === 403 ? path.startsWith('/api/admin')
          ? 'This account does not have host access. Ask your Entra administrator for the Fireside.Admin role.'
          : 'Sign in with an audience account to vote. Your audience session may have ended.'
        : response.status === 429 ? 'Too many requests. Please wait a minute and try again.'
        : `Unable to complete that request (${response.status}). Please retry.`;
    throw new ApiError(response.status, detail);
  }
  return response.status === 204 ? undefined as T : response.json() as Promise<T>;
}

export function write<T>(path: string, method: string, token: string, body?: unknown, revision?: number) {
  return request<T>(path, {
    method,
    headers: {
      'Content-Type': body instanceof Blob ? 'application/octet-stream' : 'application/json',
      'X-CSRF-TOKEN': token,
      ...(revision === undefined ? {} : { 'If-Match': `"${revision}"` }),
    },
    ...(body === undefined ? {} : { body: body instanceof Blob ? body : JSON.stringify(body) }),
  });
}

export const errorMessage = (error: unknown) =>
  error instanceof Error ? error.message : 'Something went wrong. Please try again.';
