import { describe, expect, it } from "vitest";
import { SseParser } from "./sse";

describe("SseParser", () => {
  it("parses named events with JSON data", () => {
    const p = new SseParser();
    const msgs = p.push('event: token\ndata: {"text":"Mer"}\n\nevent: token\ndata: {"text":"haba"}\n\n');
    expect(msgs).toEqual([
      { event: "token", data: '{"text":"Mer"}' },
      { event: "token", data: '{"text":"haba"}' },
    ]);
  });

  it("keeps partial frames until the separator arrives", () => {
    const p = new SseParser();
    expect(p.push("event: done\nda")).toEqual([]);
    expect(p.push('ta: {"ok":true}\n')).toEqual([]);
    expect(p.push("\n")).toEqual([{ event: "done", data: '{"ok":true}' }]);
  });

  it("joins multi-line data, ignores comments and handles CRLF", () => {
    const p = new SseParser();
    const msgs = p.push(": ping\r\n\r\nevent: x\r\ndata: a\r\ndata: b\r\n\r\n");
    expect(msgs).toEqual([{ event: "x", data: "a\nb" }]);
  });
});
