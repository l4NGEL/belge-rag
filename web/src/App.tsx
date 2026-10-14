import { useState } from "react";
import { ChatPanel } from "./components/ChatPanel";
import { DocumentsPanel } from "./components/DocumentsPanel";
import { login, type Session } from "./lib/api";

const STORAGE_KEY = "belge-rag-session";

function loadSession(): Session | null {
  try {
    const raw = sessionStorage.getItem(STORAGE_KEY);
    if (!raw) return null;
    const s = JSON.parse(raw) as Session;
    return new Date(s.expiresAt) > new Date() ? s : null;
  } catch {
    return null;
  }
}

export function App() {
  const [session, setSession] = useState<Session | null>(loadSession);
  const [username, setUsername] = useState("");
  const [password, setPassword] = useState("");
  const [error, setError] = useState<string | null>(null);

  async function onLogin(e: React.FormEvent) {
    e.preventDefault();
    try {
      const s = await login(username, password);
      sessionStorage.setItem(STORAGE_KEY, JSON.stringify(s));
      setSession(s);
      setError(null);
    } catch {
      setError("Kullanıcı adı veya parola hatalı.");
    }
  }

  if (!session) {
    return (
      <main className="login">
        <form onSubmit={onLogin} className="panel">
          <h1>belge-rag</h1>
          <input value={username} onChange={(e) => setUsername(e.target.value)} placeholder="Kullanıcı adı" autoFocus />
          <input type="password" value={password} onChange={(e) => setPassword(e.target.value)} placeholder="Parola" />
          <button disabled={!username || !password}>Giriş</button>
          {error && <p className="error">{error}</p>}
        </form>
      </main>
    );
  }

  return (
    <div className="app">
      <header className="top">
        <h1>belge-rag</h1>
        <span className="muted">
          {session.username} ({session.role})
        </span>
        <button
          className="ghost"
          onClick={() => {
            sessionStorage.removeItem(STORAGE_KEY);
            setSession(null);
          }}
        >
          Çıkış
        </button>
      </header>
      <main className="layout">
        <DocumentsPanel session={session} />
        <ChatPanel session={session} />
      </main>
    </div>
  );
}
