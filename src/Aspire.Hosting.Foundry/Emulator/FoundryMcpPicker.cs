namespace Aspire.Hosting.Foundry.Emulator;

/// <summary>
/// The page <see cref="FoundryMcp"/> serves as its MCP app. It speaks both dialects a host may offer:
/// the MCP Apps postMessage protocol (<c>ui/initialize</c>, <c>ui/notifications/tool-result</c>,
/// <c>ui/message</c>) and the Apps SDK's <c>window.openai</c>. A pick is sent as the person's next
/// message, in the "Solution / Project" form the scenario's gate and project lookup read.
/// </summary>
internal static class FoundryMcpPicker
{
    public const string Html = """
<!doctype html>
<html lang="da">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Vælg projekt</title>
<style>
  :root {
    --bg: #ffffff; --card: #f7f7f8; --line: #e4e4e7; --text: #18181b; --muted: #71717a;
    --accent: #2563eb; --accent-soft: #dbeafe; --ok: #16a34a;
    color-scheme: light;
  }
  @media (prefers-color-scheme: dark) {
    :root:not([data-theme="light"]) {
      --bg: #18181b; --card: #222226; --line: #34343a; --text: #f4f4f5; --muted: #a1a1aa;
      --accent: #60a5fa; --accent-soft: #1e293b; --ok: #4ade80; color-scheme: dark;
    }
  }
  :root[data-theme="dark"] {
    --bg: #18181b; --card: #222226; --line: #34343a; --text: #f4f4f5; --muted: #a1a1aa;
    --accent: #60a5fa; --accent-soft: #1e293b; --ok: #4ade80; color-scheme: dark;
  }
  * { box-sizing: border-box; }
  html, body { margin: 0; background: var(--bg); color: var(--text);
    font: 14px/1.45 var(--font-sans, ui-sans-serif, system-ui, -apple-system, "Segoe UI", Roboto, sans-serif); }
  .wrap { padding: 16px; }
  .head { display: flex; align-items: center; gap: 10px; margin-bottom: 4px; }
  .mark { width: 28px; height: 28px; border-radius: 8px; background: var(--accent); color: #fff;
    display: grid; place-items: center; font-weight: 700; font-size: 13px; flex: none; }
  h1 { font-size: 15px; margin: 0; font-weight: 600; }
  .sub { color: var(--muted); margin: 0 0 14px 38px; font-size: 13px; }
  .list { display: grid; gap: 8px; }
  button.row { all: unset; box-sizing: border-box; cursor: pointer; display: flex; align-items: center; gap: 12px;
    width: 100%; padding: 12px 14px; border: 1px solid var(--line); border-radius: 10px; background: var(--card);
    transition: border-color .12s, background .12s; }
  button.row:hover:not([disabled]), button.row:focus-visible { border-color: var(--accent); }
  button.row[aria-pressed="true"] { border-color: var(--accent); background: var(--accent-soft); }
  button.row[disabled] { cursor: default; }
  button.row[disabled]:not([aria-pressed="true"]) { opacity: .45; }
  .num { width: 24px; height: 24px; border-radius: 50%; border: 1px solid var(--line); display: grid;
    place-items: center; font-size: 12px; color: var(--muted); flex: none; }
  [aria-pressed="true"] .num { background: var(--accent); border-color: var(--accent); color: #fff; }
  .text { flex: 1; min-width: 0; }
  .project { font-weight: 600; }
  .solution { color: var(--muted); font-size: 12px; }
  .repo { font-size: 12px; color: var(--muted); display: flex; align-items: center; gap: 5px; white-space: nowrap; }
  .repo i { width: 7px; height: 7px; border-radius: 50%; background: var(--ok); display: inline-block; }
  .empty, .done { color: var(--muted); padding: 10px 2px 0; font-size: 13px; }
  .done strong { color: var(--text); }
</style>
</head>
<body>
<div class="wrap">
  <div class="head"><div class="mark">H</div><h1>Hvilket projekt hører det til?</h1></div>
  <p class="sub">Koden pushes til projektets repository i Home, og pipelinen tager den derfra.</p>
  <div class="list" id="list"><div class="empty">Henter dine projekter…</div></div>
  <div class="done" id="done" hidden></div>
</div>
<script>
(() => {
  const list = document.getElementById('list');
  const done = document.getElementById('done');
  let nextId = 1, picked = false;
  const pending = new Map();

  function post(message) { window.parent.postMessage(message, '*'); }
  function request(method, params) {
    const id = nextId++;
    post({ jsonrpc: '2.0', id, method, params });
    return new Promise((resolve, reject) => pending.set(id, { resolve, reject }));
  }
  function notify(method, params) { post({ jsonrpc: '2.0', method, params }); }

  function resize() {
    const r = document.documentElement.getBoundingClientRect();
    notify('ui/notifications/size-changed', { width: Math.ceil(r.width), height: Math.ceil(r.height) });
    if (window.openai && window.openai.notifyIntrinsicHeight) window.openai.notifyIntrinsicHeight(Math.ceil(r.height));
  }
  new ResizeObserver(resize).observe(document.body);

  function projectsFrom(result) {
    if (!result) return null;
    const fromMeta = result._meta && result._meta['home/projects'];
    const fromData = result.structuredContent && result.structuredContent.projects;
    return fromMeta || fromData || (Array.isArray(result.projects) ? result.projects : null);
  }

  function render(projects) {
    list.textContent = '';
    if (!projects.length) {
      list.innerHTML = '<div class="empty">Du har ingen projekter i Home endnu. Opret projektet i Home først.</div>';
      return;
    }
    for (const p of projects) {
      const b = document.createElement('button');
      b.className = 'row';
      b.setAttribute('aria-pressed', 'false');
      b.innerHTML = '<span class="num"></span><span class="text"><div class="project"></div><div class="solution"></div></span>' +
        (p.hasRepository ? '<span class="repo"><i></i>Repository klar</span>' : '');
      b.querySelector('.num').textContent = p.number;
      b.querySelector('.project').textContent = p.project;
      b.querySelector('.solution').textContent = p.solution ? 'Løsning: ' + p.solution : '';
      b.addEventListener('click', () => pick(p, b));
      list.appendChild(b);
    }
  }

  async function pick(p, button) {
    if (picked) return;
    picked = true;
    for (const b of list.querySelectorAll('button')) b.disabled = true;
    button.setAttribute('aria-pressed', 'true');
    done.hidden = false;
    done.innerHTML = 'Valgt: <strong></strong>. Codex kobler mappen på projektet.';
    done.querySelector('strong').textContent = p.label;
    try {
      if (window.openai && window.openai.sendFollowUpMessage) {
        await window.openai.sendFollowUpMessage({ prompt: p.label });
      } else {
        await request('ui/message', { role: 'user', content: { type: 'text', text: p.label } });
      }
    } catch (e) {
      done.innerHTML = 'Kunne ikke sende valget. Skriv <strong></strong> i chatten.';
      done.querySelector('strong').textContent = String(p.number);
    }
  }

  function applyTheme(ctx) {
    if (!ctx) return;
    if (ctx.theme) document.documentElement.dataset.theme = ctx.theme;
    const vars = ctx.styles && ctx.styles.variables;
    if (vars) for (const [k, v] of Object.entries(vars)) if (v) document.documentElement.style.setProperty(k, v);
  }

  window.addEventListener('message', (event) => {
    const m = event.data;
    if (!m || m.jsonrpc !== '2.0') return;
    if (m.id !== undefined && pending.has(m.id) && !m.method) {
      const { resolve, reject } = pending.get(m.id);
      pending.delete(m.id);
      m.error ? reject(m.error) : resolve(m.result);
      return;
    }
    if (m.method === 'ui/notifications/tool-result') {
      const projects = projectsFrom(m.params);
      if (projects && !picked) render(projects);
    } else if (m.method === 'ui/notifications/host-context-changed') {
      applyTheme(m.params);
    } else if (m.method === 'ui/resource-teardown' && m.id !== undefined) {
      post({ jsonrpc: '2.0', id: m.id, result: {} });
    }
  });

  // Apps SDK hosts hand over the result as globals, and say when they change.
  function fromOpenAi() {
    const o = window.openai;
    if (!o) return;
    if (o.theme) document.documentElement.dataset.theme = o.theme;
    const projects = projectsFrom({ _meta: o.toolResponseMetadata, structuredContent: o.toolOutput });
    if (projects && !picked) render(projects);
  }
  window.addEventListener('openai:set_globals', fromOpenAi);
  fromOpenAi();

  request('ui/initialize', {
    protocolVersion: '2026-01-26',
    appCapabilities: { availableDisplayModes: ['inline'] },
    clientInfo: { name: 'home-project-picker', version: '1.0.0' },
  }).then((result) => {
    applyTheme(result && result.hostContext);
    notify('ui/notifications/initialized', {});
    resize();
  }, () => {});
})();
</script>
</body>
</html>
""";
}
