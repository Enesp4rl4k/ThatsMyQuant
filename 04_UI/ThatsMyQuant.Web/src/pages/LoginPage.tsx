import { useState, type FormEvent } from "react";
import { useNavigate, useLocation, Navigate } from "react-router-dom";
import { useAuth } from "@/hooks/useAuth";
import { ApiError } from "@/api/client";
import { cn } from "@/lib/utils";

export function LoginPage() {
  const auth = useAuth();
  const navigate = useNavigate();
  const location = useLocation();
  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("");
  const [remember, setRemember] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [submitting, setSubmitting] = useState(false);

  if (auth.authEnabled === false || auth.user) {
    const dest = (location.state as { from?: string } | null)?.from ?? "/";
    return <Navigate to={dest} replace />;
  }

  async function onSubmit(e: FormEvent<HTMLFormElement>) {
    e.preventDefault();
    setSubmitting(true);
    setError(null);
    try {
      await auth.login(email.trim(), password, remember);
      const dest = (location.state as { from?: string } | null)?.from ?? "/";
      navigate(dest, { replace: true });
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) {
        setError("Those credentials don’t match anyone we know.");
      } else if (err instanceof ApiError && err.status === 400) {
        setError("Authentication is disabled on this server.");
      } else {
        setError(err instanceof Error ? err.message : "Login failed. Check the API server.");
      }
    } finally {
      setSubmitting(false);
    }
  }

  return (
    <div
      className="min-h-screen relative flex items-center justify-center p-6"
      style={{ backgroundColor: "var(--background, #FAF9F5)" }}
    >
      {/* Subtle radial wash, very low opacity — paper feel */}
      <div
        className="absolute top-[-30%] right-[-10%] w-[60%] h-[60%] rounded-full pointer-events-none"
        style={{
          background:
            "radial-gradient(circle, rgba(30, 58, 138, 0.08), transparent 65%)",
          filter: "blur(40px)",
        }}
        aria-hidden
      />
      <div
        className="absolute bottom-[-20%] left-[-15%] w-[55%] h-[55%] rounded-full pointer-events-none"
        style={{
          background:
            "radial-gradient(circle, rgba(23, 46, 111, 0.06), transparent 65%)",
          filter: "blur(40px)",
        }}
        aria-hidden
      />

      <div className="relative w-full max-w-md">
        {/* Brand */}
        <div className="mb-10 text-center">
          <div className="inline-flex items-center gap-2 mb-5">
            <div className="w-1.5 h-1.5 rounded-full bg-[#1E3A8A] animate-[pulse_2s_infinite]" />
            <span className="text-[10px] uppercase tracking-[0.3em] text-[#1E3A8A] font-medium">
              Live console
            </span>
          </div>
          <h1
            className="font-serif text-[44px] leading-[1.05] tracking-tight text-[#1F1E1B] mb-3"
            style={{ fontFamily: "Newsreader, Source Serif 4, Georgia, serif" }}
          >
            ThatsMyQuant<span className="text-[#1E3A8A]">.</span>
          </h1>
          <p
            className="font-serif italic text-[#6B6A64] text-[15px] leading-relaxed max-w-xs mx-auto"
            style={{ fontFamily: "Newsreader, Source Serif 4, Georgia, serif" }}
          >
            A trading console for thoughtful operators — quantitative, calm, deliberate.
          </p>
        </div>

        {/* Form card */}
        <form
          onSubmit={onSubmit}
          className="rounded-2xl border border-[rgba(31,30,27,0.08)] bg-white shadow-[0_4px_24px_rgba(31,30,27,0.04)] p-7 space-y-5"
        >
          <div className="space-y-1.5">
            <label
              htmlFor="email"
              className="text-[11px] font-medium tracking-wide text-[#3D3B36] block"
            >
              Email
            </label>
            <input
              id="email"
              type="email"
              autoComplete="email"
              value={email}
              onChange={(e) => setEmail(e.target.value)}
              className="w-full bg-[#FAF9F5] border border-[rgba(31,30,27,0.12)] rounded-md px-3 py-2.5 text-[14px] focus:border-[#1E3A8A] focus:bg-white focus:ring-2 focus:ring-[rgba(30,58,138,0.15)] outline-none transition-all"
              required
              placeholder="admin@thatsmyquant.com"
              autoFocus
            />
          </div>

          <div className="space-y-1.5">
            <label
              htmlFor="password"
              className="text-[11px] font-medium tracking-wide text-[#3D3B36] block"
            >
              Password
            </label>
            <input
              id="password"
              type="password"
              autoComplete="current-password"
              value={password}
              onChange={(e) => setPassword(e.target.value)}
              className="w-full bg-[#FAF9F5] border border-[rgba(31,30,27,0.12)] rounded-md px-3 py-2.5 text-[14px] focus:border-[#1E3A8A] focus:bg-white focus:ring-2 focus:ring-[rgba(30,58,138,0.15)] outline-none transition-all"
              required
            />
          </div>

          <label className="flex items-center gap-2.5 text-[12px] text-[#6B6A64] cursor-pointer select-none">
            <input
              type="checkbox"
              checked={remember}
              onChange={(e) => setRemember(e.target.checked)}
              className="w-4 h-4 rounded accent-[#1E3A8A]"
            />
            <span>Keep me signed in on this device</span>
          </label>

          {error && (
            <div className="rounded-md border border-[rgba(181,54,75,0.25)] bg-[rgba(181,54,75,0.05)] px-3.5 py-2.5">
              <p className="text-[12px] text-[#B5364B] font-serif italic leading-snug">
                {error}
              </p>
            </div>
          )}

          <button
            type="submit"
            disabled={submitting || !email || !password}
            className={cn(
              "w-full py-3 rounded-md text-[14px] font-medium tracking-wide transition-all",
              submitting || !email || !password
                ? "bg-[rgba(31,30,27,0.06)] text-[#A8A69E] cursor-not-allowed"
                : "bg-[#1F1E1B] text-white hover:bg-[#2C2A26] shadow-[0_1px_2px_rgba(31,30,27,0.1)]"
            )}
          >
            {submitting ? "Signing in…" : "Sign in"}
          </button>
        </form>

        {/* Footer */}
        <p
          className="text-center mt-7 text-[12px] text-[#A8A69E] font-serif italic"
          style={{ fontFamily: "Newsreader, Source Serif 4, Georgia, serif" }}
        >
          Built for the patient quant.
        </p>
      </div>
    </div>
  );
}
