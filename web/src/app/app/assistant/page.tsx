"use client";

import { useEffect, useRef, useState } from "react";
import { Bot, Send, Sparkles, User } from "lucide-react";
import { api } from "@/lib/api";
import { Badge, Button, Card, ErrorNote, Input, PageHeader, cx, errorText } from "@/components/ui";

type Turn = { role: "user" | "assistant"; text: string; source?: string; sections?: string[] };
type Status = { aiEnabled: boolean; model: string | null };

const examples = ["What should I do first today?", "How is our OEE and which machine is worst?", "Why is the energy bill high?", "Any machines likely to break down?",
  "Which spare parts are running low?", "今天的稼動率怎麼樣?", "Carbon emissions this week?"];

/** Ask about the factory in plain language; answers come from the live data of every module in the plan. */
export default function AssistantPage() {
  const [status, setStatus] = useState<Status | null>(null);
  const [turns, setTurns] = useState<Turn[]>([]);
  const [q, setQ] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const end = useRef<HTMLDivElement>(null);
  useEffect(() => { api<Status>("/api/v1/assistant/status").then(setStatus).catch((e) => setError(errorText(e))); }, []);
  useEffect(() => { end.current?.scrollIntoView({ behavior: "smooth" }); }, [turns, busy]);

  async function ask(question: string) {
    if (!question.trim() || busy) return;
    const history = turns.map((t) => ({ role: t.role, text: t.text }));
    setTurns((t) => [...t, { role: "user", text: question }]);
    setQ("");
    setBusy(true);
    try {
      const r = await api<{ answer: string; source: string; sections: string[] }>("/api/v1/assistant/ask", { method: "POST", json: { question, history } });
      setTurns((t) => [...t, { role: "assistant", text: r.answer, source: r.source, sections: r.sections }]);
      setError(null);
    } catch (err) {
      setError(errorText(err));
    } finally {
      setBusy(false);
    }
  }

  return (
    <>
      <PageHeader title="AI assistant" sub="Ask about OEE, energy, alarms, maintenance, quality, stock, carbon or the robot fleet — in English or 中文."
        actions={status && <Badge tone={status.aiEnabled ? "ok" : "info"}>{status.aiEnabled ? `AI: ${status.model}` : "Built-in analysis"}</Badge>} />
      <ErrorNote error={error} />
      <Card className="flex min-h-[60vh] flex-col">
        <div className="flex-1 space-y-4 overflow-y-auto pb-4">
          {turns.length === 0 && (
            <div className="py-6 text-center">
              <Sparkles className="mx-auto mb-2 text-brand" />
              <p className="mb-4 text-sm text-muted">Try one of these:</p>
              <div className="flex flex-wrap justify-center gap-2">
                {examples.map((e) => <button key={e} onClick={() => ask(e)} className="rounded-full border border-line px-3 py-1.5 text-sm hover:bg-panel-2">{e}</button>)}
              </div>
              {status && !status.aiEnabled && (
                <p className="mx-auto mt-6 max-w-lg text-xs text-muted">Answers come from FactoryOS&apos;s built-in analysis of your live data. To allow free-form questions, the server administrator can add an
                  Anthropic API key (setting <code>Ai:AnthropicApiKey</code>) — it is kept on the server, never in the browser.</p>
              )}
            </div>
          )}
          {turns.map((t, i) => (
            <div key={i} className={cx("flex gap-3", t.role === "user" && "flex-row-reverse")}>
              <div className={cx("grid h-8 w-8 shrink-0 place-items-center rounded-full", t.role === "user" ? "bg-brand text-white" : "bg-panel-2 text-brand")}>
                {t.role === "user" ? <User size={16} /> : <Bot size={16} />}
              </div>
              <div className={cx("max-w-[80%] rounded-xl px-4 py-3 text-sm", t.role === "user" ? "bg-brand text-white" : "bg-panel-2")}>
                <div className="whitespace-pre-wrap">{t.text}</div>
                {t.source && <div className="mt-2 text-[11px] opacity-70">{t.source === "ai" ? "AI answer" : "Built-in analysis"} · data: {t.sections?.join(", ")}</div>}
              </div>
            </div>
          ))}
          {busy && <div className="flex gap-3"><div className="grid h-8 w-8 place-items-center rounded-full bg-panel-2 text-brand"><Bot size={16} /></div><div className="rounded-xl bg-panel-2 px-4 py-3 text-sm text-muted">Looking at the factory data…</div></div>}
          <div ref={end} />
        </div>
        <form onSubmit={(e) => { e.preventDefault(); ask(q); }} className="flex gap-2 border-t border-line pt-4">
          <Input value={q} onChange={(e) => setQ(e.target.value)} placeholder="Ask a question about your factory…" maxLength={1000} aria-label="Question" />
          <Button type="submit" disabled={busy || !q.trim()}><Send size={14} /> Ask</Button>
        </form>
      </Card>
    </>
  );
}
