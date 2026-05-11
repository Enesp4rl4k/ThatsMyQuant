import { NavLink } from "react-router-dom";
import { cn } from "@/lib/utils";
import { useSidebar } from "@/hooks/useSidebar";

const navItems = [
  { to: "/", label: "Dashboard", glyph: "01" },
  { to: "/markets", label: "Markets", glyph: "02" },
  { to: "/cockpit", label: "Live Cockpit", glyph: "⊙" },
  { to: "/positions", label: "Positions", glyph: "03" },
  { to: "/trades", label: "Trade Journal", glyph: "04" },
  { to: "/alpha", label: "Alpha Portfolio", glyph: "05" },
  { to: "/funding", label: "Funding Monitor", glyph: "06" },
  { to: "/pairs", label: "Pairs Discovery", glyph: "07" },
  { to: "/quant-lab", label: "Quant Lab", glyph: "08" },
  { to: "/pro-mode", label: "Pro Mode", glyph: "09" },
  { to: "/diagnostics", label: "Signal Diagnostics", glyph: "◉" },
  { to: "/audit", label: "Audit Log", glyph: "10" },
  { to: "/health", label: "System Health", glyph: "11" },
  { to: "/users", label: "Users", glyph: "12" },
  { to: "/settings", label: "Settings", glyph: "13" },
];

export function Sidebar() {
  const { mobileOpen, close } = useSidebar();

  return (
    <>
      {/* Mobile backdrop */}
      {mobileOpen && (
        <div
          className="fixed inset-0 z-30 bg-black/60 backdrop-blur-sm lg:hidden"
          onClick={close}
        />
      )}

      <aside
        aria-label="Primary navigation"
        className={cn(
          "w-60 shrink-0 border-r border-white/5 bg-[#070A0F]/95 backdrop-blur-xl flex flex-col",
          "fixed lg:static inset-y-0 left-0 z-40 transition-transform duration-200 ease-out",
          mobileOpen ? "translate-x-0" : "-translate-x-full lg:translate-x-0"
        )}
      >
        <div className="px-6 py-6 border-b border-white/5">
          <div className="flex items-center gap-2 mb-2">
            <div className="w-1.5 h-1.5 rounded-full bg-accent animate-[pulse_2s_infinite]" />
            <span className="text-[10px] uppercase tracking-[0.25em] text-accent/80 font-medium">Live</span>
          </div>
          <h1 className="font-serif text-2xl tracking-tight leading-none text-foreground">
            ThatsMyQuant<span className="text-accent">.</span>
          </h1>
          <p className="font-serif italic text-[12px] text-muted-foreground/70 mt-1.5">
            Trading console
          </p>
        </div>

        <nav className="flex-1 px-3 py-4 space-y-1 overflow-y-auto" aria-label="Main pages">
          {navItems.map((item) => (
            <NavLink
              key={item.to}
              to={item.to}
              end={item.to === "/"}
              onClick={close}
              aria-label={item.label}
              className={({ isActive }) =>
                cn(
                  "group flex items-center gap-3 px-3 py-2 rounded-md text-sm font-medium transition-colors",
                  isActive
                    ? "bg-accent/10 text-accent border-l-2 border-accent"
                    : "text-muted-foreground hover:text-foreground hover:bg-white/[0.02] border-l-2 border-transparent"
                )
              }
            >
              <span className="font-mono text-[10px] tracking-tighter opacity-60 group-hover:opacity-100">
                {item.glyph}
              </span>
              <span>{item.label}</span>
            </NavLink>
          ))}
        </nav>

        <div className="px-6 py-4 border-t border-white/5 text-[11px] text-muted-foreground/50 font-serif italic">
          v0.2 · Web
        </div>
      </aside>
    </>
  );
}
