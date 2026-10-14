import { readSse } from "./sse";

export interface Session {
  accessToken: string;
  expiresAt: string;
  username: string;
  role: "admin" | "user";
}

export interface DocumentSummary {
  id: string;
  title: string;
  status: "queued" | "processing" | "indexed" | "failed";
  chunkCount: number;
  contentLength: number;
  embeddingModel: string | null;
  error: string | null;
  createdAt: string;
  indexedAt: string | null;
}

export interface SourceRef {
  number: number;
  documentId: string;
  chunkId: string;
  title: string;
  ordinal: number;
  score: number;
  snippet: string;
}

export interface Verdict {
  label: "Grounded" | "Partial" | "Ungrounded" | "NoAnswer";
  supportedSentenceRatio: number;
  citedSources: number[];
  invalidCitations: number[];
  method: string;
  note?: string | null;
}

export interface ChatMetrics {
  retrievalMs: number;
  firstTokenMs: number;
  totalMs: number;
  promptTokens: number;
  contextWindowTokens: number;
  sourcesUsed: number;
  sourcesDropped: number;
  sourcesTruncated: number;
  historyTurnsUsed: number;
  historyTurnsDropped: number;
  answerFromCache: boolean;
  standaloneQuery: string;
}

export type ChatEvent =
  | { type: "sources"; sessionId: string; sources: SourceRef[] }
  | { type: "token"; text: string }
  | { type: "verdict"; verdict: Verdict }
  | { type: "done"; metrics: ChatMetrics }
  | { type: "error"; message: string };

export class ApiError extends Error {
  constructor(public status: number, message: string) {
    super(message);
  }
}

async function check(res: Response): Promise<Response> {
  if (res.ok) return res;
  let detail = res.statusText;
  try {
    const body = await res.json();
    detail = body.detail ?? body.title ?? detail;
  } catch {
    /* gövde JSON değil */
  }
  throw new ApiError(res.status, detail);
}

const auth = (s: Session) => ({ Authorization: `Bearer ${s.accessToken}` });

export async function login(username: string, password: string): Promise<Session> {
  const res = await check(
    await fetch("/api/auth/token", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ username, password }),
    }),
  );
  return res.json();
}

export async function listDocuments(s: Session): Promise<DocumentSummary[]> {
  return (await check(await fetch("/api/documents", { headers: auth(s) }))).json();
}

export async function uploadFiles(s: Session, files: FileList): Promise<void> {
  const form = new FormData();
  for (const f of Array.from(files)) form.append("files", f);
  await check(await fetch("/api/documents/upload", { method: "POST", headers: auth(s), body: form }));
}

export async function createDocument(s: Session, title: string, content: string): Promise<void> {
  await check(
    await fetch("/api/documents", {
      method: "POST",
      headers: { ...auth(s), "Content-Type": "application/json" },
      body: JSON.stringify({ title, content }),
    }),
  );
}

export async function* streamChat(
  s: Session,
  question: string,
  sessionId: string | null,
  signal: AbortSignal,
): AsyncGenerator<ChatEvent> {
  const res = await check(
    await fetch("/api/chat/stream", {
      method: "POST",
      headers: { ...auth(s), "Content-Type": "application/json", Accept: "text/event-stream" },
      body: JSON.stringify({ question, sessionId, topK: 5 }),
      signal,
    }),
  );
  for await (const msg of readSse(res, signal)) yield JSON.parse(msg.data) as ChatEvent;
}
