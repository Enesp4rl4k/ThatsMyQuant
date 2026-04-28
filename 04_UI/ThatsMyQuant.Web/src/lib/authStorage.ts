// Token + user persistence. sessionStorage so closing the tab logs you out
// (XSS-safe relative to localStorage) — explicit "Remember me" path uses localStorage.

const TOKEN_KEY = "tmq.token";
const USER_KEY = "tmq.user";
const REMEMBER_KEY = "tmq.remember";

export interface StoredUser {
  id: string;
  username: string;
  email: string;
  role: string;
  mustChangePassword: boolean;
}

export const authStorage = {
  getToken(): string | null {
    return localStorage.getItem(TOKEN_KEY) ?? sessionStorage.getItem(TOKEN_KEY);
  },
  getUser(): StoredUser | null {
    const raw = localStorage.getItem(USER_KEY) ?? sessionStorage.getItem(USER_KEY);
    if (!raw) return null;
    try {
      return JSON.parse(raw) as StoredUser;
    } catch {
      return null;
    }
  },
  setSession(token: string, user: StoredUser, remember: boolean) {
    // Clear both stores so we don't leave stale data.
    localStorage.removeItem(TOKEN_KEY);
    localStorage.removeItem(USER_KEY);
    sessionStorage.removeItem(TOKEN_KEY);
    sessionStorage.removeItem(USER_KEY);

    const store = remember ? localStorage : sessionStorage;
    store.setItem(TOKEN_KEY, token);
    store.setItem(USER_KEY, JSON.stringify(user));

    if (remember) {
      localStorage.setItem(REMEMBER_KEY, "1");
    } else {
      localStorage.removeItem(REMEMBER_KEY);
    }
  },
  clear() {
    localStorage.removeItem(TOKEN_KEY);
    localStorage.removeItem(USER_KEY);
    localStorage.removeItem(REMEMBER_KEY);
    sessionStorage.removeItem(TOKEN_KEY);
    sessionStorage.removeItem(USER_KEY);
  },
};
