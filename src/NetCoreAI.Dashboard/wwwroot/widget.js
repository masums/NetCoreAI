/*
 * The embeddable chat widget.
 *
 *   <script src="/netcoreai/_content/widget.js" data-agent="support"></script>
 *
 * A floating button that opens a panel and streams an agent's answers. Everything it draws is styled
 * through CSS variables on .netcoreai-widget, so a host restyles it without touching this file and
 * without this file needing a theming API.
 *
 * It deliberately carries no credential. The request goes to the host that served this script, as the
 * browser, with whatever cookie the visitor already has — which is the only arrangement where the page
 * does not contain a secret. See the guide: an API key in a page is a public API key.
 */
(() => {
  const script = document.currentScript;
  if (!script) return;

  const agent = script.dataset.agent;
  if (!agent) {
    console.error('[netcoreai] The widget script needs data-agent="<agent id>".');
    return;
  }

  // The path this script was served from, so the widget talks to the host it came from rather than to
  // one somebody has to configure twice.
  const base = new URL(script.src).pathname.replace(/\/_content\/widget\.js.*$/, '');
  const title = script.dataset.title || 'Ask a question';
  const greeting = script.dataset.greeting || 'Ask me anything.';
  const placement = script.dataset.placement === 'left' ? 'left' : 'right';

  const css = `
.netcoreai-widget{
  --ncai-accent:#512bd4; --ncai-accent-text:#fff; --ncai-bg:#fff; --ncai-text:#1f2328;
  --ncai-muted:#6b7280; --ncai-border:#e4e6eb; --ncai-bubble:#f3f4f6; --ncai-radius:12px;
  --ncai-font:system-ui,-apple-system,"Segoe UI",Roboto,sans-serif; --ncai-width:380px; --ncai-height:520px;
  position:fixed; bottom:20px; ${placement}:20px; z-index:2147483000; font-family:var(--ncai-font);
  color:var(--ncai-text); font-size:14px; line-height:1.5;
}
@media (prefers-color-scheme:dark){.netcoreai-widget{
  --ncai-bg:#171a21; --ncai-text:#e6e8ec; --ncai-muted:#9aa3b2; --ncai-border:#262b36; --ncai-bubble:#1f232c;
}}
.netcoreai-widget *{box-sizing:border-box}
.netcoreai-launch{border:0;border-radius:999px;background:var(--ncai-accent);color:var(--ncai-accent-text);
  padding:12px 20px;font:inherit;font-weight:600;cursor:pointer;box-shadow:0 6px 24px rgba(0,0,0,.18)}
.netcoreai-panel{display:none;flex-direction:column;width:var(--ncai-width);max-width:calc(100vw - 32px);
  height:var(--ncai-height);max-height:calc(100vh - 120px);background:var(--ncai-bg);
  border:1px solid var(--ncai-border);border-radius:var(--ncai-radius);overflow:hidden;
  box-shadow:0 12px 40px rgba(0,0,0,.22);margin-bottom:10px}
.netcoreai-widget[data-open="true"] .netcoreai-panel{display:flex}
.netcoreai-head{display:flex;align-items:center;justify-content:space-between;gap:8px;
  padding:12px 14px;border-bottom:1px solid var(--ncai-border);font-weight:600}
.netcoreai-close{border:0;background:none;color:var(--ncai-muted);font-size:20px;line-height:1;cursor:pointer;padding:0 4px}
.netcoreai-log{flex:1;overflow-y:auto;padding:14px;display:flex;flex-direction:column;gap:10px}
.netcoreai-msg{padding:9px 12px;border-radius:var(--ncai-radius);max-width:85%;white-space:pre-wrap;overflow-wrap:anywhere}
.netcoreai-msg[data-from="user"]{align-self:flex-end;background:var(--ncai-accent);color:var(--ncai-accent-text)}
.netcoreai-msg[data-from="agent"]{align-self:flex-start;background:var(--ncai-bubble)}
.netcoreai-msg[data-from="error"]{align-self:stretch;background:transparent;color:#b91c1c;padding:0;font-size:13px}
.netcoreai-form{display:flex;gap:8px;padding:10px;border-top:1px solid var(--ncai-border)}
.netcoreai-form textarea{flex:1;resize:none;border:1px solid var(--ncai-border);border-radius:8px;
  padding:8px 10px;font:inherit;background:var(--ncai-bg);color:var(--ncai-text)}
.netcoreai-form button{border:0;border-radius:8px;background:var(--ncai-accent);color:var(--ncai-accent-text);
  padding:0 16px;font:inherit;font-weight:600;cursor:pointer}
.netcoreai-form button[disabled]{opacity:.5;cursor:default}
@media (prefers-reduced-motion:no-preference){.netcoreai-panel{animation:netcoreai-in .16s ease-out}}
@keyframes netcoreai-in{from{opacity:0;transform:translateY(6px)}to{opacity:1;transform:none}}
`;

  const style = document.createElement('style');
  style.textContent = css;
  document.head.append(style);

  const root = document.createElement('div');
  root.className = 'netcoreai-widget';
  root.dataset.open = 'false';
  root.innerHTML = `
    <div class="netcoreai-panel" role="dialog" aria-label="${escape(title)}">
      <div class="netcoreai-head"><span>${escape(title)}</span>
        <button class="netcoreai-close" type="button" aria-label="Close">&times;</button></div>
      <div class="netcoreai-log" aria-live="polite"></div>
      <form class="netcoreai-form">
        <textarea rows="1" placeholder="Message…" aria-label="Message" required></textarea>
        <button type="submit">Send</button>
      </form>
    </div>
    <button class="netcoreai-launch" type="button" aria-expanded="false">${escape(title)}</button>`;
  document.body.append(root);

  const panel = root.querySelector('.netcoreai-panel');
  const log = root.querySelector('.netcoreai-log');
  const form = root.querySelector('.netcoreai-form');
  const input = form.querySelector('textarea');
  const send = form.querySelector('button');
  const launch = root.querySelector('.netcoreai-launch');

  let sessionId = null;
  let busy = false;

  say('agent', greeting);

  launch.addEventListener('click', () => toggle(root.dataset.open !== 'true'));
  root.querySelector('.netcoreai-close').addEventListener('click', () => toggle(false));

  // Escape closes it. A fixed panel with no keyboard way out is a trap for anybody not using a mouse.
  document.addEventListener('keydown', (ev) => {
    if (ev.key === 'Escape' && root.dataset.open === 'true') toggle(false);
  });

  input.addEventListener('keydown', (ev) => {
    if (ev.key === 'Enter' && !ev.shiftKey) { ev.preventDefault(); form.requestSubmit(); }
  });

  form.addEventListener('submit', (ev) => { ev.preventDefault(); ask(input.value.trim()); });

  function toggle(open) {
    root.dataset.open = open ? 'true' : 'false';
    launch.setAttribute('aria-expanded', open ? 'true' : 'false');
    if (open) input.focus();
  }

  function escape(text) {
    const el = document.createElement('div');
    el.textContent = text;
    return el.innerHTML;
  }

  function say(from, text) {
    const el = document.createElement('div');
    el.className = 'netcoreai-msg';
    el.dataset.from = from;

    // textContent throughout: an answer is model output, and a widget that renders it as HTML on
    // somebody else's page is a cross-site scripting hole with a friendly face.
    el.textContent = text;
    log.append(el);
    log.scrollTop = log.scrollHeight;
    return el;
  }

  async function ask(question) {
    if (!question || busy) return;

    busy = true;
    send.disabled = true;
    input.value = '';
    say('user', question);
    const answer = say('agent', '…');
    let first = true;

    try {
      const response = await fetch(`${base}/api/agents/${encodeURIComponent(agent)}/run/stream`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },

        // The visitor's existing cookie, and nothing else. No key travels in the page.
        credentials: 'same-origin',
        body: JSON.stringify({ message: question, sessionId }),
      });

      if (!response.ok) {
        answer.remove();
        say('error', response.status === 401 || response.status === 403
          ? 'You are not signed in, or not allowed to use this assistant.'
          : `That did not work (HTTP ${response.status}).`);
        return;
      }

      const reader = response.body.getReader();
      const decoder = new TextDecoder();
      let buffer = '';

      for (;;) {
        const { value, done } = await reader.read();
        if (done) break;
        buffer += decoder.decode(value, { stream: true });

        // SSE frames are separated by a blank line, and a frame can arrive split across reads.
        const frames = buffer.split('\n\n');
        buffer = frames.pop();

        for (const frame of frames) {
          const event = /^event: (.+)$/m.exec(frame)?.[1];
          const data = /^data: (.+)$/m.exec(frame)?.[1];
          if (!data) continue;

          const payload = JSON.parse(data);
          if (event === 'delta' && payload.text) {
            if (first) { answer.textContent = ''; first = false; }
            answer.textContent += payload.text;
            log.scrollTop = log.scrollHeight;
          } else if (event === 'done') {
            sessionId = payload.response?.sessionId || sessionId;
          } else if (event === 'error') {
            answer.remove();
            say('error', payload.error || 'That did not work.');
          }
        }
      }

      if (first) answer.textContent = 'No answer came back.';
    } catch {
      answer.remove();
      say('error', 'The assistant could not be reached.');
    } finally {
      busy = false;
      send.disabled = false;
      input.focus();
    }
  }
})();
