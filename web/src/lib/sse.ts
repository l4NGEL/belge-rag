// fetch + ReadableStream ile SSE okuyucu. Tarayıcının EventSource'u yalnızca GET destekler ve
// Authorization başlığı gönderemez; sohbet POST + Bearer token gerektirdiği için akış elle çözümlenir.

export interface SseMessage {
  event: string;
  data: string;
}

/**
 * Parçalı gelen metni SSE olaylarına çevirir. Ağ katmanı bir olayı herhangi bir yerden
 * bölebilir; tamamlanmamış kuyruk bir sonraki çağrıya taşınır.
 */
export class SseParser {
  private buffer = "";

  push(chunk: string): SseMessage[] {
    this.buffer += chunk.replace(/\r\n/g, "\n");
    const out: SseMessage[] = [];
    let sep: number;
    while ((sep = this.buffer.indexOf("\n\n")) >= 0) {
      const block = this.buffer.slice(0, sep);
      this.buffer = this.buffer.slice(sep + 2);
      let event = "message";
      const data: string[] = [];
      for (const line of block.split("\n")) {
        if (line.startsWith(":")) continue; // yorum / heartbeat
        if (line.startsWith("event:")) event = line.slice(6).trim();
        else if (line.startsWith("data:")) data.push(line.slice(5).replace(/^ /, ""));
      }
      if (data.length > 0) out.push({ event, data: data.join("\n") });
    }
    return out;
  }
}

export async function* readSse(response: Response, signal?: AbortSignal): AsyncGenerator<SseMessage> {
  if (!response.body) throw new Error("Yanıt gövdesi yok");
  const reader = response.body.getReader();
  const decoder = new TextDecoder("utf-8");
  const parser = new SseParser();
  try {
    while (true) {
      if (signal?.aborted) return;
      const { value, done } = await reader.read();
      if (done) break;
      for (const msg of parser.push(decoder.decode(value, { stream: true }))) yield msg;
    }
  } finally {
    reader.releaseLock();
  }
}
