import { useRef, useState } from "react";
import { streamChat, type ChatMetrics, type Session, type SourceRef, type Verdict } from "../lib/api";

interface Turn {
  question: string;
  answer: string;
  sources: SourceRef[];
  verdict?: Verdict;
  metrics?: ChatMetrics;
  error?: string;
  streaming: boolean;
}

const verdictText: Record<Verdict["label"], string> = {
  Grounded: "Kaynaklara dayanıyor",
  Partial: "Kısmen dayanıyor",
  Ungrounded: "Dayanak zayıf",
  NoAnswer: "Kaynaklarda yanıt yok",
};

export function ChatPanel({ session }: { session: Session }) {
  const [turns, setTurns] = useState<Turn[]>([]);
  const [question, setQuestion] = useState("");
  const [sessionId, setSessionId] = useState<string | null>(null);
  const abort = useRef<AbortController | null>(null);
  const busy = turns.some((t) => t.streaming);

  const update = (fn: (t: Turn) => Turn) =>
    setTurns((prev) => [...prev.slice(0, -1), fn(prev[prev.length - 1])]);

  async function ask(e: React.FormEvent) {
    e.preventDefault();
    const q = question.trim();
    if (q.length < 2 || busy) return;
    setQuestion("");
    setTurns((prev) => [...prev, { question: q, answer: "", sources: [], streaming: true }]);
    abort.current = new AbortController();
    try {
      for await (const evt of streamChat(session, q, sessionId, abort.current.signal)) {
        switch (evt.type) {
          case "sources":
            setSessionId(evt.sessionId);
            update((t) => ({ ...t, sources: evt.sources }));
            break;
          case "token":
            update((t) => ({ ...t, answer: t.answer + evt.text }));
            break;
          case "verdict":
            update((t) => ({ ...t, verdict: evt.verdict }));
            break;
          case "done":
            update((t) => ({ ...t, metrics: evt.metrics }));
            break;
          case "error":
            update((t) => ({ ...t, error: evt.message }));
            break;
        }
      }
    } catch (err) {
      if ((err as Error).name !== "AbortError") update((t) => ({ ...t, error: (err as Error).message }));
    } finally {
      update((t) => ({ ...t, streaming: false }));
    }
  }

  return (
    <section className="panel chat">
      <header className="panel-head">
        <h2>Sohbet</h2>
        <button
          className="ghost"
          disabled={busy}
          onClick={() => {
            setTurns([]);
            setSessionId(null);
          }}
        >
          Yeni oturum
        </button>
      </header>

      <div className="turns">
        {turns.length === 0 && <p className="muted">İndekslenmiş belgeler hakkında bir soru sorun.</p>}
        {turns.map((t, i) => (
          <article key={i} className="turn">
            <p className="question">{t.question}</p>
            <p className="answer">
              {t.answer}
              {t.streaming && <span className="cursor" aria-hidden />}
            </p>
            {t.error && <p className="error">{t.error}</p>}
            {t.verdict && (
              <p className={`badge ${t.verdict.label.toLowerCase()}`}>
                {verdictText[t.verdict.label]} · desteklenen cümle oranı{" "}
                {(t.verdict.supportedSentenceRatio * 100).toFixed(0)}% · {t.verdict.method}
                {t.verdict.invalidCitations.length > 0 && ` · geçersiz atıf: ${t.verdict.invalidCitations.join(", ")}`}
              </p>
            )}
            {t.metrics && (
              <p className="metrics">
                ilk token {t.metrics.firstTokenMs.toFixed(0)} ms · toplam {t.metrics.totalMs.toFixed(0)} ms · arama{" "}
                {t.metrics.retrievalMs.toFixed(0)} ms · prompt ≈{t.metrics.promptTokens}/{t.metrics.contextWindowTokens} token
                {t.metrics.sourcesDropped + t.metrics.sourcesTruncated > 0 &&
                  ` · bütçe: ${t.metrics.sourcesTruncated} kesildi, ${t.metrics.sourcesDropped} atıldı`}
                {t.metrics.answerFromCache && " · önbellekten"}
              </p>
            )}
            {t.sources.length > 0 && (
              <details className="sources">
                <summary>{t.sources.length} kaynak</summary>
                <ol>
                  {t.sources.map((s) => (
                    <li key={s.chunkId}>
                      <strong>
                        [{s.number}] {s.title}
                      </strong>{" "}
                      <span className="muted">#{s.ordinal}</span>
                      <p>{s.snippet}</p>
                    </li>
                  ))}
                </ol>
              </details>
            )}
          </article>
        ))}
      </div>

      <form className="ask" onSubmit={ask}>
        <input
          value={question}
          onChange={(e) => setQuestion(e.target.value)}
          placeholder="Sorunuz…"
          maxLength={2000}
          aria-label="Soru"
        />
        {busy ? (
          <button type="button" onClick={() => abort.current?.abort()}>
            Durdur
          </button>
        ) : (
          <button type="submit" disabled={question.trim().length < 2}>
            Sor
          </button>
        )}
      </form>
    </section>
  );
}
