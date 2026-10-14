import { useCallback, useEffect, useState } from "react";
import { createDocument, listDocuments, uploadFiles, type DocumentSummary, type Session } from "../lib/api";

const statusText: Record<DocumentSummary["status"], string> = {
  queued: "kuyrukta",
  processing: "işleniyor",
  indexed: "indekslendi",
  failed: "başarısız",
};

export function DocumentsPanel({ session }: { session: Session }) {
  const [docs, setDocs] = useState<DocumentSummary[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [title, setTitle] = useState("");
  const [content, setContent] = useState("");
  const isAdmin = session.role === "admin";

  const refresh = useCallback(async () => {
    try {
      setDocs(await listDocuments(session));
      setError(null);
    } catch (e) {
      setError((e as Error).message);
    }
  }, [session]);

  // Kuyrukta/işlenen belge varken durumu yokla; hepsi bitince dur.
  const pending = docs.some((d) => d.status === "queued" || d.status === "processing");
  useEffect(() => {
    refresh();
  }, [refresh]);
  useEffect(() => {
    if (!pending) return;
    const id = setInterval(refresh, 1000);
    return () => clearInterval(id);
  }, [pending, refresh]);

  async function onFiles(e: React.ChangeEvent<HTMLInputElement>) {
    if (!e.target.files?.length) return;
    try {
      await uploadFiles(session, e.target.files);
      e.target.value = "";
      await refresh();
    } catch (err) {
      setError((err as Error).message);
    }
  }

  async function onPaste(e: React.FormEvent) {
    e.preventDefault();
    try {
      await createDocument(session, title.trim(), content);
      setTitle("");
      setContent("");
      await refresh();
    } catch (err) {
      setError((err as Error).message);
    }
  }

  return (
    <section className="panel docs">
      <header className="panel-head">
        <h2>Belgeler</h2>
        <button className="ghost" onClick={refresh}>
          Yenile
        </button>
      </header>

      {isAdmin ? (
        <>
          <label className="file">
            .md / .txt yükle
            <input type="file" accept=".md,.txt" multiple onChange={onFiles} />
          </label>
          <form className="paste" onSubmit={onPaste}>
            <input value={title} onChange={(e) => setTitle(e.target.value)} placeholder="Başlık" maxLength={200} />
            <textarea value={content} onChange={(e) => setContent(e.target.value)} placeholder="Metin" rows={4} />
            <button disabled={!title.trim() || !content.trim()}>Kuyruğa ekle</button>
          </form>
        </>
      ) : (
        <p className="muted">Belge eklemek için admin rolü gerekir.</p>
      )}

      {error && <p className="error">{error}</p>}
      <ul className="doc-list">
        {docs.map((d) => (
          <li key={d.id}>
            <span className="doc-title">{d.title}</span>
            <span className={`status ${d.status}`}>{statusText[d.status]}</span>
            <span className="muted">
              {d.status === "indexed" ? `${d.chunkCount} parça` : d.error ?? ""}
            </span>
          </li>
        ))}
      </ul>
    </section>
  );
}
