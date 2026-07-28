// Minimal SignalR client (zero-dependency, Node 22+ for the global WebSocket).
//
// The load tests stay dependency-free — same as clap-storm.mjs — so this speaks the SignalR JSON
// protocol directly rather than pulling in @microsoft/signalr. The protocol is small:
//   1. POST {hub}/negotiate?negotiateVersion=1  -> connectionToken
//   2. open a WebSocket to {hub}?id={token}
//   3. send the handshake {"protocol":"json","version":1} terminated by 0x1e
//   4. every frame afterwards is one or more 0x1e-separated JSON messages
// Message types used here: 1 = invocation, 6 = ping, 7 = close.

const SEP = '';

export class SignalRClient {
  #ws = null;
  #buffer = '';
  #handlers = new Map();
  #pingTimer = null;
  #handshakeResolve = null;

  constructor(hubUrl, token = null) {
    this.hubUrl = hubUrl;
    this.token = token;
    this.closed = false;
  }

  /** Register a callback for a server-to-client method (e.g. "clapUpdate"). */
  on(method, handler) {
    const list = this.#handlers.get(method) ?? [];
    list.push(handler);
    this.#handlers.set(method, list);
  }

  async start() {
    const headers = this.token ? { Authorization: `Bearer ${this.token}` } : {};
    const res = await fetch(`${this.hubUrl}/negotiate?negotiateVersion=1`, { method: 'POST', headers });
    if (!res.ok) throw new Error(`negotiate failed: ${res.status} ${await res.text()}`);
    const negotiation = await res.json();

    const url = new URL(this.hubUrl.replace(/^http/, 'ws'));
    url.searchParams.set('id', negotiation.connectionToken ?? negotiation.connectionId);
    if (this.token) url.searchParams.set('access_token', this.token);

    await new Promise((resolve, reject) => {
      const ws = new WebSocket(url);
      this.#ws = ws;
      ws.addEventListener('message', (e) => this.#onData(String(e.data)));
      ws.addEventListener('error', () => reject(new Error('websocket error')));
      ws.addEventListener('close', () => { this.closed = true; });
      ws.addEventListener('open', () => {
        ws.send(JSON.stringify({ protocol: 'json', version: 1 }) + SEP);
        // The handshake response is an empty object; treat the first message as the ack.
        this.#handshakeResolve = resolve;
      });
      setTimeout(() => reject(new Error('handshake timed out')), 10_000);
    });

    // Keepalive so the server does not time the connection out mid-test.
    this.#pingTimer = setInterval(() => this.#send({ type: 6 }), 10_000);
  }

  /** Call a hub method (fire and forget — none of ours return a value). */
  invoke(method, ...args) {
    this.#send({ type: 1, target: method, arguments: args });
  }

  stop() {
    if (this.#pingTimer) clearInterval(this.#pingTimer);
    try { this.#ws?.close(); } catch { /* already gone */ }
  }

  #send(message) {
    if (this.#ws?.readyState === 1) this.#ws.send(JSON.stringify(message) + SEP);
  }

  #onData(chunk) {
    this.#buffer += chunk;
    // A frame can carry several messages, or split one across frames — split on the separator and
    // keep any trailing partial for the next frame.
    const parts = this.#buffer.split(SEP);
    this.#buffer = parts.pop() ?? '';

    for (const part of parts) {
      if (!part) continue;
      let message;
      try { message = JSON.parse(part); } catch { continue; }

      if (this.#handshakeResolve) {
        // First message after the handshake request is its response.
        const resolve = this.#handshakeResolve;
        this.#handshakeResolve = null;
        if (message.error) throw new Error(`handshake rejected: ${message.error}`);
        resolve();
        continue;
      }

      if (message.type === 1 && message.target) {
        for (const handler of this.#handlers.get(message.target) ?? []) {
          handler(...(message.arguments ?? []));
        }
      }
    }
  }
}
