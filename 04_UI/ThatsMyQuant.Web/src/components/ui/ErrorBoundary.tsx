import { Component, type ErrorInfo, type ReactNode } from "react";

interface Props {
  children: ReactNode;
  fallback?: ReactNode;
  onError?: (error: Error, info: ErrorInfo) => void;
}

interface State {
  error: Error | null;
}

export class ErrorBoundary extends Component<Props, State> {
  state: State = { error: null };

  static getDerivedStateFromError(error: Error): State {
    return { error };
  }

  componentDidCatch(error: Error, info: ErrorInfo) {
    console.error("[ErrorBoundary] Caught:", error.message, info.componentStack);
    this.props.onError?.(error, info);
  }

  reset = () => this.setState({ error: null });

  render() {
    if (this.state.error) {
      if (this.props.fallback) return this.props.fallback;
      return <ErrorFallback error={this.state.error} onReset={this.reset} />;
    }
    return this.props.children;
  }
}

function ErrorFallback({ error, onReset }: { error: Error; onReset: () => void }) {
  return (
    <div className="flex flex-col items-center justify-center min-h-[40vh] gap-5 p-8 text-center">
      <div className="w-14 h-14 rounded-2xl bg-rose-500/10 border border-rose-500/20 flex items-center justify-center">
        <svg width="24" height="24" viewBox="0 0 24 24" fill="none" className="text-rose-400">
          <path
            d="M12 9v4M12 17h.01M10.29 3.86L1.82 18a2 2 0 0 0 1.71 3h16.94a2 2 0 0 0 1.71-3L13.71 3.86a2 2 0 0 0-3.42 0z"
            stroke="currentColor"
            strokeWidth="1.5"
            strokeLinecap="round"
            strokeLinejoin="round"
          />
        </svg>
      </div>
      <div className="space-y-1">
        <p className="text-sm font-bold text-foreground">Something went wrong</p>
        <p className="text-xs text-muted-foreground/60 max-w-xs leading-relaxed font-mono">
          {error.message || "Unexpected render error"}
        </p>
      </div>
      <div className="flex gap-2">
        <button
          onClick={onReset}
          className="px-4 py-2 text-xs font-bold rounded-md bg-white/5 hover:bg-white/10 border border-white/10 transition-colors"
        >
          Try again
        </button>
        <button
          onClick={() => window.location.reload()}
          className="px-4 py-2 text-xs font-bold rounded-md bg-accent/10 hover:bg-accent/20 border border-accent/20 text-accent transition-colors"
        >
          Reload page
        </button>
      </div>
    </div>
  );
}

/** Wraps a page-level section. Less alarming than the full-page fallback. */
export function SectionErrorBoundary({ children }: { children: ReactNode }) {
  return (
    <ErrorBoundary
      fallback={
        <div className="rounded-xl border border-rose-500/20 bg-rose-500/[0.04] p-6 flex items-center gap-3">
          <span className="text-rose-400 text-lg">⚠</span>
          <div>
            <p className="text-xs font-bold text-rose-300">Widget failed to render</p>
            <p className="text-[10px] text-muted-foreground/50 mt-0.5">
              Reload the page or check the console for details.
            </p>
          </div>
        </div>
      }
    >
      {children}
    </ErrorBoundary>
  );
}
