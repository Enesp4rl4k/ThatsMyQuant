import { FinancialScoreCards } from "@/components/ui/financial-score-cards";

export default function ScoreCardsDemo() {
  return (
    <div className="flex flex-col items-center justify-center min-h-screen bg-background">
      <div className="w-full max-w-7xl px-6 py-12">
        <div className="mb-12 text-center">
          <h1 className="text-4xl font-black tracking-tighter text-foreground mb-4">FINANCIAL VITALS</h1>
          <p className="text-muted-foreground max-w-2xl mx-auto">
            Real-time analytics for your wealth protection, investment alignment, and overall financial health.
          </p>
        </div>
        <FinancialScoreCards />
      </div>
    </div>
  );
}
