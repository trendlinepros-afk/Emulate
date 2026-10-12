using System.Net;

namespace Emulate.Services;

/// <summary>The pages served by Web access (self-contained, no external files).</summary>
internal static class WebPage
{
    private const string Head = """
        <!doctype html><html lang="en"><head><meta charset="utf-8">
        <meta name="viewport" content="width=device-width,initial-scale=1,maximum-scale=1,user-scalable=no,viewport-fit=cover">
        <meta name="theme-color" content="#0f0f11"><meta name="apple-mobile-web-app-capable" content="yes">
        <style>
        :root{--bg:#0f0f11;--surface:#16161a;--card:#1c1c20;--line:#2a2a30;--text:#ececee;--muted:#8b8b94;--good:#5fd3a0;--bad:#f07a85}
        *{box-sizing:border-box;-webkit-tap-highlight-color:transparent}
        html,body{margin:0;height:100%;background:var(--bg);color:var(--text);font:14px/1.4 system-ui,-apple-system,"Segoe UI",Roboto,sans-serif;overflow:hidden}
        button{font:inherit;color:inherit;background:none;border:0;cursor:pointer}
        </style>
        """;

    public static string Login(string name, bool wrongCode) => Head + $$"""
        <title>{{WebUtility.HtmlEncode(name)}} · Emulate</title>
        <style>
        body{display:flex;align-items:center;justify-content:center;padding:24px}
        form{width:100%;max-width:340px;background:var(--surface);border:1px solid var(--line);border-radius:16px;padding:28px}
        h1{font-size:18px;margin:0 0 4px} p{color:var(--muted);margin:0 0 18px}
        input{width:100%;font:600 24px/1 ui-monospace,monospace;letter-spacing:.3em;text-align:center;padding:14px;border-radius:10px;border:1px solid var(--line);background:var(--card);color:var(--text);outline:none}
        input:focus{border-color:#8b9cff}
        .go{width:100%;margin-top:12px;padding:12px;border-radius:10px;background:var(--text);color:var(--bg);font-weight:600}
        .err{color:var(--bad);margin:10px 0 0;font-size:13px}
        </style></head><body>
        <form method="get" action="/">
          <h1>{{WebUtility.HtmlEncode(name)}}</h1>
          <p>Enter the access code shown in Emulate on the PC.</p>
          <input name="code" inputmode="numeric" autocomplete="one-time-code" maxlength="6" autofocus placeholder="000000">
          <button class="go" type="submit">Connect</button>
          {{(wrongCode ? "<p class=\"err\">That code didn't match. Check Emulate › Web access.</p>" : "")}}
        </form></body></html>
        """;

    public static string Viewer(string name) => Head + $$"""
        <title>{{WebUtility.HtmlEncode(name)}} · Emulate</title>
        <style>
        body{display:flex;flex-direction:column;height:100dvh}
        header{display:flex;align-items:center;gap:10px;padding:10px 14px;padding-top:max(10px,env(safe-area-inset-top));border-bottom:1px solid var(--line);background:var(--surface)}
        header .name{font-weight:600;flex:1;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
        .dot{width:8px;height:8px;border-radius:50%;background:var(--muted)} .dot.on{background:var(--good)}
        #status{color:var(--muted);font-size:12px}
        #wrap{flex:1;min-height:0;display:flex;align-items:center;justify-content:center;padding:10px;position:relative}
        canvas{max-width:100%;max-height:100%;border-radius:14px;background:#000;touch-action:none;box-shadow:0 0 0 1px var(--line);cursor:pointer}
        #msg{position:absolute;color:var(--muted);text-align:center;padding:20px}
        nav{display:flex;justify-content:center;gap:2px;padding:8px;padding-bottom:max(8px,env(safe-area-inset-bottom));border-top:1px solid var(--line);background:var(--surface)}
        nav button{flex:1 1 0;min-width:0;max-width:48px;height:40px;outline:none;border-radius:10px;display:flex;align-items:center;justify-content:center;color:var(--muted)}
        nav button:hover,nav button:active{background:var(--card);color:var(--text)}
        nav .sep{width:1px;background:var(--line);margin:8px 6px}
        svg{width:20px;height:20px;fill:none;stroke:currentColor;stroke-width:2;stroke-linecap:round;stroke-linejoin:round}
        #kb{position:fixed;left:-1000px;top:0;opacity:0;width:10px;height:10px}
        </style></head><body>
        <header><span class="dot" id="dot"></span><span class="name">{{WebUtility.HtmlEncode(name)}}</span><span id="status">Connecting…</span></header>
        <div id="wrap"><canvas id="screen" width="360" height="780"></canvas><div id="msg">Connecting to the device…</div></div>
        <nav>
          <button data-key="4" title="Back"><svg viewBox="0 0 24 24"><path d="M15 6l-6 6 6 6"/></svg></button>
          <button data-key="3" title="Home"><svg viewBox="0 0 24 24"><circle cx="12" cy="12" r="7"/></svg></button>
          <button data-key="187" title="Recent apps"><svg viewBox="0 0 24 24"><rect x="6" y="6" width="12" height="12" rx="2"/></svg></button>
          <span class="sep"></span>
          <button id="kbBtn" title="Keyboard"><svg viewBox="0 0 24 24"><rect x="3" y="6" width="18" height="12" rx="2"/><path d="M7 10h.01M11 10h.01M15 10h.01M7 14h10"/></svg></button>
          <button data-key="25" title="Volume down"><svg viewBox="0 0 24 24"><path d="M11 5L6 9H3v6h3l5 4z"/><path d="M16 12h4"/></svg></button>
          <button data-key="24" title="Volume up"><svg viewBox="0 0 24 24"><path d="M11 5L6 9H3v6h3l5 4z"/><path d="M16 12h4M18 10v4"/></svg></button>
          <button id="rotBtn" title="Rotate"><svg viewBox="0 0 24 24"><path d="M3 12a9 9 0 1 0 3-6.7L3 8"/><path d="M3 3v5h5"/></svg></button>
          <button data-key="26" title="Power"><svg viewBox="0 0 24 24"><path d="M12 3v9"/><path d="M6.3 7.3a8 8 0 1 0 11.4 0"/></svg></button>
        </nav>
        <input id="kb" autocomplete="off" autocorrect="off" autocapitalize="off" spellcheck="false">
        <script>
        (() => {
        const canvas = document.getElementById('screen'), ctx = canvas.getContext('2d');
        const msg = document.getElementById('msg'), statusEl = document.getElementById('status'), dot = document.getElementById('dot');
        const code = new URLSearchParams(location.search).get('code') || '';
        let useVideo = 'VideoDecoder' in window && 'EncodedVideoChunk' in window, decodeErrors = 0;
        let ws = null, decoder = null, codecStr = '', sps = null, pps = null, waitingKey = true, ts = 0;
        let buf = new Uint8Array(0), au = [], auKey = false, flushTimer = 0, shotTimer = 0, gotFrame = false;

        function setStatus(text, on) { statusEl.textContent = text; dot.classList.toggle('on', !!on); }
        function hex(n) { return n.toString(16).padStart(2, '0'); }
        function concat(a, b) { const c = new Uint8Array(a.length + b.length); c.set(a); c.set(b, a.length); return c; }
        function draw(src, w, h) {
          if (canvas.width !== w || canvas.height !== h) { canvas.width = w; canvas.height = h; }
          ctx.drawImage(src, 0, 0, w, h);
          if (!gotFrame) { gotFrame = true; msg.style.display = 'none'; }
        }

        // ---- H.264 (Annex B) -> WebCodecs ----
        function resetDecoder() {
          try { decoder && decoder.state !== 'closed' && decoder.close(); } catch (e) {}
          decoder = null; codecStr = ''; sps = pps = null; waitingKey = true; buf = new Uint8Array(0); au = []; auKey = false;
        }
        function configure() {
          const c = 'avc1.' + hex(sps[1]) + hex(sps[2]) + hex(sps[3]);
          if (decoder && decoder.state === 'configured' && c === codecStr) return;
          try { decoder && decoder.state !== 'closed' && decoder.close(); } catch (e) {}
          decoder = new VideoDecoder({
            output: f => { draw(f, f.displayWidth, f.displayHeight); f.close(); },
            error: e => {
              console.warn(e); resetDecoder();
              // This browser can't decode the stream: switch to screenshot mode instead of retrying forever.
              if (++decodeErrors >= 2) { useVideo = false; shotLoop(); }
              reconnect();
            }
          });
          decoder.configure({ codec: c, optimizeForLatency: true });
          codecStr = c; waitingKey = true;
        }
        const SC = new Uint8Array([0, 0, 0, 1]);
        function flushAU() {
          if (!au.length) return;
          const nals = au, key = auKey; au = []; auKey = false;
          if (!decoder || decoder.state !== 'configured') return;
          if (waitingKey && !key) return;
          let parts = [];
          if (key && sps && pps) parts.push(SC, sps, SC, pps);
          for (const n of nals) parts.push(SC, n);
          let len = 0; for (const p of parts) len += p.length;
          const data = new Uint8Array(len); let o = 0; for (const p of parts) { data.set(p, o); o += p.length; }
          if (decoder.decodeQueueSize > 30 && !key) return; // falling behind: wait for next keyframe
          decoder.decode(new EncodedVideoChunk({ type: key ? 'key' : 'delta', timestamp: (ts += 16666), data }));
          if (key) waitingKey = false;
        }
        function nal(n) {
          if (!n.length) return;
          const type = n[0] & 31;
          if (type === 7) { flushAU(); sps = n.slice(); configure(); }
          else if (type === 8) { flushAU(); pps = n.slice(); }
          else if (type === 9) { flushAU(); }
          else if (type === 1 || type === 5) {
            if (n.length > 1 && (n[1] & 0x80)) flushAU(); // first_mb_in_slice == 0 starts a new frame
            au.push(n.slice()); if (type === 5) auKey = true;
          }
        }
        function feed(data) {
          buf = buf.length ? concat(buf, data) : data;
          const starts = [];
          for (let i = 0; i + 3 < buf.length; i++) {
            if (buf[i] === 0 && buf[i + 1] === 0) {
              if (buf[i + 2] === 1) { starts.push([i, 3]); i += 2; }
              else if (buf[i + 2] === 0 && buf[i + 3] === 1) { starts.push([i, 4]); i += 3; }
            }
          }
          if (starts.length >= 2) {
            for (let k = 0; k < starts.length - 1; k++) nal(buf.subarray(starts[k][0] + starts[k][1], starts[k + 1][0]));
            buf = buf.slice(starts[starts.length - 1][0]);
          }
          clearTimeout(flushTimer);
          // The device writes whole frames at once: if nothing more arrives shortly, the pending NAL is complete.
          flushTimer = setTimeout(() => {
            if (buf.length > 4 && buf[0] === 0 && buf[1] === 0) {
              const l = buf[2] === 1 ? 3 : 4; nal(buf.subarray(l)); buf = new Uint8Array(0);
            }
            flushAU();
          }, 20);
        }

        // ---- Screenshot fallback (browsers without WebCodecs) ----
        async function shotLoop() {
          clearTimeout(shotTimer);
          try {
            const r = await fetch('/shot?code=' + encodeURIComponent(code) + '&t=' + Date.now(), { cache: 'no-store' });
            if (r.ok) { const b = await createImageBitmap(await r.blob()); draw(b, b.width, b.height); b.close && b.close(); }
          } catch (e) {}
          shotTimer = setTimeout(shotLoop, 250);
        }

        // ---- Connection ----
        let retry = 0;
        function connect() {
          resetDecoder();
          const url = (location.protocol === 'https:' ? 'wss://' : 'ws://') + location.host + '/ws?video=' + (useVideo ? 1 : 0) + '&code=' + encodeURIComponent(code);
          ws = new WebSocket(url); ws.binaryType = 'arraybuffer';
          ws.onopen = () => { retry = 0; setStatus(useVideo ? 'Live' : 'Live (basic mode)', true); };
          ws.onmessage = e => { if (typeof e.data !== 'string' && useVideo) feed(new Uint8Array(e.data)); };
          ws.onclose = () => { setStatus('Reconnecting…', false); reconnect(); };
          ws.onerror = () => {};
        }
        let reconnectTimer = 0;
        function reconnect() {
          clearTimeout(reconnectTimer);
          try { ws && ws.readyState === 1 && ws.close(); } catch (e) {}
          reconnectTimer = setTimeout(connect, Math.min(5000, 800 + 400 * retry++));
        }
        function send(o) { if (ws && ws.readyState === 1) ws.send(JSON.stringify(o)); }

        // ---- Touch / mouse ----
        let down = null;
        function norm(e) {
          const r = canvas.getBoundingClientRect();
          return { x: Math.min(1, Math.max(0, (e.clientX - r.left) / r.width)), y: Math.min(1, Math.max(0, (e.clientY - r.top) / r.height)) };
        }
        canvas.addEventListener('pointerdown', e => { e.preventDefault(); canvas.setPointerCapture(e.pointerId); down = { ...norm(e), t: performance.now(), px: e.clientX, py: e.clientY }; });
        canvas.addEventListener('pointerup', e => {
          if (!down) return; e.preventDefault();
          const p = norm(e), dt = performance.now() - down.t, dist = Math.hypot(e.clientX - down.px, e.clientY - down.py);
          if (dist < 10) {
            if (dt > 450) send({ t: 'swipe', x1: down.x, y1: down.y, x2: down.x, y2: down.y, ms: Math.round(dt) }); // long press
            else send({ t: 'tap', x: p.x, y: p.y });
          } else {
            send({ t: 'swipe', x1: down.x, y1: down.y, x2: p.x, y2: p.y, ms: Math.round(Math.min(2000, Math.max(80, dt))) });
          }
          down = null;
        });
        canvas.addEventListener('pointercancel', () => down = null);
        canvas.addEventListener('contextmenu', e => e.preventDefault());
        canvas.addEventListener('wheel', e => {
          e.preventDefault(); const p = norm(e), d = Math.max(-0.3, Math.min(0.3, -e.deltaY / 1500));
          send({ t: 'swipe', x1: p.x, y1: p.y, x2: p.x, y2: Math.min(1, Math.max(0, p.y + d)), ms: 120 });
        }, { passive: false });

        // ---- Buttons ----
        document.querySelectorAll('nav [data-key]').forEach(b => b.addEventListener('click', () => send({ t: 'key', code: +b.dataset.key })));
        document.getElementById('rotBtn').addEventListener('click', () => send({ t: 'rotate' }));

        // ---- Keyboard (physical keyboard, or the on-screen keyboard via a hidden input) ----
        const kb = document.getElementById('kb');
        document.getElementById('kbBtn').addEventListener('click', () => { kb.value = ''; kb.focus(); });
        let textBuf = '', textTimer = 0;
        function queueText(s) { textBuf += s; clearTimeout(textTimer); textTimer = setTimeout(() => { if (textBuf) send({ t: 'text', s: textBuf }); textBuf = ''; }, 60); }
        function flushText() { clearTimeout(textTimer); if (textBuf) send({ t: 'text', s: textBuf }); textBuf = ''; }
        const keyMap = { Enter: 66, Backspace: 67, Delete: 112, Tab: 61, Escape: 4, ArrowUp: 19, ArrowDown: 20, ArrowLeft: 21, ArrowRight: 22, Home: 122, End: 123 };
        document.addEventListener('keydown', e => {
          if (e.ctrlKey || e.metaKey || e.altKey) return;
          if (keyMap[e.key]) { e.preventDefault(); flushText(); send({ t: 'key', code: keyMap[e.key] }); return; }
          if (e.target !== kb && e.key.length === 1) { e.preventDefault(); queueText(e.key); }
        });
        kb.addEventListener('beforeinput', e => {
          if (e.inputType === 'insertText' && e.data) queueText(e.data);
          else if (e.inputType === 'insertLineBreak') { flushText(); send({ t: 'key', code: 66 }); }
          else if (e.inputType === 'deleteContentBackward') { flushText(); send({ t: 'key', code: 67 }); }
          e.preventDefault();
        });

        (async () => {
          if (useVideo) {
            try { useVideo = !!(await VideoDecoder.isConfigSupported({ codec: 'avc1.42E01F', optimizeForLatency: true })).supported; }
            catch (e) { useVideo = false; }
          }
          if (!useVideo) shotLoop();
          connect();
        })();
        })();
        </script></body></html>
        """;
}
