import { type ReactNode } from "react";
import { Navigate, useLocation } from "react-router-dom";
import { useAuth } from "@/hooks/useAuth";
import { PageLoadingSkeleton } from "@/components/ui/Skeleton";

export function ProtectedRoute({ children }: { children: ReactNode }) {
  const auth = useAuth();
  const location = useLocation();

  // Still resolving /api/auth/config — show skeleton briefly.
  if (auth.isLoading || auth.authEnabled === null) {
    return <PageLoadingSkeleton />;
  }

  // Auth disabled = open access, preserves existing X-Api-Key flow.
  if (!auth.authEnabled) return <>{children}</>;

  // Auth enabled but no session — bounce to login, remember the original URL.
  if (!auth.user) {
    return (
      <Navigate
        to="/login"
        replace
        state={{ from: location.pathname + location.search }}
      />
    );
  }

  return <>{children}</>;
}
