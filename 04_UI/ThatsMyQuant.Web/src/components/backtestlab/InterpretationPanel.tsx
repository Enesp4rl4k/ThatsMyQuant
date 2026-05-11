import { useMutation } from "@tanstack/react-query";
import {
  backtestLabApi,
  buildInterpretRequest,
  type AnalyzeResponse,
  type BacktestInterpretation,
} from "@/api/backtestLab";

interface Props {
  data: AnalyzeResponse;
  label?: string;
}

/**
 * LLM interpretation panel. Operator clicks "Interpret" → POSTs the analysis
 * summary to /api/backtest-lab/interpret → renders the returned markdown.
 *
 * Why a button instead of auto-running on every analyze:
 *   - LLM calls have latency (3-15s typical) and cost (~$0.01 per call).
 *   - Operator may want to scan numbers before deciding interpretation is worth it.
 *   - Keeps the analyze call fast and deterministic.
 *
 * Pre-reg discipline: the system prompt forbids action recommendations
 * (no "deploy this", no "size up"). Verified at the prompt level in
 * BacktestInterpreterService.SystemPrompt.
 */
export function InterpretationPanel({ data, label }: Props) {
  const mutation = useMutation({
    mutationFn: backtestLabApi.interpret,
  });

  const handleInterpret = () => {
    mutation.mutate(buildInterpretRequest(data, label));
  };

  return (
    <section className="rounded-lg border border-white/5 bg-[#0D1117]/60 backdrop-blur-xl p-4 space-y-3">
      <div className="flex items-center justify-between">
        <h3 className="text-xs font-black uppercase tracking-[0.18em] text-muted-foreground/70">
          AI interpretation
          {mutation.data?.provider && (
            <span className="ml-2 text-muted-foreground/40 normal-case font-mono text-[10px]">
              · {mutation.data.provider}
            </span>
          )}
        </h3>
        <button
          onClick={handleInterpret}
          disabled={mutation.isPending}
          className="text-[11px] px-3 py-1 rounded bg-accent/15 border border-accent/40 text-accent hover:bg-accent/25 disabled:opacity-30 transition"
        >
          {mutation.isPending
            ? "Interpreting…"
            : mutation.data
              ? "Re-interpret"
              : "Interpret →"}
        </button>
      </div>

      {!mutation.data && !mutation.isPending && !mutation.isError && (
        <p className="text-xs text-muted-foreground/60 italic">
          Click <strong>Interpret</strong> to get a plain-Turkish narrative
          covering the regression, MC fan, and entropy results above. Decision
          remains with you — the interpreter explains numbers, never recommends
          action (deployment, sizing, capital allocation).
        </p>
      )}

      {mutation.isPending && (
        <p className="text-xs text-muted-foreground/60 italic">
          Talking to {label ? "LLM" : "the model"} (3–15s typical)…
        </p>
      )}

      {mutation.isError && (
        <p className="text-xs text-rose-400 font-mono">
          Server error: {(mutation.error as Error).message}
        </p>
      )}

      {mutation.data && <InterpretationBody result={mutation.data} />}
    </section>
  );
}

function InterpretationBody({ result }: { result: BacktestInterpretation }) {
  if (result.error) {
    return (
      <div className="text-xs text-amber-400 font-mono">
        ⚠ Interpreter unavailable: {result.error}
      </div>
    );
  }
  if (!result.markdown.trim()) {
    return (
      <p className="text-xs text-muted-foreground/60 italic">
        Empty response from the model. Check provider configuration.
      </p>
    );
  }
  return (
    // Lightweight markdown rendering: we render headings, bold, and bullet
    // lists with simple regex transforms — no need to add a markdown lib for
    // a single panel. The system prompt's output format is fixed
    // (## H2 + bullets) so we don't need general-purpose markdown parsing.
    <div className="prose prose-invert prose-sm max-w-none text-foreground/90 space-y-2">
      {renderSimpleMarkdown(result.markdown)}
    </div>
  );
}

/** Minimal markdown → React. Handles ##/###, bullets, **bold**, paragraphs.
 *  Replace with a real lib (react-markdown) if/when we need code blocks
 *  or tables — for the structured prompt output, this is sufficient. */
function renderSimpleMarkdown(text: string): React.ReactNode {
  const blocks = text.split(/\n{2,}/).map((b) => b.trim()).filter(Boolean);
  return blocks.map((block, i) => {
    if (block.startsWith("## ")) {
      return (
        <h4
          key={i}
          className="text-[11px] font-black uppercase tracking-[0.18em] text-accent mt-3 mb-1"
        >
          {block.slice(3)}
        </h4>
      );
    }
    if (block.startsWith("### ")) {
      return (
        <h5 key={i} className="text-xs font-bold text-foreground mt-2 mb-1">
          {block.slice(4)}
        </h5>
      );
    }
    // Bullet list block
    if (block.split("\n").every((l) => /^\s*[-*]\s/.test(l))) {
      const items = block
        .split("\n")
        .map((l) => l.replace(/^\s*[-*]\s/, ""));
      return (
        <ul key={i} className="list-disc list-inside text-xs space-y-1">
          {items.map((it, j) => (
            <li key={j}>{renderInline(it)}</li>
          ))}
        </ul>
      );
    }
    // Plain paragraph
    return (
      <p key={i} className="text-xs leading-relaxed">
        {renderInline(block)}
      </p>
    );
  });
}

function renderInline(text: string): React.ReactNode {
  // Split on **bold**, keeping the delimiters in the result so we can rebuild.
  const parts = text.split(/(\*\*[^*]+\*\*)/g);
  return parts.map((p, i) => {
    if (p.startsWith("**") && p.endsWith("**")) {
      return (
        <strong key={i} className="text-foreground">
          {p.slice(2, -2)}
        </strong>
      );
    }
    return <span key={i}>{p}</span>;
  });
}
