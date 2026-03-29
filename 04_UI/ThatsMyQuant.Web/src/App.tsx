"use client";

import { ThemeProvider } from "next-themes";
import { FinancialTable } from "@/components/ui/financial-markets-table";
import { FinancialScoreCards } from "@/components/ui/financial-score-cards";

function App() {
  return (
    <ThemeProvider attribute="class" defaultTheme="dark" enableSystem>
      <div className="grain" />
      <div className="min-h-screen bg-[#05070A] text-foreground font-sans selection:bg-accent/30 p-8 relative overflow-hidden">
        {/* ATMOSPHERIC ORBS */}
        <div className="absolute top-[-10%] left-[-10%] w-[40%] h-[40%] bg-accent/5 rounded-full blur-[120px] pointer-events-none" />
        <div className="absolute bottom-[-10%] right-[-10%] w-[40%] h-[40%] bg-indigo-500/5 rounded-full blur-[120px] pointer-events-none" />

        <div className="max-w-7xl mx-auto space-y-12 relative z-10">
          <header className="flex flex-col md:flex-row md:items-center justify-between gap-8 pb-12">
            <div className="relative">
              <div className="flex items-center gap-3 mb-3">
                <div className="flex gap-1">
                  <div className="w-1 h-3 bg-accent animate-[pulse_2s_infinite]" />
                  <div className="w-1 h-3 bg-accent/40" />
                  <div className="w-1 h-3 bg-accent/10" />
                </div>
                <span className="text-[10px] font-black uppercase tracking-[0.3em] text-accent/80">System Connectivity: Nominal</span>
              </div>
              <h1 className="text-5xl font-black tracking-tighter leading-none mb-2">
                <span className="bg-gradient-to-r from-white via-white to-white/40 bg-clip-text text-transparent">INSTITUTIONAL</span>
                <br />
                <span className="text-accent italic font-light tracking-widest text-3xl opacity-80 uppercase">Analytics Node</span>
              </h1>
              <div className="h-px w-32 bg-gradient-to-r from-accent to-transparent mt-4" />
            </div>
            
            <div className="group relative">
               {/* GLASS HUD MODULE */}
               <div className="absolute -inset-0.5 bg-gradient-to-br from-accent/20 to-transparent rounded-2xl blur opacity-20 group-hover:opacity-40 transition" />
               <div className="relative flex items-center gap-8 px-8 py-5 bg-[#0D1117]/60 backdrop-blur-xl rounded-2xl border border-white/5 shadow-2xl overflow-hidden">
                  <div className="absolute top-0 left-0 w-full h-px bg-gradient-to-r from-transparent via-accent/30 to-transparent" />
                  
                  <div className="flex flex-col">
                    <span className="text-[9px] font-black text-muted-foreground/40 uppercase tracking-widest mb-1">Telemetry Latency</span>
                    <div className="flex items-end gap-1">
                      <span className="text-2xl font-mono font-bold text-green-400 leading-none">12</span>
                      <span className="text-[10px] font-bold text-green-400/50 mb-0.5">MS</span>
                    </div>
                  </div>

                  <div className="w-px h-10 bg-white/5" />

                  <div className="flex flex-col">
                    <span className="text-[9px] font-black text-muted-foreground/40 uppercase tracking-widest mb-1">Active Uplink</span>
                    <div className="flex items-center gap-2">
                      <div className="w-2 h-2 rounded-full bg-accent shadow-[0_0_8px_rgba(0,191,255,0.8)]" />
                      <span className="text-sm font-black text-foreground uppercase tracking-tight">Mainnet-Alpha</span>
                    </div>
                  </div>
               </div>
            </div>
          </header>

          <section className="animate-in fade-in slide-in-from-bottom-8 duration-1000">
             <div className="mb-10 relative">
               <div className="absolute -left-4 top-0 bottom-0 w-1 bg-accent shadow-[0_0_15px_rgba(0,191,255,0.5)]" />
               <h2 className="text-[10px] font-black uppercase tracking-[0.4em] text-accent mb-1 ml-2">Protocol 01 / Health</h2>
               <p className="text-2xl font-black tracking-tight ml-2">Financial Vitals <span className="text-muted-foreground/20 font-light truncate">— Real-time Portfolio Calibration</span></p>
             </div>
             <FinancialScoreCards />
          </section>

          <main className="animate-in fade-in slide-in-from-bottom-4 duration-1000">
            <div className="mb-10 relative">
               <div className="absolute -left-4 top-0 bottom-0 w-1 bg-accent shadow-[0_0_15px_rgba(0,191,255,0.5)]" />
               <h2 className="text-[10px] font-black uppercase tracking-[0.4em] text-accent mb-1 ml-2">Protocol 02 / Liquidity</h2>
               <p className="text-2xl font-black tracking-tight ml-2">Market Monitoring <span className="text-muted-foreground/20 font-light">— Global Asset Streams</span></p>
             </div>
            <FinancialTable title="Index Registry" />
          </main>
          
          <footer className="pt-12 border-t border-border/10 flex justify-between items-center opacity-40 hover:opacity-100 transition-opacity">
            <div className="text-[10px] font-bold uppercase tracking-widest">ThatsMyQuant © 2026</div>
            <div className="flex gap-4">
              <div className="w-1.5 h-1.5 rounded-full bg-green-500" title="API Status: Excellent" />
              <div className="w-1.5 h-1.5 rounded-full bg-green-500" title="WS Stream: Active" />
              <div className="w-1.5 h-1.5 rounded-full bg-accent" title="Auth: Hardware Encrypted" />
            </div>
          </footer>
        </div>
      </div>
    </ThemeProvider>
  );
}

export default App;
