import { lazy, Suspense } from "react";
import { BrowserRouter, Navigate, Route, Routes } from "react-router-dom";
import { QueryClientProvider } from "@tanstack/react-query";
import { queryClient } from "@/lib/queryClient";
import { AppShell } from "@/components/layout/AppShell";
import { ErrorBoundary } from "@/components/ui/ErrorBoundary";
import { Toaster } from "@/components/ui/Toaster";
import { PageLoadingSkeleton } from "@/components/ui/Skeleton";
import { AuthProvider } from "@/hooks/useAuth";
import { ProtectedRoute } from "@/components/auth/ProtectedRoute";
import { LoginPage } from "@/pages/LoginPage";

const DashboardPage = lazy(() =>
  import("@/pages/DashboardPage").then((m) => ({ default: m.DashboardPage }))
);
const PositionsPage = lazy(() =>
  import("@/pages/PositionsPage").then((m) => ({ default: m.PositionsPage }))
);
const TradeJournalPage = lazy(() =>
  import("@/pages/TradeJournalPage").then((m) => ({ default: m.TradeJournalPage }))
);
const AlphaPortfolioPage = lazy(() =>
  import("@/pages/AlphaPortfolioPage").then((m) => ({ default: m.AlphaPortfolioPage }))
);
const FundingMonitorPage = lazy(() =>
  import("@/pages/FundingMonitorPage").then((m) => ({ default: m.FundingMonitorPage }))
);
const PairsDiscoveryPage = lazy(() =>
  import("@/pages/PairsDiscoveryPage").then((m) => ({ default: m.PairsDiscoveryPage }))
);
const QuantLabPage = lazy(() =>
  import("@/pages/QuantLabPage").then((m) => ({ default: m.QuantLabPage }))
);
const ProModePage = lazy(() =>
  import("@/pages/ProModePage").then((m) => ({ default: m.ProModePage }))
);
const SettingsPage = lazy(() =>
  import("@/pages/SettingsPage").then((m) => ({ default: m.SettingsPage }))
);
const AuditLogPage = lazy(() =>
  import("@/pages/AuditLogPage").then((m) => ({ default: m.AuditLogPage }))
);
const SystemHealthPage = lazy(() =>
  import("@/pages/SystemHealthPage").then((m) => ({ default: m.SystemHealthPage }))
);
const MarketsPage = lazy(() =>
  import("@/pages/MarketsPage").then((m) => ({ default: m.MarketsPage }))
);
const CockpitPage = lazy(() =>
  import("@/pages/CockpitPage").then((m) => ({ default: m.CockpitPage }))
);
const UsersPage = lazy(() =>
  import("@/pages/UsersPage").then((m) => ({ default: m.UsersPage }))
);
const SignalDiagnosticsPage = lazy(() =>
  import("@/pages/SignalDiagnosticsPage").then((m) => ({ default: m.SignalDiagnosticsPage }))
);
const BacktestLabPage = lazy(() =>
  import("@/pages/BacktestLabPage").then((m) => ({ default: m.BacktestLabPage }))
);

function App() {
  return (
    <ErrorBoundary>
      <QueryClientProvider client={queryClient}>
        <BrowserRouter>
          <AuthProvider>
            <Routes>
              <Route path="/login" element={<LoginPage />} />
              <Route
                element={
                  <ProtectedRoute>
                    <AppShell />
                  </ProtectedRoute>
                }
              >
                <Route
                  index
                  element={
                    <Suspense fallback={<PageLoadingSkeleton />}>
                      <DashboardPage />
                    </Suspense>
                  }
                />
                <Route
                  path="markets"
                  element={
                    <Suspense fallback={<PageLoadingSkeleton />}>
                      <MarketsPage />
                    </Suspense>
                  }
                />
                <Route
                  path="cockpit"
                  element={
                    <Suspense fallback={<PageLoadingSkeleton />}>
                      <CockpitPage />
                    </Suspense>
                  }
                />
                <Route
                  path="positions"
                  element={
                    <Suspense fallback={<PageLoadingSkeleton />}>
                      <PositionsPage />
                    </Suspense>
                  }
                />
                <Route
                  path="trades"
                  element={
                    <Suspense fallback={<PageLoadingSkeleton />}>
                      <TradeJournalPage />
                    </Suspense>
                  }
                />
                <Route
                  path="alpha"
                  element={
                    <Suspense fallback={<PageLoadingSkeleton />}>
                      <AlphaPortfolioPage />
                    </Suspense>
                  }
                />
                <Route
                  path="funding"
                  element={
                    <Suspense fallback={<PageLoadingSkeleton />}>
                      <FundingMonitorPage />
                    </Suspense>
                  }
                />
                <Route
                  path="pairs"
                  element={
                    <Suspense fallback={<PageLoadingSkeleton />}>
                      <PairsDiscoveryPage />
                    </Suspense>
                  }
                />
                <Route
                  path="quant-lab"
                  element={
                    <Suspense fallback={<PageLoadingSkeleton />}>
                      <QuantLabPage />
                    </Suspense>
                  }
                />
                <Route
                  path="pro-mode"
                  element={
                    <Suspense fallback={<PageLoadingSkeleton />}>
                      <ProModePage />
                    </Suspense>
                  }
                />
                <Route
                  path="diagnostics"
                  element={
                    <Suspense fallback={<PageLoadingSkeleton />}>
                      <SignalDiagnosticsPage />
                    </Suspense>
                  }
                />
                <Route
                  path="backtest-lab"
                  element={
                    <Suspense fallback={<PageLoadingSkeleton />}>
                      <BacktestLabPage />
                    </Suspense>
                  }
                />
                <Route
                  path="audit"
                  element={
                    <Suspense fallback={<PageLoadingSkeleton />}>
                      <AuditLogPage />
                    </Suspense>
                  }
                />
                <Route
                  path="health"
                  element={
                    <Suspense fallback={<PageLoadingSkeleton />}>
                      <SystemHealthPage />
                    </Suspense>
                  }
                />
                <Route
                  path="users"
                  element={
                    <Suspense fallback={<PageLoadingSkeleton />}>
                      <UsersPage />
                    </Suspense>
                  }
                />
                <Route
                  path="settings"
                  element={
                    <Suspense fallback={<PageLoadingSkeleton />}>
                      <SettingsPage />
                    </Suspense>
                  }
                />
                <Route path="*" element={<Navigate to="/" replace />} />
              </Route>
            </Routes>
          </AuthProvider>
        </BrowserRouter>
        <Toaster />
      </QueryClientProvider>
    </ErrorBoundary>
  );
}

export default App;
