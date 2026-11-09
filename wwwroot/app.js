'use strict';
(() => {
  const $ = id => document.getElementById(id);
  const authKey = 'diff-review-slim:token';
  const draftKey = 'diff-review-slim:draft';
  let token = '', metadata = null, current = null, generation = 0;
  let terminal = false, busy = false, connected = false, storageAvailable = true;
  let selected = null, comments = [], reviewed = new Set(), expanded = new Set();
  let tokens = null;
  const scroll = new Map();
  const escapeText = text => text.replace(/[&<>]/g, c => c === '&' ? '&amp;' : c === '<' ? '&lt;' : '&gt;');

  function node(tag, className, text) {
    const element = document.createElement(tag);
    if (className) element.className = className;
    if (text !== undefined) element.textContent = text;
    return element;
  }
  function banner(message, kind = '') { $('banner').textContent = message; $('banner').className = kind; }
  function storage(action) {
    try { return action(sessionStorage); }
    catch { storageAvailable = false; return null; }
  }
  function save() {
    if (!metadata || terminal) return;
    const draft = JSON.stringify({ sessionId: metadata.sessionId, selected, comments,
      reviewed: [...reviewed], overallNote: $('overall').value, wrap: $('wrap').checked, full: $('full').checked });
    if (new Blob([draft]).size > 256000) { banner('Draft is too large for refresh recovery. Shorten the review before submitting.', 'warning'); return; }
    storage(s => s.setItem(draftKey, draft));
    if (!storageAvailable) banner('Browser storage is unavailable. Review works, but refreshing may lose your draft and connection.', 'warning');
  }
  function clearStorage() { storage(s => { s.removeItem(authKey); s.removeItem(draftKey); }); }
  function controls() {
    for (const id of ['finish', 'cancel', 'copy']) $(id).disabled = !connected || busy || terminal;
    $('file-note').disabled = !current || busy || terminal;
    $('reviewed').disabled = !selected || busy || terminal;
  }
  async function api(path, body) {
    const response = await fetch(path, { method: body === undefined ? 'GET' : 'POST', cache: 'no-store',
      headers: { 'X-Review-Token': token, ...(body === undefined ? {} : { 'Content-Type': 'application/json' }) },
      body: body === undefined ? undefined : JSON.stringify(body) });
    if (response.status === 410) {
      terminal = true; connected = false; clearStorage(); controls();
      throw new Error('This review has ended. Start a new review from the terminal.');
    }
    const result = await response.json().catch(() => ({}));
    if (!response.ok && !result.status) throw new Error(result.error || `Request failed (${response.status}).`);
    return result;
  }
  async function lifetime() {
    const response = await fetch('/api/lifetime', { cache: 'no-store', headers: { 'X-Review-Token': token } });
    if (!response.ok || !response.body) throw new Error('Cannot connect to this review. Reopen the URL printed by the reviewer.');
    const reader = response.body.getReader();
    await reader.read(); // server flushes an initial acknowledgement; no polling or heartbeats
    connected = true; controls();
    void (async () => {
      try { while (!(await reader.read()).done) { /* held-open lifetime response */ } }
      catch { /* refresh/close/transport loss are handled by the server grace period */ }
      if (!terminal && !busy) {
        connected = false; controls();
        banner('Review connection lost. Reload promptly to reconnect, or start a new review from the terminal.', 'warning');
      }
    })();
  }
  function fuzzy(path, query) {
    path = path.toLowerCase(); query = query.toLowerCase();
    let at = 0;
    for (const c of query) { at = path.indexOf(c, at); if (at < 0) return false; at++; }
    return true;
  }
  function renderFiles() {
    const root = { folders: new Map(), files: [] };
    for (const file of metadata.files) {
      const path = file.newPath || file.oldPath;
      if (!fuzzy(path, $('filter').value)) continue;
      const parts = path.split('/');
      let folder = root;
      for (const part of parts.slice(0, -1)) {
        if (!folder.folders.has(part)) folder.folders.set(part, { folders: new Map(), files: [] });
        folder = folder.folders.get(part);
      }
      folder.files.push([file, parts.at(-1) || path]);
    }
    function tree(folder) {
      const list = node('ul', 'tree');
      for (const [name, child] of folder.folders) {
        const item = node('li'), details = node('details'); details.open = true;
        details.append(node('summary', '', name), tree(child)); item.append(details); list.append(item);
      }
      for (const [file, name] of folder.files) {
        const item = node('li'), button = node('button', 'file-button' + (file.id === selected ? ' selected' : ''));
        button.type = 'button'; button.dataset.file = file.id;
        button.title = `${file.status}: ${file.oldPath || ''}${file.oldPath !== file.newPath ? ' → ' + (file.newPath || '') : ''}`;
        button.append(node('span', 'file-status', reviewed.has(file.id) ? '✓' : file.status[0].toUpperCase()), node('span', 'file-name', name));
        const count = node('span', 'file-count'); count.dataset.count = file.id; button.append(count);
        item.append(button); list.append(item);
      }
      return list;
    }
    $('files').replaceChildren(tree(root)); updateCounts();
  }
  function updateCounts() {
    for (const element of $('files').querySelectorAll('[data-count]')) {
      const count = comments.filter(c => c.fileId === element.dataset.count && c.body.trim()).length;
      element.textContent = count ? String(count) : '';
    }
    $('counts').textContent = `${reviewed.size}/${metadata.files.length} reviewed · ${comments.filter(c => c.body.trim()).length} comments`;
  }
  function editor(comment) {
    const article = node('article', 'comment'), label = node('label');
    label.htmlFor = 'comment-' + comment.id;
    label.textContent = comment.side === 'file' ? 'File comment' : `${comment.side} line ${comment.startLine}`;
    const input = node('textarea'); input.id = label.htmlFor; input.dataset.comment = comment.id;
    input.maxLength = 8000; input.rows = 3; input.value = comment.body;
    const remove = node('button', '', 'Delete'); remove.dataset.delete = comment.id; remove.type = 'button';
    article.append(label, input, remove); return article;
  }
  function lineSide(row, side) {
    const line = row && (side === 'old' ? row.oldLine : row.newLine);
    const container = node('div', 'side ' + side + '-side ' + (row ? row.kind : 'empty'));
    if (!row || line === null) return container;
    const number = node('button', 'line-number', String(line)); number.type = 'button';
    number.dataset.line = String(line); number.dataset.side = side;
    number.setAttribute('aria-label', `Comment on ${side} line ${line}`);
    number.title = `Add comment on ${side} line ${line}`;
    const code = node('pre');
    const highlighted = tokens ? (side === 'old' ? tokens.old : tokens.new).get(row) : undefined;
    // Pre-highlighted rows are escaped by the highlighter; plain rows escape here.
    // Anything other than a string (missing highlighter, uncoloured language) must
    // fall back to text, or the row renders empty.
    code.innerHTML = typeof highlighted === 'string' ? highlighted : escapeText(row.text.replace(/\r$/, ''));
    container.append(number, code);
    if (side === 'old' ? row.oldNoNewline : row.newNoNewline)
      container.append(node('span', 'newline-note', 'No final newline'));
    for (const comment of comments.filter(c => c.fileId === selected && c.side === side && c.startLine === line))
      container.append(editor(comment));
    return container;
  }
  function paired(rows) {
    const result = [];
    for (let i = 0; i < rows.length;) {
      const row = rows[i];
      if (row.kind === 'context') { result.push({ old: row, new: row, context: true }); i++; continue; }
      const old = [], added = [];
      while (i < rows.length && rows[i].kind === 'deleted') old.push(rows[i++]);
      while (i < rows.length && rows[i].kind === 'added') added.push(rows[i++]);
      for (let n = 0; n < Math.max(old.length, added.length); n++) result.push({ old: old[n], new: added[n], context: false });
    }
    return result;
  }
  function renderDiff() {
    if (!current) return;
    const position = $('viewport').scrollTop;
    $('file-comments').replaceChildren(...comments.filter(c => c.fileId === selected && c.side === 'file').map(editor));
    const fragment = document.createDocumentFragment();
    const heading = node('div', 'diff-heading');
    heading.append(node('span', '', 'Old' + (current.oldCrlf ? ' · CRLF' : '')),
      node('span', '', 'New' + (current.newCrlf ? ' · CRLF' : '')));
    fragment.append(heading);
    if (current.omission) fragment.append(node('div', 'notice warning', current.omission));
    else if (!current.rows.length) fragment.append(node('div', 'notice', 'Empty file or metadata-only change. File comments are available.'));
    else {
      const pairs = paired(current.rows);
      const append = pair => {
        const row = node('div', 'diff-row' + (pair.context ? ' context' : ''));
        row.append(lineSide(pair.old, 'old'), lineSide(pair.new, 'new')); fragment.append(row);
      };
      for (let i = 0; i < pairs.length;) {
        if (!pairs[i].context || $('full').checked) { append(pairs[i++]); continue; }
        const start = i;
        while (i < pairs.length && pairs[i].context) i++;
        const run = pairs.slice(start, i);
        const hasComments = run.some(p => comments.some(c => c.fileId === selected &&
          ((c.side === 'old' && c.startLine === p.old.oldLine) || (c.side === 'new' && c.startLine === p.new.newLine))));
        if (run.length <= 8 || expanded.has(start) || hasComments) run.forEach(append);
        else {
          run.slice(0, 3).forEach(append);
          const gap = node('div', 'context-gap'), button = node('button', '', `Show ${run.length - 6} unchanged lines`);
          button.dataset.expand = String(start); gap.append(button); fragment.append(gap);
          run.slice(-3).forEach(append);
        }
      }
    }
    $('diff').replaceChildren(fragment);
    $('viewport').scrollTop = position;
    controls();
  }
  async function selectFile(id) {
    if (selected) scroll.set(selected, $('viewport').scrollTop);
    selected = id; current = null; expanded = new Set();
    const request = ++generation, file = metadata.files.find(f => f.id === id);
    if (!file) return;
    $('filename').textContent = file.oldPath && file.newPath && file.oldPath !== file.newPath
      ? `${file.oldPath} → ${file.newPath}` : file.newPath || file.oldPath;
    $('reviewed').checked = reviewed.has(id);
    $('file-comments').replaceChildren(); $('diff').replaceChildren(node('div', 'notice', 'Loading captured diff…'));
    renderFiles(); save(); controls();
    try {
      const result = await api('/api/files/' + encodeURIComponent(id));
      if (request !== generation) return;
      current = result;
      tokens = globalThis.DiffReviewHighlight
        ? globalThis.DiffReviewHighlight.tokenize(file.newPath || file.oldPath, result.rows) : null;
      renderDiff(); $('viewport').scrollTop = scroll.get(id) || 0;
    } catch (error) {
      if (request !== generation) return;
      const notice = node('div', 'notice warning', error.message), retry = node('button', '', 'Retry');
      retry.dataset.retry = 'true'; notice.append(document.createElement('br'), retry); $('diff').replaceChildren(notice);
    }
  }
  function addComment(side, line = null) {
    if (!selected || terminal || busy) return;
    if (comments.length >= 128) { banner('Maximum 128 comments per review.', 'warning'); return; }
    const comment = { id: crypto.randomUUID(), fileId: selected, side, startLine: line, endLine: line, body: '' };
    comments.push(comment); save(); renderDiff(); updateCounts();
    $('comment-' + comment.id)?.focus();
  }
  function payload() { return { overallNote: $('overall').value, comments }; }
  async function finish(cancel) {
    if (busy || terminal) return;
    if (!cancel && new Blob([JSON.stringify(payload())]).size > 256000) {
      banner('Review exceeds the submission limit. Shorten comments before finishing.', 'warning'); return;
    }
    busy = true; controls();
    try {
      const result = await api(cancel ? '/api/cancel' : '/api/submit', cancel ? {} : payload());
      terminal = true; connected = false; clearStorage(); controls();
      $('overall').disabled = true;
      for (const input of document.querySelectorAll('[data-comment], [data-line], [data-delete]')) input.disabled = true;
      banner((result.message || 'Review ended.') + ' You can close this window.', result.status === 'error' ? 'warning' : 'done');
      // Script-opened/app windows may refuse this. Never kill a shared browser.
      window.close();
    } catch (error) {
      banner(error.message + ' If the connection was lost during submission, check the terminal before retrying.', 'warning');
    } finally { busy = false; controls(); }
  }
  function markdown() {
    let text = 'Please address the following feedback\n\n';
    if ($('overall').value.trim()) text += $('overall').value.trim() + '\n\n';
    let number = 0;
    for (const comment of comments.filter(c => c.body.trim())) {
      const file = metadata.files.find(f => f.id === comment.fileId);
      const display = path => /[\x00-\x1f\x7f]/.test(path) ? JSON.stringify(path) : path;
      const path = file.oldPath && file.newPath && file.oldPath !== file.newPath
        ? `${display(file.oldPath)} -> ${display(file.newPath)}` : display(file.newPath || file.oldPath);
      text += `${++number}. [git diff] ${path}${comment.side === 'file' ? '' : ':' + comment.startLine + ' (' + comment.side + ')'}\n`;
      text += comment.body.trim().split(/\r?\n/).map(line => '   ' + line).join('\n') + '\n\n';
    }
    return text.trimEnd() + '\n';
  }
  $('filter').addEventListener('input', renderFiles);
  $('filter').addEventListener('keydown', event => { if (event.key === 'Escape') { $('filter').value = ''; renderFiles(); } });
  $('files').addEventListener('click', event => { const button = event.target.closest('[data-file]'); if (button) void selectFile(button.dataset.file); });
  $('overall').addEventListener('input', save);
  $('reviewed').addEventListener('change', () => { if ($('reviewed').checked) reviewed.add(selected); else reviewed.delete(selected); save(); renderFiles(); });
  $('wrap').addEventListener('change', () => { $('viewport').classList.toggle('wrap', $('wrap').checked); save(); });
  $('full').addEventListener('change', () => { save(); renderDiff(); });
  $('file-note').addEventListener('click', () => addComment('file'));
  $('viewport').addEventListener('click', event => {
    const target = event.target.closest('button'); if (!target) return;
    if (target.dataset.line) addComment(target.dataset.side, Number(target.dataset.line));
    if (target.dataset.delete) { comments = comments.filter(c => c.id !== target.dataset.delete); save(); renderDiff(); updateCounts(); }
    if (target.dataset.expand !== undefined) { expanded.add(Number(target.dataset.expand)); renderDiff(); }
    if (target.dataset.retry) void selectFile(selected);
  });
  $('viewport').addEventListener('input', event => {
    const id = event.target.dataset.comment; if (!id) return;
    const comment = comments.find(c => c.id === id); if (comment) { comment.body = event.target.value; save(); updateCounts(); }
  });
  $('finish').addEventListener('click', () => void finish(false));
  $('cancel').addEventListener('click', () => void finish(true));
  $('copy').addEventListener('click', async () => {
    try { await navigator.clipboard.writeText(markdown()); banner('Feedback copied. Finish review to submit, or Cancel to discard.'); }
    catch { banner('Browser clipboard access failed. You can still submit feedback through the CLI.', 'warning'); }
  });
  async function init() {
    const fragment = location.hash.slice(1);
    token = fragment || storage(s => s.getItem(authKey)) || '';
    if (!/^[a-f0-9]{64}$/.test(token)) throw new Error('Missing review token. Open the complete URL printed by the reviewer.');
    storage(s => s.setItem(authKey, token));
    history.replaceState(null, '', location.pathname + location.search);
    metadata = await api('/api/session');
    let draft = null;
    try { draft = JSON.parse(storage(s => s.getItem(draftKey)) || 'null'); } catch { /* ignore corrupt local draft */ }
    if (draft?.sessionId === metadata.sessionId) {
      const files = new Set(metadata.files.map(f => f.id));
      comments = Array.isArray(draft.comments) ? draft.comments.filter(c => c && typeof c.id === 'string' &&
        files.has(c.fileId) && ['file', 'old', 'new'].includes(c.side) && typeof c.body === 'string').slice(0, 128) : [];
      reviewed = new Set(Array.isArray(draft.reviewed) ? draft.reviewed.filter(id => files.has(id)) : []);
      $('overall').value = typeof draft.overallNote === 'string' ? draft.overallNote.slice(0, 16000) : '';
      $('wrap').checked = !!draft.wrap; $('full').checked = !!draft.full;
      if (files.has(draft.selected)) selected = draft.selected;
    }
    $('viewport').classList.toggle('wrap', $('wrap').checked);
    $('repository').textContent = metadata.repository;
    $('identity').textContent = metadata.base
      ? `Committed branch comparison · ${metadata.base} merge-base ${metadata.mergeBase.slice(0, 12)} → HEAD ${metadata.head.slice(0, 12)}`
      : `Working tree against ${metadata.head ? 'HEAD ' + metadata.head.slice(0, 12) : 'an empty tree (no commits yet)'}`;
    await lifetime();
    banner('Connected. Changes are read-only; line-number buttons add comments.');
    renderFiles();
    if (metadata.files.length) await selectFile(selected || metadata.files[0].id);
    else { $('filename').textContent = 'No changes'; $('diff').append(node('div', 'notice', 'No files in this comparison. History/all-files scopes are not implemented yet.')); }
    save();
  }
  void init().catch(error => { banner(error.message, 'warning'); controls(); });
})();
