interface StubPageProps {
  title: string;
  description?: string;
  phase?: string;
}

export function StubPage({ title, description, phase = "Coming next phase" }: StubPageProps) {
  return (
    <div className="flex flex-col items-center justify-center min-h-[60vh] text-center">
      <span className="text-[10px] font-black uppercase tracking-[0.4em] text-accent/60 mb-3">
        {phase}
      </span>
      <h1 className="text-4xl font-black tracking-tighter mb-4">
        <span className="bg-gradient-to-r from-white via-white to-white/40 bg-clip-text text-transparent">
          {title}
        </span>
      </h1>
      {description && (
        <p className="text-sm text-muted-foreground/70 max-w-md leading-relaxed">{description}</p>
      )}
      <div className="mt-8 h-px w-32 bg-gradient-to-r from-transparent via-accent/40 to-transparent" />
    </div>
  );
}
