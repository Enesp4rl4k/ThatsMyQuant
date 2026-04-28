import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from "react";
import { useQuery } from "@tanstack/react-query";
import { authApi } from "@/api/auth";
import { onUnauthorized } from "@/api/client";
import { authStorage, type StoredUser } from "@/lib/authStorage";

export type Role = "Admin" | "Operator" | "ReadOnly";

interface AuthContextValue {
  authEnabled: boolean | null; // null while config is loading
  user: StoredUser | null;
  role: Role | null;
  isLoading: boolean;
  login: (email: string, password: string, remember: boolean) => Promise<void>;
  logout: () => void;
  /** True if Auth is disabled OR user has at least one of the allowed roles. */
  hasRole: (...roles: Role[]) => boolean;
}

const AuthContext = createContext<AuthContextValue | null>(null);

export function AuthProvider({ children }: { children: ReactNode }) {
  const [user, setUser] = useState<StoredUser | null>(() => authStorage.getUser());
  const [tick, setTick] = useState(0);

  const configQ = useQuery({
    queryKey: ["auth", "config"],
    queryFn: authApi.config,
    staleTime: Infinity,
    retry: 1,
  });

  // Global 401 handler — clears state so the router redirects to /login.
  useEffect(() => {
    return onUnauthorized(() => {
      setUser(null);
      setTick((t) => t + 1);
    });
  }, []);

  const login = useCallback(async (email: string, password: string, remember: boolean) => {
    const res = await authApi.login({ email, password });
    authStorage.setSession(res.token, res.user, remember);
    setUser(res.user);
  }, []);

  const logout = useCallback(() => {
    authStorage.clear();
    setUser(null);
    setTick((t) => t + 1);
  }, []);

  const role = (user?.role as Role | undefined) ?? null;

  const hasRole = useCallback(
    (...roles: Role[]) => {
      // Auth disabled = unrestricted
      if (configQ.data && !configQ.data.enabled) return true;
      if (!role) return false;
      if (role === "Admin") return true; // Admin satisfies any role check
      return roles.includes(role);
    },
    [configQ.data, role]
  );

  const value = useMemo<AuthContextValue>(
    () => ({
      // If /api/auth/config errors (older backend without auth endpoints),
      // treat auth as disabled so the rest of the app keeps working.
      authEnabled: configQ.data
        ? configQ.data.enabled
        : configQ.isError
          ? false
          : null,
      user,
      role,
      isLoading: configQ.isLoading,
      login,
      logout,
      hasRole,
    }),
    // tick forces re-render when listeners notify; included intentionally
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [configQ.data, configQ.isLoading, user, role, login, logout, hasRole, tick]
  );

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

export function useAuth(): AuthContextValue {
  const ctx = useContext(AuthContext);
  if (!ctx) throw new Error("useAuth must be used inside <AuthProvider>");
  return ctx;
}
