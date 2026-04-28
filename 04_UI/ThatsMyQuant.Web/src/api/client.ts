import { authStorage } from "@/lib/authStorage";

const API_BASE_URL = (import.meta.env.VITE_API_BASE_URL as string | undefined) ?? "http://localhost:5000";
const API_KEY = (import.meta.env.VITE_API_KEY as string | undefined) ?? "";

export class ApiError extends Error {
  readonly status: number;
  readonly body?: string;

  constructor(message: string, status: number, body?: string) {
    super(message);
    this.status = status;
    this.body = body;
  }
}

// Subscribers for global 401 handling (AuthProvider hooks into this).
const unauthorizedListeners: Array<() => void> = [];

export function onUnauthorized(fn: () => void): () => void {
  unauthorizedListeners.push(fn);
  return () => {
    const idx = unauthorizedListeners.indexOf(fn);
    if (idx >= 0) unauthorizedListeners.splice(idx, 1);
  };
}

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const headers = new Headers(init?.headers);
  headers.set("Accept", "application/json");
  if (init?.body && !headers.has("Content-Type")) {
    headers.set("Content-Type", "application/json");
  }
  if (API_KEY) {
    headers.set("X-Api-Key", API_KEY);
  }
  const token = authStorage.getToken();
  if (token) {
    headers.set("Authorization", `Bearer ${token}`);
  }

  const res = await fetch(`${API_BASE_URL}${path}`, {
    ...init,
    headers,
  });

  if (res.status === 401 && !path.startsWith("/api/auth/")) {
    // Token expired or invalid — clear and notify subscribers (AuthProvider will redirect).
    authStorage.clear();
    unauthorizedListeners.forEach((fn) => {
      try { fn(); } catch { /* ignore */ }
    });
  }

  if (!res.ok) {
    const body = await res.text().catch(() => undefined);
    throw new ApiError(`API ${res.status} ${res.statusText} for ${path}`, res.status, body);
  }

  if (res.status === 204) {
    return undefined as T;
  }

  return (await res.json()) as T;
}

export const api = {
  get: <T>(path: string) => request<T>(path, { method: "GET" }),
  post: <T>(path: string, body?: unknown) =>
    request<T>(path, { method: "POST", body: body ? JSON.stringify(body) : undefined }),
  patch: <T>(path: string, body?: unknown) =>
    request<T>(path, { method: "PATCH", body: body ? JSON.stringify(body) : undefined }),
  delete: <T>(path: string) => request<T>(path, { method: "DELETE" }),
};

export const apiBaseUrl = API_BASE_URL;
