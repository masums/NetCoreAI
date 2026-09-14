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
  // The message the server wrote, not the ProblemDetails envelope it came in.
  function problem(text, res) {
    try {
      const data = text ? JSON.parse(text) : null;
      return (data && (data.detail || data.title || data.message)) || text || `${res.status} ${res.statusText}`;
    } catch {
      return text || `${res.status} ${res.statusText}`;
    }
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
          setTimeout(() => location.reload(), 500);
        }, btn);
      }
      case 'download-pause': return guarded(async () => { await call('POST', `downloads/${encodeURIComponent(id)}/pause`); reload(); }, btn);
      case 'download-resume': return guarded(async () => { await call('POST', `downloads/${encodeURIComponent(id)}/resume`); watchDownloads(); }, btn);
      case 'hub-back': return location.reload();
      case 'kb-sync': return guarded(async () => { const job = await call('POST', `kb/${encodeURIComponent(id)}/ingest`); toast('Ingestion queued.'); watchJob(job.id); }, btn);
      case 'kb-delete': return confirm(`Delete "${name}" and everything indexed in it?`) && guarded(async () => { await call('DELETE', `kb/${encodeURIComponent(id)}`); reload(); }, btn);
      case 'kb-open': return guarded(() => openKnowledgeBase(id), btn);
      case 'kb-close': { const d = $('#kb-detail'); if (d) d.hidden = true; return; }
      case 'kb-delete-source': return confirm('Remove this source and the documents it brought in?') && guarded(async () => { await call('DELETE', `kb/${encodeURIComponent(btn.dataset.kb)}/sources/${encodeURIComponent(id)}`); reload(); }, btn);
      case 'kb-delete-document': return guarded(async () => { await call('DELETE', `kb/${encodeURIComponent(btn.dataset.kb)}/documents/${encodeURIComponent(id)}`); openKnowledgeBase(btn.dataset.kb); }, btn);
      case 'agent-edit': return guarded(() => openAgent(id), btn);
      case 'agent-try': return openAgentPlayground(id, name);
      case 'agent-runs': return guarded(() => openAgentRuns(id, name), btn);
      case 'agent-close': { const a = $('#agent-detail'); if (a) a.hidden = true; return; }
      case 'agent-delete': return confirm(`Delete the agent "${name}"? Its run history goes too.`) && guarded(async () => { await call('DELETE', `agents/${encodeURIComponent(id)}`); reload(); }, btn);
      case 'run-open': return guarded(() => openRun(id), btn);
      case 'tool-edit': return guarded(() => openTool(id), btn);
      case 'tool-test': return guarded(() => openToolTest(id, name), btn);
      case 'tool-close': { const t = $('#tool-detail'); if (t) t.hidden = true; return; }
      case 'tool-delete': return confirm(`Delete the tool "${name}"? Agents naming it will stop finding it.`) && guarded(async () => { await call('DELETE', `tools/${encodeURIComponent(id)}`); reload(); }, btn);
      case 'tool-from-endpoint': return guarded(async () => { const t = await call('POST', `tools/from-endpoint/${encodeURIComponent(id)}`, null); toast(`Created ${t.name}.`); setTimeout(reload, 700); }, btn);
      case 'job-cancel': return guarded(async () => { await call('POST', `jobs/${encodeURIComponent(id)}/cancel`); reload(); }, btn);
      case 'job-retry': return guarded(async () => { await call('POST', `jobs/${encodeURIComponent(id)}/retry`); reload(); }, btn);
      case 'edit-model': return guarded(() => openModelEditor(id), btn);
      case 'close-editor': { const ed = $('#model-editor'); if (ed) ed.hidden = true; return; }
      case 'scan-orphans': return location.reload();
      case 'delete-orphans': {
        const paths = $$('.orphan:checked').map((c) => c.value);
        if (!paths.length) { toast('Select the files to delete first.', true); return; }
        return confirm(`Delete ${paths.length} file(s)? This cannot be undone.`) && guarded(async () => {
          const r = await call('POST', 'storage/orphans/delete', { paths });
          toast(`Reclaimed ${fmtBytes(r.freedBytes)}.`);
          setTimeout(() => location.reload(), 900);
        }, btn);
      }
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

  // One event stream serves every row: the server pushes each job as it changes, so there is no polling
  // and no per-download timer. EventSource reconnects on its own if the connection drops.
  let downloadStream = null;
  function watchDownloads() {
    if (downloadStream || !$('#downloads-table')) return;
    downloadStream = new EventSource(api('downloads/events'));
    downloadStream.addEventListener('progress', (ev) => {
      let job;
      try { job = JSON.parse(ev.data); } catch { return; }
      applyDownload(job);
    });
    downloadStream.onerror = () => {
      // EventSource retries by itself; a page left open overnight should not spam the user.
      if (downloadStream.readyState === EventSource.CLOSED) downloadStream = null;
    };
  }

  function applyDownload(job) {
    const row = $(`#downloads-table tr[data-id="${job.id}"]`);
    if (!row) {
      // A job queued from another tab: reload once so the new row appears with its controls.
      if (job.state === 'Queued' || job.state === 'Downloading') location.reload();
      return;
    }

    const bar = row.querySelector('progress');
    if (bar) { bar.value = job.bytesDone; bar.max = job.bytesTotal || 1; }
    const small = row.querySelector('td:nth-child(2) small');
    if (small) small.textContent = `${fmtBytes(job.bytesDone)} of ${fmtBytes(job.bytesTotal)}${job.bytesPerSecond ? ` - ${fmtBytes(job.bytesPerSecond)}/s` : ''}`;
    const status = row.querySelector('.status');
    if (status) { status.textContent = job.state; status.className = `status ${job.state.toLowerCase()}`; }

    if (['Completed', 'Failed', 'Cancelled'].includes(job.state)) {
      if (job.state === 'Completed') toast(`${job.request.modelName || job.request.repoId} is ready.`);
      if (job.state === 'Failed') toast(job.error || 'Download failed.', true);
      setTimeout(() => location.reload(), 800);
    }
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
        ${view.readmeHtml ? `<details class="model-card"><summary>Model card</summary><div class="markdown">${view.readmeHtml}</div></details>` : ''}
        ${variants.length ? `<table><thead><tr><th>Variant</th><th>Size</th><th>Fits</th><th></th></tr></thead><tbody>${variants.map((v) => `
          <tr><td><strong>${esc(v.name)}</strong>${v.quantization ? ` <span class="tag">${esc(v.quantization)}</span>` : ''}<br /><span class="muted">${v.format} - ${v.files.length} file(s)</span></td>
          <td>${fmtBytes(v.sizeBytes)}</td><td>${fitBadge(v.fit)}</td>
          <td class="actions"><button class="btn small" data-action="hub-download" data-repo="${esc(view.detail.summary.repoId)}" data-source="${esc(source)}" data-files="${esc(v.files.join('|'))}" data-name="${esc(view.detail.summary.name)}">Download</button></td></tr>`).join('')}</tbody></table>`
          : '<p class="empty">This repository has no GGUF or ONNX files NetCoreAI can run. It may need converting first.</p>'}
        <button class="btn small" data-action="hub-back">Back to search</button>`;
    }, btn);
  });

  // Uploads go up in chunks: a 4 GB model cannot ride in one request, and the session survives a
  // dropped chunk because the server tells us how many bytes it already has.
  const uploadForm = $('#upload-form');
  uploadForm?.addEventListener('submit', (ev) => {
    ev.preventDefault();
    const input = uploadForm.elements.file;
    const file = input.files && input.files[0];
    if (!file) { toast('Choose a file to upload.', true); return; }

    const status = $('#upload-status');
    const bar = $('#upload-progress');
    const button = uploadForm.querySelector('button[type="submit"]');
    const chunkSize = 8 * 1024 * 1024;

    guarded(async () => {
      const session = await call('POST', 'models/upload/init', { fileName: file.name, sizeBytes: file.size });
      bar.hidden = false;
      bar.max = file.size;
      let offset = 0;
      try {
        while (offset < file.size) {
          const end = Math.min(offset + chunkSize, file.size);
          const res = await fetch(api(`models/upload/${session.uploadId}?offset=${offset}`), {
            method: 'PUT',
            body: file.slice(offset, end),
            headers: { 'Content-Type': 'application/octet-stream' },
            credentials: 'same-origin',
          });
          if (!res.ok) throw new Error(`Upload failed at ${fmtBytes(offset)}: ${res.status} ${res.statusText}`);
          const state = await res.json();
          offset = state.receivedBytes;
          bar.value = offset;
          status.textContent = `${fmtBytes(offset)} of ${fmtBytes(file.size)}`;
        }

        status.textContent = 'Registering...';
        const done = await call('POST', `models/upload/${session.uploadId}/complete`, { name: uploadForm.elements.name.value || null });
        toast(`Uploaded and registered ${done.model.name}.`);
        setTimeout(() => location.reload(), 900);
      } catch (e) {
        // Abandon the scratch file rather than leaving gigabytes in uploads/.
        await call('DELETE', `models/upload/${session.uploadId}`).catch(() => {});
        bar.hidden = true;
        status.textContent = '';
        throw e;
      }
    }, button);
  });

  const importForm = $('#import-form');
  importForm?.addEventListener('submit', (ev) => {
    ev.preventDefault();
    const d = formData(importForm);
    if (!d.path && !d.url) { toast('Give a path on the server or a URL.', true); return; }
    guarded(async () => {
      const r = await call('POST', 'models/import', { path: d.path || null, url: d.url || null, name: d.name || null, copy: !!d.copy });
      if (r.job) { toast('Download queued.'); setTimeout(() => location.reload(), 500); } else { toast(`Imported ${r.model.name}.`); setTimeout(() => location.reload(), 900); }
    });
  });

  // One stream for the page, opened whenever the downloads table is on screen.
  watchDownloads();

  // Select-all for the reclaimable files list.
  $('#orphan-all')?.addEventListener('change', (ev) => {
    $$('.orphan').forEach((c) => { c.checked = ev.target.checked; });
  });

  // ---------- tools ----------
  const toolDetail = $('#tool-detail');

  async function openTool(id) {
    const tool = await call('GET', `tools/${encodeURIComponent(id)}`);
    $('#tool-detail-title').textContent = tool.name;
    const params = tool.parameters || [];

    // Locked parameters are listed with everything else, because "which of these can the model set?" is
    // the question this page exists to answer, and hiding them would answer it by omission.
    $('#tool-detail-body').innerHTML = `
      <form id="tool-form" class="grid-form" data-id="${esc(tool.id)}">
        <label>Name<input name="name" value="${esc(tool.name)}" required /></label>
        <label>Enabled<select name="enabled">
          <option value="true"${tool.enabled ? ' selected' : ''}>yes</option>
          <option value="false"${tool.enabled ? '' : ' selected'}>no</option></select></label>
        <label class="wide">Description — the model reads this to decide when to use the tool
          <textarea name="description" rows="2">${esc(tool.description || '')}</textarea></label>
        <label>Safety<select name="safety">
          <option value="ReadOnly"${tool.safety === 'ReadOnly' ? ' selected' : ''}>reads only</option>
          <option value="SideEffecting"${tool.safety === 'SideEffecting' ? ' selected' : ''}>changes data</option></select></label>
        <label>Confirmation<select name="confirmation">
          <option value="Auto"${tool.confirmation === 'Auto' ? ' selected' : ''}>run without asking</option>
          <option value="AskUser"${tool.confirmation === 'AskUser' ? ' selected' : ''}>ask the caller first</option>
          <option value="AdminOnly"${tool.confirmation === 'AdminOnly' ? ' selected' : ''}>administrators only</option></select></label>
        <label>Runs<select name="invocationMode">
          <option value="HttpLoopback"${tool.invocationMode === 'HttpLoopback' ? ' selected' : ''}>HTTP, this host</option>
          <option value="InProcess"${tool.invocationMode === 'InProcess' ? ' selected' : ''}>in-process</option>
          <option value="HttpExternal"${tool.invocationMode === 'HttpExternal' ? ' selected' : ''}>HTTP, elsewhere</option></select></label>
        <label>Timeout (seconds)<input name="timeoutSeconds" type="number" min="1" max="600" value="${tool.timeoutSeconds}" /></label>
        <label>Response: return only this path<input name="selectPath" value="${esc(tool.response?.selectPath || '')}" placeholder="data.total" /></label>
        <label>Response: maximum characters<input name="maxBytes" type="number" min="256" step="256" value="${tool.response?.maxBytes ?? 16384}" /></label>
        <label class="wide"><small class="muted">In-process is faster and needs no address, but a synthetic request has no client IP or TLS details. If this endpoint relies on middleware that reads those, leave it on HTTP.</small></label>

        <h3 class="wide">Parameters</h3>
        <div class="wide" id="tool-params">
          ${params.length ? params.map((p, i) => paramRow(p, i)).join('') : '<p class="empty">This tool takes no parameters.</p>'}
        </div>
        <button class="btn" type="submit">Save tool</button>
      </form>`;

    toolDetail.hidden = false;
    toolDetail.scrollIntoView({ behavior: 'smooth', block: 'nearest' });
    $('#tool-form').addEventListener('submit', (ev) => { ev.preventDefault(); saveTool(tool); });
  }

  function paramRow(p, i) {
    const binding = p.binding || 'Model';
    const label = { Model: 'the model', Claim: 'a claim', RequestMetadata: 'request metadata', Static: 'a fixed value' };
    return `<div class="param-row" data-i="${i}">
      <span class="mono">${esc(p.name)}</span>
      <span class="muted small">${esc(p.type)} in ${esc(p.location)}${p.required ? ', required' : ''}</span>
      <label class="small">Value from
        <select data-param="binding">
          ${Object.keys(label).map((b) => `<option value="${b}"${b === binding ? ' selected' : ''}>${label[b]}</option>`).join('')}
        </select></label>
      <label class="small">Source<input data-param="bindingSource" value="${esc(p.bindingSource || '')}" placeholder="tenant"${binding === 'Model' ? ' disabled' : ''} /></label>
      <label class="small wide">Description shown to the model<input data-param="description" value="${esc(p.description || '')}" /></label>
    </div>`;
  }

  // Only a model-supplied parameter has nothing to bind from; the rest need a source, and the server
  // refuses one without it rather than sending null for something like a tenant id.
  document.addEventListener('change', (ev) => {
    if (ev.target.matches('[data-param="binding"]')) {
      const source = ev.target.closest('.param-row').querySelector('[data-param="bindingSource"]');
      source.disabled = ev.target.value === 'Model';
      if (source.disabled) source.value = '';
    }
  });

  function saveTool(original) {
    const form = $('#tool-form');
    const d = formData(form);
    const parameters = $$('.param-row', form).map((row, i) => ({
      ...original.parameters[i],
      binding: row.querySelector('[data-param="binding"]').value,
      bindingSource: row.querySelector('[data-param="bindingSource"]').value || null,
      description: row.querySelector('[data-param="description"]').value || null,
    }));

    guarded(async () => {
      // The whole record goes back with the edited fields replaced: a PUT that dropped the approval flags
      // would quietly re-open a question an administrator has already answered.
      await call('PUT', `tools/${encodeURIComponent(original.id)}`, {
        ...original,
        name: d.name,
        description: d.description || null,
        enabled: d.enabled === 'true',
        safety: d.safety,
        confirmation: d.confirmation,
        invocationMode: d.invocationMode,
        timeoutSeconds: Number(d.timeoutSeconds),
        response: { ...original.response, selectPath: d.selectPath || null, maxBytes: Number(d.maxBytes) },
        parameters,
      });
      toast('Tool saved.');
      setTimeout(() => location.reload(), 700);
    });
  }

  async function openToolTest(id, name) {
    const tool = await call('GET', `tools/${encodeURIComponent(id)}`);

    // Named here because the result below can look wrong without it: a locked parameter is filled by the
    // host, so what the endpoint receives is not what was typed into this box.
    const locked = (tool.parameters || []).filter((p) => (p.binding || 'Model') !== 'Model');

    $('#tool-detail-title').textContent = `Test ${name}`;
    $('#tool-detail-body').innerHTML = `
      <form id="tool-test-form" class="grid-form" data-id="${esc(id)}">
        <label class="wide">Ask a question, and the model picks the arguments
          <input name="prompt" placeholder="What is order A-1?" /></label>
        <label class="wide">…or give the arguments yourself, as JSON
          <textarea name="arguments" rows="3" placeholder='{ "id": "A-1" }'></textarea></label>
        ${locked.length ? `<p class="muted small wide">${locked.map((p) => esc(p.name)).join(', ')} ${locked.length === 1 ? 'is' : 'are'} filled in by the host from your own identity, whatever is typed here or chosen by the model. The trial call runs as you.</p>` : ''}
        <button class="btn" type="submit">Run it</button>
      </form>
      <div id="tool-test-result"></div>`;

    toolDetail.hidden = false;
    toolDetail.scrollIntoView({ behavior: 'smooth', block: 'nearest' });
    $('#tool-test-form').addEventListener('submit', (ev) => { ev.preventDefault(); runToolTest(id); });
  }

  function runToolTest(id) {
    const d = formData($('#tool-test-form'));
    let args = null;
    if (d.arguments && d.arguments.trim()) {
      try { args = JSON.parse(d.arguments); } catch { toast('The arguments are not valid JSON.', true); return; }
    }

    const box = $('#tool-test-result');
    box.innerHTML = '<p class="muted">Running…</p>';
    guarded(async () => {
      const r = await call('POST', `tools/${encodeURIComponent(id)}/test`, { prompt: d.prompt || null, arguments: args });
      const used = Object.keys(r.arguments || {}).length
        ? `<h3>Arguments the tool was called with</h3><pre class="small">${esc(JSON.stringify(r.arguments, null, 2))}</pre>` : '';
      box.innerHTML = `
        ${r.error ? `<p class="muted">${esc(r.error)}</p>` : ''}
        ${r.modelSaid ? `<h3>What the model said</h3><p class="muted small">${esc(r.modelSaid)}</p>` : ''}
        ${used}
        ${r.success ? `<h3>Result <span class="muted small">${r.elapsedMs} ms</span></h3><pre class="small">${esc(r.output || '(empty)')}</pre>` : ''}`;
    });
  }

  $('#openapi-form')?.addEventListener('submit', (ev) => {
    ev.preventDefault();
    const d = formData(ev.target);
    guarded(async () => {
      const r = await call('POST', 'tools/import-openapi', { document: d.document, baseUrl: d.baseUrl || null });
      toast(`Imported ${r.imported} operation(s).`);
      setTimeout(() => location.reload(), 900);
    });
  });

  // Minimal markdown: fenced code, inline code, bold, links; everything else escaped. Module scope rather
  // than inside the chat page, because the agent playground renders answers the same way — and did not,
  // which is how a run there ended with "render is not defined" instead of an answer.
  function render(md) {
    let html = esc(md);
    html = html.replace(/```(\w*)\n([\s\S]*?)```/g, (_, l, c) => `<pre><code class="lang-${l}">${c}</code></pre>`);
    html = html.replace(/`([^`\n]+)`/g, '<code>$1</code>');
    html = html.replace(/\*\*([^*]+)\*\*/g, '<strong>$1</strong>');
    html = html.replace(/\[([^\]]+)\]\((https?:[^)]+)\)/g, '<a href="$2" target="_blank" rel="noopener">$1</a>');
    return html;
  }

  // ---------- agents ----------
  const agentDetail = $('#agent-detail');
  const agentOptions = (() => {
    try { return JSON.parse($('#agent-options')?.textContent || '{}'); } catch { return {}; }
  })();

  $('#agent-form')?.addEventListener('submit', (ev) => {
    ev.preventDefault();
    const d = formData(ev.target);
    guarded(async () => {
      await call('POST', 'agents', { id: d.id, name: d.name, model: d.model });
      toast(`Created ${d.name}.`);
      setTimeout(() => location.reload(), 700);
    });
  });

  async function openAgent(id) {
    const agent = await call('GET', `agents/${encodeURIComponent(id)}`);
    const models = agentOptions.models || [];
    const tools = agentOptions.tools || [];
    const bases = agentOptions.knowledge || [];
    const chosenTools = new Set(agent.toolIds || []);
    const chosenBases = new Set((agent.knowledge || []).map((k) => k.knowledgeBaseId));
    const model = models.find((m) => m.id === agent.model);

    $('#agent-detail-title').textContent = agent.name;
    $('#agent-detail-body').innerHTML = `
      <form id="agent-editor" class="grid-form">
        <label>Name<input name="name" value="${esc(agent.name)}" required /></label>
        <label>Enabled<select name="enabled">
          <option value="true"${agent.enabled ? ' selected' : ''}>yes</option>
          <option value="false"${agent.enabled ? '' : ' selected'}>no</option></select></label>
        <label class="wide">Description — for the people choosing between agents, not for the model
          <input name="description" value="${esc(agent.description || '')}" /></label>
        <label>Model<select name="model">
          ${models.map((m) => `<option value="${esc(m.id)}"${m.id === agent.model ? ' selected' : ''}>${esc(m.name)}</option>`).join('')}
        </select></label>
        <label>Temperature<input name="temperature" type="number" step="0.05" min="0" max="2" value="${agent.parameters?.temperature ?? ''}" placeholder="model default" /></label>
        <label class="wide">System prompt — placeholders like claims.name, request.tenant and agent.name are filled in from the host
          <textarea name="systemPrompt" rows="4">${esc(agent.systemPrompt || '')}</textarea></label>

        <h3 class="wide">Knowledge</h3>
        ${bases.length
          ? `<div class="wide">${bases.map((b) => `<label class="checkbox"><input type="checkbox" class="agent-kb" value="${esc(b.id)}"${chosenBases.has(b.id) ? ' checked' : ''} /> ${esc(b.name)}</label>`).join('')}</div>`
          : '<p class="empty wide">No knowledge bases yet.</p>'}

        <h3 class="wide">Tools</h3>
        ${model && model.tools === false
          ? `<p class="empty wide">${esc(model.name)} does not support tool calling, so tools would be ignored. Pick a model that does, or leave this agent without tools.</p>`
          : tools.length
            ? `<div class="wide">${tools.map((t) => `<label class="checkbox"><input type="checkbox" class="agent-tool" value="${esc(t.id)}"${chosenTools.has(t.id) ? ' checked' : ''} /> ${esc(t.name)}${t.safety === 'SideEffecting' ? ' <span class="tag warn">changes data</span>' : ''}</label>`).join('')}</div>`
            : '<p class="empty wide">No tools yet.</p>'}

        <h3 class="wide">Limits</h3>
        <label>Tool rounds per answer<input name="maxToolIterations" type="number" min="1" max="50" value="${agent.limits?.maxToolIterations ?? 8}" /></label>
        <label>Tool timeout (seconds)<input name="toolTimeoutSeconds" type="number" min="1" max="600" value="${agent.limits?.toolTimeoutSeconds ?? 30}" /></label>
        <label>Run timeout (seconds)<input name="runTimeoutSeconds" type="number" min="1" max="3600" value="${agent.limits?.runTimeoutSeconds ?? 300}" /></label>
        <label>Turns remembered<input name="windowTurns" type="number" min="1" max="100" value="${agent.memory?.windowTurns ?? 10}" /></label>
        <label class="wide">Access tags — who may run this agent (comma separated; blank means anyone)
          <input name="aclTags" value="${esc((agent.aclTags || []).join(', '))}" placeholder="role:support" /></label>
        <button class="btn" type="submit">Save agent</button>
      </form>`;

    agentDetail.hidden = false;
    agentDetail.scrollIntoView({ behavior: 'smooth', block: 'nearest' });
    $('#agent-editor').addEventListener('submit', (ev) => { ev.preventDefault(); saveAgent(agent); });
  }

  function saveAgent(original) {
    const d = formData($('#agent-editor'));
    guarded(async () => {
      // The whole record goes back with the edited fields replaced, so nothing the editor does not show —
      // the output schema, fallback models — is dropped by saving from this form.
      await call('PUT', `agents/${encodeURIComponent(original.id)}`, {
        ...original,
        name: d.name,
        description: d.description || null,
        enabled: d.enabled === 'true',
        model: d.model,
        systemPrompt: d.systemPrompt || null,
        parameters: { ...original.parameters, temperature: d.temperature === '' ? null : Number(d.temperature) },
        toolIds: $$('.agent-tool:checked').map((c) => c.value),
        knowledge: $$('.agent-kb:checked').map((c) => ({ knowledgeBaseId: c.value })),
        limits: {
          ...original.limits,
          maxToolIterations: Number(d.maxToolIterations),
          toolTimeoutSeconds: Number(d.toolTimeoutSeconds),
          runTimeoutSeconds: Number(d.runTimeoutSeconds),
        },
        memory: { ...original.memory, windowTurns: Number(d.windowTurns) },
        aclTags: d.aclTags ? d.aclTags.split(',').map((t) => t.trim()).filter(Boolean) : [],
      });
      toast('Agent saved.');
      setTimeout(() => location.reload(), 700);
    });
  }

  // The playground: one turn at a time, with what the agent did underneath it.
  function openAgentPlayground(id, name) {
    $('#agent-detail-title').textContent = `Try ${name}`;
    $('#agent-detail-body').innerHTML = `
      <div id="agent-messages" class="messages"><p class="empty">Ask it something.</p></div>
      <form id="agent-chat" class="chat-input">
        <textarea id="agent-text" rows="2" placeholder="Message… (Enter to send)" required></textarea>
        <button class="btn primary" type="submit">Send</button>
      </form>`;

    agentDetail.hidden = false;
    agentDetail.scrollIntoView({ behavior: 'smooth', block: 'nearest' });
    $('#agent-chat').addEventListener('submit', (ev) => { ev.preventDefault(); runAgent(id); });
    $('#agent-text').addEventListener('keydown', (ev) => {
      if (ev.key === 'Enter' && !ev.shiftKey) { ev.preventDefault(); $('#agent-chat').requestSubmit(); }
    });
  }

  async function runAgent(id) {
    const textEl = $('#agent-text');
    const question = textEl.value.trim();
    if (!question) return;

    const messages = $('#agent-messages');
    if (messages.querySelector('.empty')) messages.innerHTML = '';
    messages.insertAdjacentHTML('beforeend', `<div class="msg user"><div class="bubble">${esc(question)}</div></div>`);
    const bot = document.createElement('div');
    bot.className = 'msg assistant';
    bot.innerHTML = '<div class="bubble cursor"></div>';
    messages.appendChild(bot);
    textEl.value = '';

    const bubble = bot.querySelector('.bubble');
    const steps = [];
    let acc = '';

    try {
      const res = await fetch(api(`agents/${encodeURIComponent(id)}/run/stream`), {
        method: 'POST', headers: { 'Content-Type': 'application/json' }, credentials: 'same-origin',
        body: JSON.stringify({ message: question }),
      });
      if (!res.ok) throw new Error(problem(await res.text(), res));

      const reader = res.body.getReader(); const dec = new TextDecoder(); let buf = '';
      while (true) {
        const { value, done } = await reader.read(); if (done) break;
        buf += dec.decode(value, { stream: true });
        let idx;
        while ((idx = buf.indexOf('\n\n')) >= 0) {
          const chunk = buf.slice(0, idx); buf = buf.slice(idx + 2);
          const ev = /^event: (\w+)/m.exec(chunk)?.[1];
          const dataLine = chunk.split('\n').find((l) => l.startsWith('data: '));
          if (!ev || !dataLine) continue;
          const data = JSON.parse(dataLine.slice(6));

          if (ev === 'delta') { acc += data.text; bubble.innerHTML = render(acc); messages.scrollTop = messages.scrollHeight; }
          else if (ev === 'citations') { renderCitations(bot, data.citations); }
          else if (ev === 'step') { steps.push(data.step); renderSteps(bot, steps); }
          else if (ev === 'done') {
            // The trace is the point of the playground: the answer alone does not say which tool was
            // called with what, and that is usually where a wrong answer comes from.
            renderSteps(bot, data.response?.steps || steps);
            if (data.response?.citations?.length) renderCitations(bot, data.response.citations);
            if (data.response?.error) toast(`Stopped early: ${data.response.error}`, true);
          }
          else if (ev === 'error') { bubble.innerHTML = `<span class="muted">${esc(data.error || 'unknown error')}</span>`; toast(data.error, true); }
        }
      }
    } catch (e) {
      bubble.innerHTML = `<span class="muted">${esc(e.message)}</span>`;
      toast(e.message, true);
    } finally {
      bubble.classList.remove('cursor');
    }
  }

  function renderSteps(div, steps) {
    if (!steps || !steps.length) return;
    let box = div.querySelector('.steps');
    if (!box) { box = document.createElement('details'); box.className = 'steps'; div.appendChild(box); }
    box.innerHTML = `<summary>What it did (${steps.length})</summary>` + steps.map((s) => `
      <div class="citation">
        <strong>${esc(s.kind)}</strong> <span class="mono small">${esc(s.name || '')}</span>
        ${s.elapsedMs ? `<span class="muted small"> · ${s.elapsedMs} ms</span>` : ''}
        ${s.input ? `<pre class="small">${esc(s.input)}</pre>` : ''}
        ${s.output ? `<pre class="small">${esc(s.output)}</pre>` : ''}
      </div>`).join('');
  }

  async function openAgentRuns(id, name) {
    const runs = await call('GET', `agents/${encodeURIComponent(id)}/runs?limit=25`);
    $('#agent-detail-title').textContent = `Runs of ${name}`;
    $('#agent-detail-body').innerHTML = runs.length
      ? `<table><thead><tr><th>When</th><th>Asked</th><th>Answered</th><th>Did</th><th></th></tr></thead><tbody>${runs.map((r) => `
          <tr>
            <td class="small">${new Date(r.startedAt).toLocaleString()}<br /><span class="muted">${r.elapsedMs} ms</span></td>
            <td class="small">${esc((r.input || '').slice(0, 80))}</td>
            <td class="small">${r.success ? esc((r.output || '').slice(0, 80)) : `<span class="muted">${esc(r.error || 'failed')}</span>`}</td>
            <td class="small">${(r.steps || []).length} step(s)</td>
            <td class="actions"><button class="btn small" data-action="run-open" data-id="${esc(r.id)}">Trace…</button></td>
          </tr>`).join('')}</tbody></table>`
      : '<p class="empty">This agent has not run yet.</p>';

    agentDetail.hidden = false;
  }

  async function openRun(runId) {
    const run = await call('GET', `runs/${encodeURIComponent(runId)}`);
    $('#agent-detail-title').textContent = 'Run';
    $('#agent-detail-body').innerHTML = `
      <p class="muted small">${new Date(run.startedAt).toLocaleString()} · ${run.elapsedMs} ms · ${esc(run.modelId || '')}</p>
      <h3>Asked</h3><pre class="small">${esc(run.input || '')}</pre>
      ${run.error ? `<h3>Stopped</h3><p class="muted">${esc(run.error)}</p>` : ''}
      <h3>Answered</h3><pre class="small">${esc(run.output || '(nothing)')}</pre>
      <h3>What it did</h3>
      ${(run.steps || []).length
        ? run.steps.map((s) => `<div class="citation"><strong>${esc(s.kind)}</strong> <span class="mono small">${esc(s.name || '')}</span>
            ${s.input ? `<pre class="small">${esc(s.input)}</pre>` : ''}
            ${s.output ? `<pre class="small">${esc(s.output)}</pre>` : ''}</div>`).join('')
        : '<p class="muted">Nothing but the model itself.</p>'}`;

    agentDetail.hidden = false;
  }

  // ---------- knowledge ----------
  const kbForm = $('#kb-form');
  kbForm?.addEventListener('submit', (ev) => {
    ev.preventDefault();
    const d = formData(kbForm);
    guarded(async () => {
      await call('POST', 'kb', {
        id: d.id,
        name: d.name,
        embeddingModel: d.embeddingModel,
        chunking: { strategy: d.strategy },
      });
      toast(`Created ${d.name}.`);
      setTimeout(() => location.reload(), 700);
    });
  });

  async function openKnowledgeBase(id) {
    const view = await call('GET', `kb/${encodeURIComponent(id)}`);
    const body = $('#kb-detail-body');
    const sources = view.sources || [];
    const documents = view.documents || [];

    const kb = view.knowledgeBase;
    const indexed = (kb.documentCount || 0) > 0;

    body.innerHTML = `<h3>${esc(kb.name)}</h3>
      <details class="kb-settings"><summary>Settings</summary>
        <form id="kb-settings-form" class="grid-form">
          <label>Name<input name="name" value="${esc(kb.name)}" required /></label>
          <label>Embedding model<input value="${esc(kb.embeddingModel)}" disabled /></label>
          <label class="wide"><small class="muted">${indexed
            ? 'Fixed while documents are indexed: vectors from two models cannot be compared, so switching would return nonsense from a mixed index. Delete the documents to change it.'
            : 'Can still be changed while the base is empty.'}</small></label>
          <label>Chunking
            <select name="strategy">${['FixedSize', 'RecursiveStructure', 'Sentence', 'Row']
              .map((v) => `<option value="${v}"${v === kb.chunking.strategy ? ' selected' : ''}>${v}</option>`).join('')}</select></label>
          <label>Chunk size (tokens)<input name="maxTokens" type="number" min="32" step="16" value="${kb.chunking.maxTokens}" /></label>
          <label>Overlap (tokens)<input name="overlapTokens" type="number" min="0" step="8" value="${kb.chunking.overlapTokens}" /></label>
          <label>Passages per question<input name="topK" type="number" min="1" max="50" value="${kb.retrieval.topK}" /></label>
          <label>Minimum score<input name="minScore" type="number" min="0" max="1" step="0.05" value="${kb.retrieval.minScore ?? ''}" placeholder="keep everything" /></label>
          <label class="wide">Default access tags (comma separated; blank means public)<input name="defaultAclTags" value="${esc((kb.defaultAclTags || []).join(', '))}" /></label>
          <label class="wide"><small class="muted">Chunking applies to documents ingested from now on. Re-ingest a document to re-chunk it.</small></label>
          <button class="btn" type="submit">Save settings</button>
        </form>
      </details>
      ${sources.length ? `<table><thead><tr><th>Source</th><th>Type</th><th>Last synced</th><th></th></tr></thead><tbody>${sources.map((src) => `
        <tr><td><strong>${esc(src.name)}</strong>${src.lastError ? `<br /><small class="muted">${esc(src.lastError)}</small>` : ''}</td>
        <td>${esc(src.type)}</td><td>${src.lastSyncedAt ? new Date(src.lastSyncedAt).toLocaleString() : 'never'}</td>
        <td class="actions"><button class="btn small danger" data-action="kb-delete-source" data-id="${esc(src.id)}" data-kb="${esc(id)}">Remove</button></td></tr>`).join('')}</tbody></table>`
        : '<p class="empty">No sources yet. Add one below, or push documents with IKnowledgeClient.</p>'}
      <h3>Documents (${documents.length})</h3>
      <p><label class="btn small">Upload files<input type="file" id="kb-upload" data-kb="${esc(id)}" multiple hidden /></label>
        <span class="muted small" id="kb-upload-status">Files are stored with this base and indexed straight away.</span></p>
      ${documents.length ? `<table><thead><tr><th>Title</th><th>Chunks</th><th>Ingested</th><th></th></tr></thead><tbody>${documents.slice(0, 50).map((doc) => `
        <tr><td>${esc(doc.title)}<br /><small class="muted mono">${esc(doc.source || doc.id)}</small></td>
        <td>${doc.chunkCount}</td><td>${new Date(doc.ingestedAt).toLocaleString()}</td>
        <td class="actions"><button class="btn small danger" data-action="kb-delete-document" data-id="${esc(doc.id)}" data-kb="${esc(id)}">Remove</button></td></tr>`).join('')}</tbody></table>`
        : '<p class="empty">Nothing ingested yet.</p>'}`;

    $('#kb-settings-form')?.addEventListener('submit', (ev) => {
      ev.preventDefault();
      const d = formData(ev.target);
      guarded(async () => {
        // The whole record goes back, with only the edited fields replaced: a PUT that dropped the
        // counters or the vector store id would quietly detach the base from its own collection.
        await call('PUT', `kb/${encodeURIComponent(id)}`, {
          ...kb,
          name: d.name,
          chunking: {
            ...kb.chunking,
            strategy: d.strategy,
            maxTokens: Number(d.maxTokens),
            overlapTokens: Number(d.overlapTokens),
          },
          retrieval: {
            ...kb.retrieval,
            topK: Number(d.topK),
            minScore: d.minScore === '' ? null : Number(d.minScore),
          },
          defaultAclTags: d.defaultAclTags ? d.defaultAclTags.split(',').map((t) => t.trim()).filter(Boolean) : [],
        });
        toast('Settings saved.');
        openKnowledgeBase(id);
      });
    });

    // One request per file, so a large document that fails does not take the others with it, and the
    // status line can name the one that broke rather than saying "the upload failed".
    $('#kb-upload')?.addEventListener('change', async (ev) => {
      const files = [...ev.target.files];
      const status = $('#kb-upload-status');
      let done = 0;
      for (const file of files) {
        status.textContent = `Uploading ${file.name} (${done + 1} of ${files.length})…`;
        try {
          const res = await fetch(api(`kb/${encodeURIComponent(id)}/documents/upload?fileName=${encodeURIComponent(file.name)}`), {
            method: 'POST', credentials: 'same-origin', headers: { 'Content-Type': file.type || 'application/octet-stream' }, body: file,
          });
          if (!res.ok) throw new Error(problem(await res.text(), res));
          done++;
        } catch (e) {
          toast(`${file.name}: ${e.message}`, true);
        }
      }

      status.textContent = `${done} of ${files.length} file(s) indexed.`;
      if (done) { toast(`Indexed ${done} file(s).`); openKnowledgeBase(id); }
    });

    const form = $('#source-form');
    if (form) form.elements.knowledgeBaseId.value = id;
    const detail = $('#kb-detail');
    detail.hidden = false;
    detail.scrollIntoView({ behavior: 'smooth', block: 'nearest' });
  }

  const sourceForm = $('#source-form');
  if (sourceForm) {
    const typeSel = sourceForm.elements.type;
    const showFieldsFor = () => {
      for (const label of sourceForm.querySelectorAll('[data-for]')) {
        label.hidden = label.dataset.for !== typeSel.value;
      }
    };
    typeSel.addEventListener('change', showFieldsFor);
    showFieldsFor();
  }

  sourceForm?.addEventListener('submit', (ev) => {
    ev.preventDefault();
    const d = formData(sourceForm);
    if (!d.knowledgeBaseId) { toast('Open a knowledge base first.', true); return; }

    // Only the settings that belong to the chosen type are sent, so a file source does not carry a URL.
    const settings = {};
    if (d.type === 'files') {
      if (d.folder) settings.folder = d.folder;
      if (d.pattern) settings.pattern = d.pattern;
    } else if (d.type === 'rest') {
      settings.url = d.url;
      if (d.itemsPath) settings.itemsPath = d.itemsPath;
      if (d.titlePath) settings.titlePath = d.titlePath;
      if (d.contentPath) settings.contentPath = d.contentPath;
    } else if (d.type === 'sql') {
      for (const k of ['provider', 'connectionString', 'query', 'idColumn', 'titleColumn', 'contentColumns', 'aclColumn', 'modifiedColumn']) {
        if (d[k]) settings[k] = d[k];
      }
    }

    guarded(async () => {
      const source = {
        id: crypto.randomUUID().replace(/-/g, '').slice(0, 12),
        knowledgeBaseId: d.knowledgeBaseId,
        name: d.name,
        type: d.type,
        settings,
        schedule: d.schedule || null,
        aclTags: d.aclTags ? d.aclTags.split(',').map((t) => t.trim()).filter(Boolean) : [],
        enabled: true,
      };

      // Tested before saving: a wrong folder or URL is far cheaper to find now than mid-ingest.
      const test = await call('POST', `kb/${encodeURIComponent(d.knowledgeBaseId)}/sources/test`, source);
      if (!test.success) { toast(test.message, true); return; }

      await call('POST', `kb/${encodeURIComponent(d.knowledgeBaseId)}/sources`, source);
      toast(`${test.message} Source saved.`);
      setTimeout(() => location.reload(), 900);
    });
  });

  // Follows one ingestion job until it settles.
  function watchJob(id) {
    if (!id) return;
    const tick = async () => {
      try {
        const job = await call('GET', `jobs/${encodeURIComponent(id)}`);
        const row = $(`#jobs-table tr[data-id="${id}"]`);
        if (row) {
          const bar = row.querySelector('progress');
          if (bar) { bar.value = job.itemsDone; bar.max = job.itemsTotal || 1; }
          const small = row.querySelector('td:nth-child(2) small');
          if (small) small.textContent = `${job.itemsDone} of ${job.itemsTotal}${job.status ? ` - ${job.status}` : ''}`;
        }
        if (['Completed', 'Failed', 'Cancelled'].includes(job.state)) {
          if (job.state === 'Completed') toast(`Ingestion finished: ${job.itemsDone} item(s), ${job.itemsFailed} failed.`);
          if (job.state === 'Failed') toast(job.error || 'Ingestion failed.', true);
          return location.reload();
        }
        setTimeout(tick, 1000);
      } catch (e) {
        toast(e.message || String(e), true);
      }
    };
    setTimeout(tick, 600);
  }

  // Keep any running ingestion live when the page loads.
  $$('#jobs-table tr[data-id]').forEach((row) => {
    if (row.querySelector('.status.running, .status.queued')) watchJob(row.dataset.id);
  });

  // ---------- model editor ----------
  const modelForm = $('#model-form');
  async function openModelEditor(id) {
    const dto = await call('GET', `models/${encodeURIComponent(id)}`);
    const d = dto.descriptor || dto;
    const p = d.defaultParameters || {};
    const set = (name, value) => { const el = modelForm.elements[name]; if (el) el.value = value ?? ''; };
    set('id', d.id);
    set('name', d.name);
    set('contextLength', d.contextLength);
    set('temperature', p.temperature);
    set('topP', p.topP);
    set('topK', p.topK);
    set('maxOutputTokens', p.maxOutputTokens);
    set('repeatPenalty', p.repeatPenalty);
    set('seed', p.seed);
    set('systemPrompt', p.systemPrompt);
    set('stopSequences', (p.stopSequences || []).join(', '));
    set('tags', (d.tags || []).join(', '));
    set('notes', d.notes);
    modelForm.elements.loadOnStartup.checked = !!d.loadOnStartup;
    const editor = $('#model-editor');
    editor.hidden = false;
    editor.scrollIntoView({ behavior: 'smooth', block: 'nearest' });
  }

  modelForm?.addEventListener('submit', (ev) => {
    ev.preventDefault();
    const d = formData(modelForm);
    const list = (v) => (v ? v.split(',').map((x) => x.trim()).filter(Boolean) : null);
    // An empty field means "use the provider default", which is null rather than zero.
    const num = (v) => (v === '' || v === null || v === undefined ? null : Number(v));
    guarded(async () => {
      await call('PUT', `models/${encodeURIComponent(d.id)}`, {
        name: d.name || null,
        contextLength: num(d.contextLength),
        tags: list(d.tags),
        notes: d.notes || null,
        loadOnStartup: !!d.loadOnStartup,
        defaultParameters: {
          temperature: num(d.temperature),
          topP: num(d.topP),
          topK: num(d.topK),
          maxOutputTokens: num(d.maxOutputTokens),
          repeatPenalty: num(d.repeatPenalty),
          seed: num(d.seed),
          systemPrompt: d.systemPrompt || null,
          stopSequences: list(d.stopSequences),
        },
      });
      toast('Saved.');
      setTimeout(() => location.reload(), 700);
    });
  });

  // Default-model pickers on the settings page write aliases, not settings rows.
  $$('select[data-alias]').forEach((sel) => {
    sel.addEventListener('change', () => {
      const alias = sel.dataset.alias;
      guarded(async () => {
        if (sel.value) {
          await call('PUT', `models/aliases/${encodeURIComponent(alias)}`, { modelId: sel.value });
          toast(`${alias} now points at ${sel.options[sel.selectedIndex].text}.`);
        } else {
          await call('DELETE', `models/aliases/${encodeURIComponent(alias)}`);
          toast(`${alias} cleared.`);
        }
      });
    });
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
      // Cost only exists for remote models, priced from their connection rates.
      if (m.estimatedCost) parts.push(`~${Number(m.estimatedCost).toFixed(4)}`);
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
    // Left null unless something was typed, so the base's own defaults keep applying.
    function retrievalSettings() {
      const topK = Number($('#r-topk')?.value);
      const minScore = Number($('#r-minscore')?.value);
      const r = {};
      if (Number.isFinite(topK) && topK > 0) r.topK = topK;
      if ($('#r-minscore')?.value !== '' && Number.isFinite(minScore)) r.minScore = minScore;
      return Object.keys(r).length ? r : null;
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
          body: JSON.stringify({
            sessionId,
            model: modelSel.value,
            message: text,
            parameters: currentParams(),
            replaceFromMessageId: replaceFromMessageId || null,

            // Ticked bases ground this turn; none means the model answers on its own.
            knowledgeBaseIds: $$('.kb-pick:checked').map((c) => c.value),
            retrieval: retrievalSettings(),
            includeRetrievedPassages: $('#r-debug')?.checked === true,
          }),
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
            else if (ev === 'citations') { renderCitations(botDiv, data.citations); }
            else if (ev === 'retrieval') { renderPassages(botDiv, data.passages); }
            else if (ev === 'done') {
              updateMeta(botDiv, { ...data.message, raw: data.message.content });

              // A reopened conversation renders citations from the stored message, so sources survive a reload.
              if (data.message.citationsJson) { try { renderCitations(botDiv, JSON.parse(data.message.citationsJson)); } catch { /* stored badly; the answer still shows */ } }
              if (data.error) toast(`Stopped early: ${data.error}`, true);
            }
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
    function renderCitations(div, citations) {
      if (!citations || !citations.length) return;
      let box = div.querySelector('.citations');
      if (!box) {
        box = document.createElement('details');
        box.className = 'citations';
        div.appendChild(box);
      }

      box.innerHTML = `<summary>${citations.length} source${citations.length === 1 ? '' : 's'}</summary>` +
        citations.map((c) => {
          const where = c.page ? `page ${c.page}` : (c.section ? esc(c.section) : '');
          return `<div class="citation"><strong>[${c.ordinal}] ${esc(c.title)}</strong>${where ? ` <span class="muted">${where}</span>` : ''}
            <div class="muted small">${esc(c.snippet || '')}</div></div>`;
        }).join('');
    }

    // Every passage retrieval found, including the ones the answer ignored: the point is to see what the
    // model was given, so a chunk that should have been retrieved and was not is visible by its absence.
    function renderPassages(div, passages) {
      if (!passages || !passages.length) return;
      let box = div.querySelector('.passages');
      if (!box) { box = document.createElement('details'); box.className = 'passages'; div.appendChild(box); }
      box.innerHTML = `<summary>Retrieved chunks (${passages.length})</summary>` +
        passages.map((p) => {
          const where = p.page ? `page ${p.page}` : (p.section ? esc(p.section) : '');
          return `<div class="citation"><strong>[${p.ordinal}] ${esc(p.title)}</strong>
            <span class="muted small">score ${p.score.toFixed(3)}${where ? ` · ${where}` : ''}</span>
            <pre class="small">${esc(p.text || '')}</pre></div>`;
        }).join('');
    }

    // Remembered so a tuning session is not retyped on every reload; per browser, never sent anywhere else.
    for (const el of [$('#r-topk'), $('#r-minscore'), $('#r-debug')].filter(Boolean)) {
      const key = `netcoreai.retrieval.${el.id}`;
      const saved = localStorage.getItem(key);
      if (saved !== null) { if (el.type === 'checkbox') el.checked = saved === 'true'; else el.value = saved; }
      el.addEventListener('change', () => localStorage.setItem(key, el.type === 'checkbox' ? el.checked : el.value));
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
