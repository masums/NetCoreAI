/* NetCoreAI dashboard: plain JS over the management API. No framework, no build step. */
(function () {
  'use strict';
  const base = document.body.dataset.base.replace(/\/$/, '');
  const api = (p) => `${base}/api/${p.replace(/^\//, '')}`;

  // ---------- helpers ----------
  const $ = (s, r = document) => r.querySelector(s);
  const $$ = (s, r = document) => Array.from(r.querySelectorAll(s));
  const toastEl = $('#toast');
  let toastTimer;
  function toast(msg, isError) {
    toastEl.textContent = msg;
    toastEl.className = 'toast' + (isError ? ' error' : '');
    toastEl.hidden = false;
    clearTimeout(toastTimer);
    toastTimer = setTimeout(() => (toastEl.hidden = true), isError ? 8000 : 3500);
  }
  async function call(method, path, body) {
    const res = await fetch(api(path), {
      method, headers: body ? { 'Content-Type': 'application/json' } : {}, body: body ? JSON.stringify(body) : undefined, credentials: 'same-origin',
    });
    if (res.status === 204) return null;
    const text = await res.text();
    let data = null;
    try { data = text ? JSON.parse(text) : null; } catch { data = text; }
    if (!res.ok) {
      const msg = (data && (data.detail || data.title || data.message)) || (typeof data === 'string' && data) || `${res.status} ${res.statusText}`;
      throw new Error(msg);
    }
    return data;
  }
  const esc = (s) => s.replace(/[&<>"]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));
  function formData(form) {
    const o = {};
    for (const el of form.elements) {
      if (!el.name) continue;
      if (el.type === 'checkbox') {
        if (el.name.endsWith('s') && form.querySelectorAll(`[name="${el.name}"]`).length > 1) {
          (o[el.name] ||= []); if (el.checked) o[el.name].push(el.value);
        } else o[el.name] = el.checked;
      } else if (el.type === 'number') o[el.name] = el.value === '' ? null : Number(el.value);
      else o[el.name] = el.value;
    }
    return o;
  }
  async function guarded(fn, btn) {
    if (btn) { btn.disabled = true; }
    try { await fn(); } catch (e) { toast(e.message || String(e), true); } finally { if (btn) btn.disabled = false; }
  }

  // ---------- models page ----------
  document.addEventListener('click', (ev) => {
    const btn = ev.target.closest('[data-action]');
    if (!btn) return;
    const { action, id, name, alias } = btn.dataset;
    const reload = () => location.reload();
    switch (action) {
      case 'load': return guarded(async () => { toast(`Loading ${id}…`); await call('POST', `models/${encodeURIComponent(id)}/load`, null); reload(); }, btn);
      case 'unload': return guarded(async () => { await call('POST', `models/${encodeURIComponent(id)}/unload`); reload(); }, btn);
      case 'delete': return confirm(`Remove "${name}" from the registry?`) && guarded(async () => { await call('DELETE', `models/${encodeURIComponent(id)}?deleteFiles=${confirm('Also delete its files from disk?')}`); reload(); }, btn);
      case 'alias': { const a = prompt('Alias name for this model (e.g. fast, quality, default):'); if (!a) return; return guarded(async () => { await call('PUT', `models/aliases/${encodeURIComponent(a)}`, { modelId: id }); reload(); }, btn); }
      case 'remove-alias': return guarded(async () => { await call('DELETE', `models/aliases/${encodeURIComponent(alias)}`); reload(); }, btn);
      case 'test-connection': return guarded(async () => { const r = await call('POST', `providers/connections/${encodeURIComponent(id)}/test`); toast(`${r.health}: ${r.message} (${r.latencyMs} ms)`, !r.success); setTimeout(reload, 1500); }, btn);
      case 'sync-models': return guarded(async () => { const r = await call('POST', `providers/connections/${encodeURIComponent(id)}/models`); toast(`Synced ${r.length} model(s) into the registry.`); setTimeout(reload, 1200); }, btn);
      case 'delete-connection': return confirm(`Remove connection "${name}" and its models?`) && guarded(async () => { await call('DELETE', `providers/connections/${encodeURIComponent(id)}`); reload(); }, btn);
      case 'hub-download': {
        const { repo, source, files } = btn.dataset;
        return guarded(async () => {
          const job = await call('POST', 'downloads', { repoId: repo, source, files: files.split('|').filter(Boolean), name: btn.dataset.name });
          toast(`Downloading ${btn.dataset.name || repo}...`);
          watchDownload(job.id);
        }, btn);
      }
      case 'download-pause': return guarded(async () => { await call('POST', `downloads/${encodeURIComponent(id)}/pause`); reload(); }, btn);
      case 'download-resume': return guarded(async () => { await call('POST', `downloads/${encodeURIComponent(id)}/resume`); watchDownload(id); }, btn);
      case 'hub-back': return location.reload();
      case 'download-cancel': return confirm('Cancel this download and discard its partial files?') && guarded(async () => { await call('DELETE', `downloads/${encodeURIComponent(id)}`); reload(); }, btn);
    }
  });

  // ---------- model hub ----------
  const fmtBytes = (n) => {
    if (n === null || n === undefined) return '-';
    const units = ['B', 'KB', 'MB', 'GB', 'TB'];
    let i = 0, b = n;
    while (b >= 1024 && i < units.length - 1) { b /= 1024; i++; }
    return `${b.toFixed(b < 10 && i > 0 ? 1 : 0)} ${units[i]}`;
  };
  const fitBadge = (fit) => {
    switch (fit && fit.verdict) {
      case 'Fits': return '<span class="status loaded">fits</span>';
      case 'Tight': return '<span class="status loading">tight</span>';
      case 'WontFit': return '<span class="status error">too big</span>';
      default: return '<span class="muted">unknown</span>';
    }
  };

  // Polls one job until it settles, so the row reflects reality without a manual refresh.
  function watchDownload(id) {
    if (!id) return;
    const tick = async () => {
      try {
        const job = await call('GET', `downloads/${encodeURIComponent(id)}`);
        const row = $(`#downloads-table tr[data-id="${id}"]`);
        if (row) {
          const bar = row.querySelector('progress');
          if (bar) { bar.value = job.bytesDone; bar.max = job.bytesTotal || 1; }
          const small = row.querySelector('td:nth-child(2) small');
          if (small) small.textContent = `${fmtBytes(job.bytesDone)} of ${fmtBytes(job.bytesTotal)}${job.bytesPerSecond ? ` - ${fmtBytes(job.bytesPerSecond)}/s` : ''}`;
        }
        if (['Completed', 'Failed', 'Cancelled', 'Paused'].includes(job.state)) {
          if (job.state === 'Completed') toast(`${job.request.modelName || job.request.repoId} is ready.`);
          if (job.state === 'Failed') toast(job.error || 'Download failed.', true);
          return location.reload();
        }
        setTimeout(tick, 1000);
      } catch (e) {
        toast(e.message || String(e), true);
      }
    };
    setTimeout(tick, 600);
  }

  const hubSearch = $('#hub-search');
  hubSearch?.addEventListener('submit', (ev) => {
    ev.preventDefault();
    const d = formData(hubSearch);
    const results = $('#hub-results');
    results.innerHTML = '<p class="muted">Searching...</p>';
    guarded(async () => {
      const params = new URLSearchParams();
      for (const [k, v] of Object.entries({ q: d.q, format: d.format, task: d.task, sort: d.sort })) if (v) params.set(k, v);
      const rows = await call('GET', `hub/search?${params}`);
      if (!rows.length) { results.innerHTML = '<p class="empty">No models matched. Try fewer filters.</p>'; return; }
      results.innerHTML = `<table><thead><tr><th>Model</th><th>Formats</th><th>Downloads</th><th></th></tr></thead><tbody>${rows.map((r) => `
        <tr><td><strong>${esc(r.name)}</strong><br /><span class="muted mono">${esc(r.repoId)}</span>${r.gated ? ' <span class="tag">gated</span>' : ''}</td>
        <td>${r.formats.join(', ') || '-'}</td><td>${r.downloads.toLocaleString()}</td>
        <td class="actions"><button class="btn small" data-action="hub-open" data-repo="${esc(r.repoId)}" data-source="${esc(r.sourceId)}">Variants...</button></td></tr>`).join('')}</tbody></table>`;
    });
  });

  // Expands a repository into the variants that can actually be downloaded, each with a fit verdict.
  document.addEventListener('click', (ev) => {
    const btn = ev.target.closest('[data-action="hub-open"]');
    if (!btn) return;
    const { repo, source } = btn.dataset;
    guarded(async () => {
      const view = await call('GET', `hub/models/${repo.split('/').map(encodeURIComponent).join('/')}?source=${encodeURIComponent(source)}`);
      const variants = view.variants || [];
      const target = $('#hub-results');
      target.innerHTML = `<h3>${esc(view.detail.summary.repoId)}</h3>
        <p class="muted">${esc(view.detail.summary.license || 'licence not stated')} - ${view.detail.summary.downloads.toLocaleString()} downloads</p>
        ${variants.length ? `<table><thead><tr><th>Variant</th><th>Size</th><th>Fits</th><th></th></tr></thead><tbody>${variants.map((v) => `
          <tr><td><strong>${esc(v.name)}</strong>${v.quantization ? ` <span class="tag">${esc(v.quantization)}</span>` : ''}<br /><span class="muted">${v.format} - ${v.files.length} file(s)</span></td>
          <td>${fmtBytes(v.sizeBytes)}</td><td>${fitBadge(v.fit)}</td>
          <td class="actions"><button class="btn small" data-action="hub-download" data-repo="${esc(view.detail.summary.repoId)}" data-source="${esc(source)}" data-files="${esc(v.files.join('|'))}" data-name="${esc(view.detail.summary.name)}">Download</button></td></tr>`).join('')}</tbody></table>`
          : '<p class="empty">This repository has no GGUF or ONNX files NetCoreAI can run. It may need converting first.</p>'}
        <button class="btn small" data-action="hub-back">Back to search</button>`;
    }, btn);
  });

  const importForm = $('#import-form');
  importForm?.addEventListener('submit', (ev) => {
    ev.preventDefault();
    const d = formData(importForm);
    if (!d.path && !d.url) { toast('Give a path on the server or a URL.', true); return; }
    guarded(async () => {
      const r = await call('POST', 'models/import', { path: d.path || null, url: d.url || null, name: d.name || null, copy: !!d.copy });
      if (r.job) { toast('Download queued.'); watchDownload(r.job.id); } else { toast(`Imported ${r.model.name}.`); setTimeout(() => location.reload(), 900); }
    });
  });

  // Any download still moving when the page loads keeps its row live.
  $$('#downloads-table tr[data-id]').forEach((row) => {
    if (row.querySelector('.status.downloading, .status.queued')) watchDownload(row.dataset.id);
  });

  const aliasForm = $('#alias-form');
  aliasForm?.addEventListener('submit', (ev) => {
    ev.preventDefault();
    const d = formData(aliasForm);
    guarded(async () => {
      await call('PUT', `models/aliases/${encodeURIComponent(d.alias)}`, { modelId: d.modelId, fallbackModelIds: d.fallbacks ? d.fallbacks.split(',').map((s) => s.trim()).filter(Boolean) : null });
      location.reload();
    });
  });

  // ---------- providers page ----------
  const connForm = $('#connection-form');
  if (connForm) {
    const presets = JSON.parse($('#presets-json').textContent || '{}');
    const providerSel = connForm.elements.providerId, presetSel = connForm.elements.preset, baseUrl = connForm.elements.baseUrl, secret = connForm.elements.secret;
    function fillPresets() {
      const list = presets[providerSel.value] || [];
      presetSel.innerHTML = list.map((p) => `<option value="${esc(p.id)}">${esc(p.displayName)}</option>`).join('');
      applyPreset();
    }
    function applyPreset() {
      const p = (presets[providerSel.value] || []).find((x) => x.id === presetSel.value);
      if (!p) return;
      baseUrl.placeholder = p.defaultBaseUrl || 'https://…/v1';
      baseUrl.value = p.defaultBaseUrl || '';
      secret.required = !!p.requiresApiKey;
      secret.placeholder = p.requiresApiKey ? 'required' : 'optional';
      $$('[data-setting]', connForm).forEach((el) => { el.hidden = !(p.requiredSettings || []).includes(el.dataset.setting); });
      if (!connForm.elements.name.value) connForm.elements.name.value = p.displayName;
    }
    providerSel.addEventListener('change', fillPresets);
    presetSel.addEventListener('change', applyPreset);
    fillPresets();
    connForm.addEventListener('submit', (ev) => {
      ev.preventDefault();
      const d = formData(connForm);
      const settings = {};
      for (const k of Object.keys(d)) if (k.startsWith('setting:') && d[k]) settings[k.slice(8)] = d[k];
      guarded(async () => {
        const c = await call('POST', 'providers/connections', { name: d.name, providerId: d.providerId, preset: d.preset, baseUrl: d.baseUrl || null, secret: d.secret || null, settings });
        toast(`Saved "${c.name}". Testing…`);
        const t = await call('POST', `providers/connections/${encodeURIComponent(c.id)}/test`);
        if (t.success) { await call('POST', `providers/connections/${encodeURIComponent(c.id)}/models`); }
        toast(t.success ? `${t.message} Models synced.` : `${t.health}: ${t.message}`, !t.success);
        setTimeout(() => location.reload(), 1500);
      }, connForm.querySelector('button[type=submit]'));
    });
  }

  // ---------- settings page ----------
  const settingsForm = $('#settings-form');
  settingsForm?.addEventListener('submit', (ev) => {
    ev.preventDefault();
    const d = formData(settingsForm);
    const body = {
      dashboardTitle: d.dashboardTitle, defaultContextSize: d.defaultContextSize,
      idleUnloadMinutes: d.idleUnloadMinutes || 0, clearIdleUnload: !d.idleUnloadMinutes,
      memoryBudgetBytes: d.memoryBudgetBytes || 0, storageQuotaWarningBytes: d.storageQuotaWarningBytes || 0,
      telemetryEnabled: !!d.telemetryEnabled, executionProvider: d.executionProvider, threads: d.threads || 0, defaultConcurrency: d.defaultConcurrency,
      offlineMode: !!d.offlineMode, huggingFaceEndpoint: d.huggingFaceEndpoint, proxyUrl: d.proxyUrl || '', bandwidthLimitBytesPerSecond: d.bandwidthLimitBytesPerSecond || 0,
      remoteProvidersEnabled: !!d.remoteProvidersEnabled, disabledProviders: d.disabledProviders || [],
    };
    if (d.huggingFaceToken) body.huggingFaceToken = d.huggingFaceToken.trim().toLowerCase() === 'clear' ? '' : d.huggingFaceToken.trim();
    guarded(async () => { await call('PUT', 'settings', body); toast('Settings saved.'); settingsForm.elements.huggingFaceToken.value = ''; setTimeout(() => location.reload(), 800); }, settingsForm.querySelector('button[type=submit]'));
  });

  // ---------- chat page ----------
  const chatForm = $('#chat-form');
  if (chatForm) {
    const messagesEl = $('#messages'), textEl = $('#chat-text'), modelSel = $('#chat-model'), sendBtn = $('#send'), stopBtn = $('#stop'), params = $('#params');
    let sessionId = new URLSearchParams(location.search).get('session');
    let abort = null;
    const sessionButtons = ['#export-md', '#export-json', '#delete-session'].map((s) => $(s));
    function setSession(id) {
      sessionId = id;
      sessionButtons.forEach((b) => (b.disabled = !id));
      $$('#session-list li').forEach((li) => li.classList.toggle('active', li.dataset.id === id));
    }
    function render(md) {
      // minimal markdown: fenced code, inline code, bold, links; everything else escaped
      let html = esc(md);
      html = html.replace(/```(\w*)\n([\s\S]*?)```/g, (_, l, c) => `<pre><code class="lang-${l}">${c}</code></pre>`);
      html = html.replace(/`([^`\n]+)`/g, '<code>$1</code>');
      html = html.replace(/\*\*([^*]+)\*\*/g, '<strong>$1</strong>');
      html = html.replace(/\[([^\]]+)\]\((https?:[^)]+)\)/g, '<a href="$2" target="_blank" rel="noopener">$1</a>');
      return html;
    }
    function addMessage(role, content, meta) {
      if (messagesEl.querySelector('.empty')) messagesEl.innerHTML = '';
      const div = document.createElement('div');
      div.className = `msg ${role}`;
      div.innerHTML = `<div class="bubble">${render(content)}</div><div class="meta"></div>`;
      updateMeta(div, meta || {});
      messagesEl.appendChild(div);
      messagesEl.scrollTop = messagesEl.scrollHeight;
      return div;
    }
    function updateMeta(div, m) {
      const parts = [];
      if (m.latencyMs != null) parts.push(`${(m.latencyMs / 1000).toFixed(1)} s`);
      if (m.outputTokens != null) parts.push(`${m.outputTokens} tok${m.latencyMs ? ` · ${(m.outputTokens / (m.latencyMs / 1000)).toFixed(1)} tok/s` : ''}`);
      if (m.inputTokens != null) parts.push(`${m.inputTokens} in`);
      if (m.estimatedCost != null) parts.push(`$${m.estimatedCost}`);
      const meta = div.querySelector('.meta');
      meta.innerHTML = parts.map((p) => `<span>${esc(p)}</span>`).join('') +
        `<button data-copy title="Copy">copy</button>` +
        (div.classList.contains('user') && m.id ? `<button data-edit="${esc(m.id)}" title="Edit and resend">edit</button>` : '') +
        (div.classList.contains('assistant') && m.id ? `<button data-regen="${esc(m.id)}" title="Regenerate">regenerate</button>` : '');
      div.dataset.id = m.id || '';
      div.dataset.raw = m.raw ?? div.dataset.raw ?? '';
    }
    messagesEl.addEventListener('click', (ev) => {
      const b = ev.target.closest('button'); if (!b) return;
      const div = b.closest('.msg');
      if (b.hasAttribute('data-copy')) { navigator.clipboard.writeText(div.dataset.raw || div.querySelector('.bubble').innerText); toast('Copied.'); }
      if (b.hasAttribute('data-edit')) { textEl.value = div.dataset.raw; textEl.dataset.replace = b.dataset.edit; textEl.focus(); toast('Editing: sending will replace the conversation from this message.'); }
      if (b.hasAttribute('data-regen')) { const prev = div.previousElementSibling; if (prev && prev.classList.contains('user')) { send(prev.dataset.raw, prev.dataset.id); } }
    });
    async function loadSession(id) {
      const r = await call('GET', `sessions/${encodeURIComponent(id)}`);
      messagesEl.innerHTML = '';
      for (const m of r.messages) addMessage(m.role, m.content, { ...m, raw: m.content });
      if (r.session.modelId) modelSel.value = r.session.modelId;
      const p = r.session.parameters || {};
      for (const el of params.querySelectorAll('[name]')) el.value = p[el.name] ?? '';
      setSession(id);
    }
    function currentParams() {
      const p = {};
      for (const el of params.querySelectorAll('[name]')) { if (el.value !== '') p[el.name] = el.type === 'number' ? Number(el.value) : el.value; }
      return p;
    }
    async function send(text, replaceFromMessageId) {
      if (!text.trim()) return;
      if (!modelSel.value) { toast('Register a model first (Providers → Sync models, or Model Hub).', true); return; }
      if (replaceFromMessageId) { let n = messagesEl.querySelector(`.msg[data-id="${replaceFromMessageId}"]`); while (n) { const next = n.nextElementSibling; n.remove(); n = next; } }
      const userDiv = addMessage('user', text, { raw: text });
      const botDiv = addMessage('assistant', '', {});
      botDiv.querySelector('.bubble').classList.add('cursor');
      sendBtn.disabled = true; stopBtn.hidden = false; textEl.value = ''; delete textEl.dataset.replace;
      abort = new AbortController();
      let acc = '';
      try {
        const res = await fetch(api('chat'), {
          method: 'POST', headers: { 'Content-Type': 'application/json' }, credentials: 'same-origin', signal: abort.signal,
          body: JSON.stringify({ sessionId, model: modelSel.value, message: text, parameters: currentParams(), replaceFromMessageId: replaceFromMessageId || null }),
        });
        if (!res.ok) throw new Error(`${res.status} ${await res.text()}`);
        const reader = res.body.getReader(); const dec = new TextDecoder(); let buf = '';
        while (true) {
          const { value, done } = await reader.read(); if (done) break;
          buf += dec.decode(value, { stream: true });
          let idx;
          while ((idx = buf.indexOf('\n\n')) >= 0) {
            const chunk = buf.slice(0, idx); buf = buf.slice(idx + 2);
            const ev = /^event: (\w+)/m.exec(chunk)?.[1]; const dataLine = chunk.split('\n').find((l) => l.startsWith('data: '));
            if (!ev || !dataLine) continue;
            const data = JSON.parse(dataLine.slice(6));
            if (ev === 'session') { if (!sessionId) { setSession(data.session.id); history.replaceState(null, '', `?session=${data.session.id}`); const li = document.createElement('li'); li.dataset.id = data.session.id; li.className = 'active'; li.innerHTML = `<a href="#" data-session="${data.session.id}">${esc(data.session.title)}</a><small class="muted">just now</small>`; $('#session-list').prepend(li); } }
            else if (ev === 'delta') { acc += data.text; botDiv.querySelector('.bubble').innerHTML = render(acc); messagesEl.scrollTop = messagesEl.scrollHeight; }
            else if (ev === 'done') { updateMeta(botDiv, { ...data.message, raw: data.message.content }); if (data.error) toast(`Stopped early: ${data.error}`, true); }
            else if (ev === 'error') { botDiv.querySelector('.bubble').innerHTML = `<span class="muted">Error: ${esc(data.error || 'unknown')}</span>`; toast(data.error, true); }
          }
        }
      } catch (e) {
        if (e.name !== 'AbortError') { toast(e.message, true); botDiv.querySelector('.bubble').innerHTML = `<span class="muted">Error: ${esc(e.message)}</span>`; }
      } finally {
        botDiv.querySelector('.bubble').classList.remove('cursor');
        sendBtn.disabled = false; stopBtn.hidden = true; abort = null; textEl.focus();
      }
    }
    chatForm.addEventListener('submit', (ev) => { ev.preventDefault(); send(textEl.value, textEl.dataset.replace); });
    textEl.addEventListener('keydown', (ev) => { if (ev.key === 'Enter' && !ev.shiftKey) { ev.preventDefault(); chatForm.requestSubmit(); } });
    stopBtn.addEventListener('click', () => abort?.abort());
    $('#toggle-params').addEventListener('click', () => (params.hidden = !params.hidden));
    $('#new-chat').addEventListener('click', () => { setSession(null); history.replaceState(null, '', location.pathname); messagesEl.innerHTML = '<p class="empty">New chat.</p>'; });
    $('#session-list').addEventListener('click', (ev) => { const a = ev.target.closest('[data-session]'); if (!a) return; ev.preventDefault(); guarded(() => loadSession(a.dataset.session)); });
    $('#delete-session').addEventListener('click', () => sessionId && confirm('Delete this chat?') && guarded(async () => { await call('DELETE', `sessions/${sessionId}`); location.href = location.pathname; }));
    $('#export-md').addEventListener('click', () => sessionId && window.open(api(`sessions/${sessionId}/export?format=markdown`)));
    $('#export-json').addEventListener('click', () => sessionId && window.open(api(`sessions/${sessionId}/export?format=json`)));
    if (sessionId) guarded(() => loadSession(sessionId));
    setSession(sessionId);
  }
})();
