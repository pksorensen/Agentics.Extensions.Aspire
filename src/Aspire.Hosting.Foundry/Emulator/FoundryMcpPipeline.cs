using System.Diagnostics;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Aspire.Hosting.Foundry.Emulator;

/// <summary>
/// <c>show_pipeline</c>: where the project's newest push is, as an MCP app that keeps itself current.
/// The emulator holds only the project's push credential, so it reads the commits the way any clone
/// would, with a fetch into a bare copy of its own. DEMO runs the newest commit on main and
/// shows as deploying for <see cref="DeployTime"/> after it was committed, the rule Home's pipeline
/// uses. PROD is IT's to approve in Home: the key's <c>pipelineUrl</c> says which commit IT approved,
/// when and by whom, and the address IT gave it, and PROD then deploys for as long as DEMO did.
/// </summary>
internal static class FoundryMcpPipeline
{
    public const string Tool = "show_pipeline";
    public const string Uri = "ui://home/pipeline.html";
    public const string HomeClient = "foundry-home";

    /// <summary>Home's <c>ProjectPipelines.DeployTime</c>.</summary>
    private static readonly TimeSpan DeployTime = TimeSpan.FromSeconds(20);
    private const int CommitLimit = 5;

    // One fetch at a time: the card polls every few seconds and git locks the copy it writes to.
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static JsonObject Definition(JsonObject meta) => new()
    {
        ["name"] = Tool,
        ["title"] = "Show pipeline",
        ["description"] = "Show where the project's newest push is in Home's pipeline: DEMO deploys it right away, PROD after IT approves. Call it after pushing. The card in the chat keeps itself up to date.",
        ["inputSchema"] = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["project"] = new JsonObject { ["type"] = "string", ["description"] = "The project as Home names it, \"Solution / Project\". Defaults to the project of the key in use." },
            },
        },
        ["annotations"] = new JsonObject { ["readOnlyHint"] = true, ["destructiveHint"] = false, ["openWorldHint"] = false },
        ["_meta"] = meta,
    };

    public static async Task<JsonObject> ResultAsync(IReadOnlyList<FoundryProjectChoice> projects, string? wanted, string? dataDirectory, HttpClient home, JsonObject meta, CancellationToken ct)
    {
        var project = projects.FirstOrDefault(p => string.Equals(p.Label, wanted, StringComparison.OrdinalIgnoreCase))
            ?? projects.FirstOrDefault(p => p.GitRemote is { Length: > 0 });
        var now = DateTimeOffset.UtcNow;
        var data = new JsonObject
        {
            ["project"] = project?.Label,
            ["now"] = now.ToString("O"),
            ["deploySeconds"] = (int)DeployTime.TotalSeconds,
        };

        string text;
        if (project?.GitRemote is not { Length: > 0 } remote)
        {
            data["error"] = "Projektet har intet repository i Home endnu.";
            text = "The person has no project with a repository in Home.";
        }
        else
        {
            data["repository"] = WithoutCredentials(remote);
            try
            {
                var commits = await ReadCommitsAsync(remote, dataDirectory, ct);
                data["commits"] = new JsonArray([.. commits.Select(c => (JsonNode)c.ToJson())]);
                var head = commits.FirstOrDefault();
                var live = head is not null && now - head.At >= DeployTime;
                data["demo"] = new JsonObject
                {
                    ["state"] = head is null ? "empty" : live ? "live" : "deploying",
                    ["commit"] = head?.ToJson(),
                    ["liveAt"] = head is null ? null : (head.At + DeployTime).ToString("O"),
                };
                var release = await ReadProdAsync(home, project.PipelineUrl, ct);
                data["prod"] = release?.ToJson(head) ?? new JsonObject { ["state"] = head is null ? "empty" : "waiting" };
                text = head is null
                    ? $"Nothing is pushed to {project.Label} yet."
                    : $"{project.Label}: DEMO {(live ? "runs" : "is deploying")} {head.Short} \"{head.Message}\". {release?.Describe(head, now) ?? "PROD waits for IT to approve it in Home."} The card shows the person the pipeline and keeps itself up to date, so there is no need to repeat it beyond a short answer.";
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or OperationCanceledException && !ct.IsCancellationRequested)
            {
                data["error"] = "Pipelinen kan ikke læses lige nu.";
                text = $"The pipeline of {project.Label} could not be read: {ex.Message}";
            }
        }

        return new JsonObject
        {
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
            ["structuredContent"] = data,
            ["_meta"] = meta,
        };
    }

    // What Home approved for PROD. When Home cannot be asked, the card says PROD waits, as before.
    private sealed record Release(string Commit, string Message, string ApprovedBy, DateTimeOffset ApprovedOn, string? Domain)
    {
        private string Short => Commit.Length > 7 ? Commit[..7] : Commit;
        private string? Url => Domain is { Length: > 0 } d ? $"https://{d}/" : null;
        private DateTimeOffset LiveAt => ApprovedOn + DeployTime;

        public JsonObject ToJson(Commit? head) => new()
        {
            ["state"] = "approved",
            ["commit"] = new JsonObject { ["hash"] = Commit, ["short"] = Short, ["message"] = Message },
            ["approvedBy"] = ApprovedBy,
            ["approvedAt"] = ApprovedOn.ToString("O"),
            ["liveAt"] = LiveAt.ToString("O"),
            ["url"] = Url,
            ["newer"] = head is not null && !string.Equals(head.Hash, Commit, StringComparison.OrdinalIgnoreCase),
        };

        public string Describe(Commit head, DateTimeOffset now)
        {
            var at = Url is { } u ? $" at {u}" : "";
            var where = now >= LiveAt
                ? $"PROD runs {Short} \"{Message}\"{at}, approved by {ApprovedBy}."
                : $"{ApprovedBy} approved {Short} \"{Message}\" for PROD, and it is deploying{at}.";
            return string.Equals(head.Hash, Commit, StringComparison.OrdinalIgnoreCase)
                ? where
                : $"{where} The newer {head.Short} waits for IT to approve it.";
        }
    }

    private static async Task<Release?> ReadProdAsync(HttpClient home, string? pipelineUrl, CancellationToken ct)
    {
        if (pipelineUrl is not { Length: > 0 }) return null;
        try
        {
            var status = await home.GetFromJsonAsync<JsonObject>(pipelineUrl, ct);
            return status?["prod"] is JsonObject prod
                ? new Release(
                    prod["commit"]!.GetValue<string>(),
                    prod["message"]?.GetValue<string>() ?? "",
                    prod["approvedBy"]?.GetValue<string>() ?? "IT",
                    prod["approvedOn"]!.GetValue<DateTimeOffset>(),
                    status["domain"]?.GetValue<string>())
                : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or InvalidOperationException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    private sealed record Commit(string Hash, string Message, string Author, DateTimeOffset At)
    {
        public string Short => Hash.Length > 7 ? Hash[..7] : Hash;

        public JsonObject ToJson() => new()
        {
            ["hash"] = Hash, ["short"] = Short, ["message"] = Message, ["author"] = Author, ["at"] = At.ToString("O"),
        };
    }

    private static async Task<IReadOnlyList<Commit>> ReadCommitsAsync(string remote, string? dataDirectory, CancellationToken ct)
    {
        var name = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(remote)))[..16].ToLowerInvariant();
        var copy = Path.Combine(dataDirectory is { Length: > 0 } d ? d : Path.GetTempPath(), "pipelines", name);
        await Gate.WaitAsync(ct);
        try
        {
            if (!Directory.Exists(Path.Combine(copy, "objects")))
            {
                Directory.CreateDirectory(copy);
                await GitAsync(copy, ct, "init", "--bare", "-q");
            }

            // The whole history: the house git server does not serve shallow fetches. After the first, a fetch brings only what is new.
            var (fetched, error) = await GitAsync(copy, ct, "fetch", "-q", remote, "+refs/heads/main:refs/heads/main");
            // Nobody has pushed to main yet.
            if (!fetched) return error.Contains("couldn't find remote ref", StringComparison.OrdinalIgnoreCase) ? [] : throw new InvalidOperationException(error.Trim());

            var (_, log) = await GitAsync(copy, ct, "log", "-n", CommitLimit.ToString(System.Globalization.CultureInfo.InvariantCulture), "--format=%H%x1f%s%x1f%an%x1f%cI", "main");
            return [.. log.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Split('\x1f'))
                .Where(p => p.Length == 4)
                .Select(p => new Commit(p[0], p[1], p[2], DateTimeOffset.Parse(p[3], System.Globalization.CultureInfo.InvariantCulture)))];
        }
        finally
        {
            Gate.Release();
        }
    }

    // Never a prompt and never a stored credential: the remote carries its own token.
    private static async Task<(bool Ok, string Output)> GitAsync(string directory, CancellationToken ct, params string[] args)
    {
        var start = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = directory };
        foreach (var a in new[] { "-c", "credential.helper=" }.Concat(args)) start.ArgumentList.Add(a);
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        using var process = Process.Start(start) ?? throw new InvalidOperationException("git did not start.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }
        return process.ExitCode == 0 ? (true, await stdout) : (false, await stderr);
    }

    private static string WithoutCredentials(string remote) =>
        System.Uri.TryCreate(remote, UriKind.Absolute, out var u) && u.UserInfo.Length > 0
            ? new UriBuilder(u) { UserName = "", Password = "" }.Uri.ToString()
            : remote;

    public const string Html = """
<!doctype html>
<html lang="da">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Pipeline</title>
<style>
  :root {
    --bg: #ffffff; --card: #f7f7f8; --line: #e4e4e7; --text: #18181b; --muted: #71717a;
    --accent: #2563eb; --accent-soft: #dbeafe; --ok: #16a34a; --ok-soft: #dcfce7; --wait: #a16207; --wait-soft: #fef9c3;
    color-scheme: light;
  }
  @media (prefers-color-scheme: dark) {
    :root:not([data-theme="light"]) {
      --bg: #18181b; --card: #222226; --line: #34343a; --text: #f4f4f5; --muted: #a1a1aa;
      --accent: #60a5fa; --accent-soft: #1e293b; --ok: #4ade80; --ok-soft: #14301f; --wait: #facc15; --wait-soft: #2f2a12; color-scheme: dark;
    }
  }
  :root[data-theme="dark"] {
    --bg: #18181b; --card: #222226; --line: #34343a; --text: #f4f4f5; --muted: #a1a1aa;
    --accent: #60a5fa; --accent-soft: #1e293b; --ok: #4ade80; --ok-soft: #14301f; --wait: #facc15; --wait-soft: #2f2a12; color-scheme: dark;
  }
  * { box-sizing: border-box; }
  html, body { margin: 0; background: var(--bg); color: var(--text);
    font: 14px/1.45 var(--font-sans, ui-sans-serif, system-ui, -apple-system, "Segoe UI", Roboto, sans-serif); }
  .wrap { padding: 16px; }
  .head { display: flex; align-items: center; gap: 10px; }
  .mark { width: 28px; height: 28px; border-radius: 8px; background: var(--accent); color: #fff;
    display: grid; place-items: center; font-weight: 700; font-size: 13px; flex: none; }
  h1 { font-size: 15px; margin: 0; font-weight: 600; flex: 1; min-width: 0; }
  h1 span { color: var(--muted); font-weight: 400; }
  button.refresh { all: unset; cursor: pointer; font-size: 12px; color: var(--muted); padding: 4px 8px;
    border: 1px solid var(--line); border-radius: 6px; white-space: nowrap; }
  button.refresh:hover, button.refresh:focus-visible { color: var(--text); border-color: var(--accent); }
  button.refresh[aria-busy="true"] { opacity: .6; }
  .stages { display: grid; grid-template-columns: 1fr auto 1fr; align-items: stretch; gap: 8px; margin: 14px 0 12px; }
  .arrow { align-self: center; color: var(--muted); font-size: 18px; }
  .stage { border: 1px solid var(--line); border-radius: 10px; background: var(--card); padding: 12px 14px; min-width: 0; }
  .stage .env { font-size: 11px; font-weight: 700; letter-spacing: .06em; color: var(--muted); }
  .stage .state { display: flex; align-items: center; gap: 7px; font-weight: 600; margin-top: 4px; }
  .stage .note { color: var(--muted); font-size: 12px; margin-top: 2px; }
  .dot { width: 9px; height: 9px; border-radius: 50%; background: var(--muted); flex: none; }
  .deploying { border-color: var(--accent); background: var(--accent-soft); }
  .deploying .dot { background: var(--accent); animation: pulse 1s ease-in-out infinite; }
  .live { border-color: var(--ok); background: var(--ok-soft); }
  .live .dot { background: var(--ok); }
  .waiting .dot { background: var(--wait); }
  .waiting .state { color: var(--wait); }
  .bar { height: 4px; border-radius: 2px; background: var(--line); margin-top: 8px; overflow: hidden; }
  .bar i { display: block; height: 100%; width: 0; background: var(--accent); transition: width .9s linear; }
  @keyframes pulse { 50% { opacity: .35; } }
  @media (prefers-reduced-motion: reduce) { .deploying .dot { animation: none; } .bar i { transition: none; } }
  .stage a.open { all: unset; cursor: pointer; display: inline-block; margin-top: 8px; font-size: 13px; font-weight: 600;
    color: var(--ok); text-decoration: underline; text-underline-offset: 2px; overflow-wrap: anywhere; }
  .stage a.open:focus-visible { outline: 2px solid var(--ok); outline-offset: 2px; border-radius: 2px; }
  .commits { display: grid; gap: 4px; }
  .commit { display: flex; gap: 10px; align-items: baseline; font-size: 13px; min-width: 0; }
  .commit code { font: 12px ui-monospace, SFMono-Regular, Menlo, Consolas, monospace; color: var(--accent); flex: none; }
  .commit .msg { flex: 1; min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  .commit .when { color: var(--muted); font-size: 12px; flex: none; }
  .commit.old { color: var(--muted); }
  .foot { color: var(--muted); font-size: 12px; margin-top: 12px; }
  .empty { color: var(--muted); font-size: 13px; padding-top: 10px; }
  @media (max-width: 420px) { .stages { grid-template-columns: 1fr; } .arrow { transform: rotate(90deg); justify-self: center; } }
</style>
</head>
<body>
<div class="wrap">
  <div class="head">
    <div class="mark">H</div>
    <h1>Pipeline <span id="project"></span></h1>
    <button class="refresh" id="refresh" type="button">Opdater</button>
  </div>
  <div id="body"><div class="empty">Henter pipelinen…</div></div>
  <div class="foot" id="foot"></div>
</div>
<script>
(() => {
  const body = document.getElementById('body');
  const foot = document.getElementById('foot');
  const refreshButton = document.getElementById('refresh');
  let nextId = 1, data = null, offset = 0, fetchedAt = null, canCall = true, timer = null;
  const pending = new Map();

  // Codex Desktop copies the page's console warnings into its own log, which is the only place a failed handshake shows.
  function trace(what, detail) { console.warn('[home-pipeline] ' + what + (detail === undefined ? '' : ' ' + JSON.stringify(detail))); }
  trace('script started', { openai: !!window.openai, framed: window.parent !== window });

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

  // The server's clock decides when DEMO is live, so the card counts on the server's time, not the machine's.
  const serverNow = () => Date.now() + offset;

  function ago(iso) {
    const s = Math.max(0, Math.round((serverNow() - Date.parse(iso)) / 1000));
    if (s < 60) return 'for ' + s + ' sek. siden';
    if (s < 3600) return 'for ' + Math.round(s / 60) + ' min. siden';
    if (s < 86400) return 'for ' + Math.round(s / 3600) + ' t. siden';
    return new Date(iso).toLocaleDateString('da-DK');
  }

  function el(tag, cls, text) { const e = document.createElement(tag); if (cls) e.className = cls; if (text !== undefined) e.textContent = text; return e; }

  function stage(env, cls, state, note, progress, extra) {
    const s = el('div', 'stage ' + cls);
    s.appendChild(el('div', 'env', env));
    const st = el('div', 'state'); st.appendChild(el('span', 'dot')); st.appendChild(document.createTextNode(state)); s.appendChild(st);
    if (note) s.appendChild(el('div', 'note', note));
    if (progress !== undefined) { const b = el('div', 'bar'); const i = el('i'); i.style.width = Math.round(progress * 100) + '%'; b.appendChild(i); s.appendChild(b); }
    if (extra) s.appendChild(extra);
    return s;
  }

  // Deploying until the server's clock passes liveAt, so the card moves on its own between fetches.
  function stateOf(env) {
    if (!env || !env.liveAt) return env && env.state === 'waiting' ? 'waiting' : 'empty';
    return serverNow() >= Date.parse(env.liveAt) ? 'live' : 'deploying';
  }
  const demoState = () => stateOf(data && data.demo);
  const prodState = () => stateOf(data && data.prod);
  const secondsTo = (iso) => Math.max(0, Math.ceil((Date.parse(iso) - serverNow()) / 1000));

  // The page sits in a sandbox, so the host opens the link: MCP Apps first, then the Apps SDK, then the page itself.
  function openLink(url) {
    request('ui/open-link', { url }).catch((e) => {
      trace('ui/open-link failed', e);
      if (window.openai && window.openai.openExternal) window.openai.openExternal({ href: url });
      else window.open(url, '_blank', 'noopener');
    });
  }

  function link(url) {
    const a = el('a', 'open', 'Åbn ' + url.replace(/^https?:\/\//, '').replace(/\/$/, '') + ' ↗');
    a.href = url; a.target = '_blank'; a.rel = 'noopener';
    a.addEventListener('click', (e) => { e.preventDefault(); openLink(url); });
    return a;
  }

  function prodStage() {
    const p = data.prod, state = prodState();
    if (state !== 'live' && state !== 'deploying') return stage('PROD', 'waiting', 'Venter på IT', 'IT godkender den i Home');
    const newer = p.newer ? ' · nyere ændring venter på IT' : '';
    if (state === 'deploying') {
      const left = secondsTo(p.liveAt);
      return stage('PROD', 'deploying', 'Udrulles…', 'Godkendt af ' + p.approvedBy + ' · klar om ca. ' + left + ' sek.', 1 - left / (data.deploySeconds || 20));
    }
    return stage('PROD', 'live', 'Kører', p.commit.short + ' · godkendt af ' + p.approvedBy + newer, undefined, p.url ? link(p.url) : null);
  }

  function render() {
    document.getElementById('project').textContent = data && data.project ? '· ' + data.project : '';
    body.textContent = '';
    if (!data) { body.appendChild(el('div', 'empty', 'Henter pipelinen…')); return; }
    if (data.error) { body.appendChild(el('div', 'empty', data.error)); return; }
    const demo = demoState();
    if (demo === 'empty') { body.appendChild(el('div', 'empty', 'Der er ikke pushet noget til projektet endnu.')); return; }

    const head = data.demo.commit;
    const stages = el('div', 'stages');
    if (demo === 'live') {
      stages.appendChild(stage('DEMO', 'live', 'Kører', head.short + ' er udrullet'));
    } else {
      const left = secondsTo(data.demo.liveAt);
      stages.appendChild(stage('DEMO', 'deploying', 'Udrulles…', 'Klar om ca. ' + left + ' sek.', 1 - left / (data.deploySeconds || 20)));
    }
    stages.appendChild(el('div', 'arrow', '→'));
    stages.appendChild(prodStage());
    body.appendChild(stages);

    const list = el('div', 'commits');
    (data.commits || []).slice(0, 3).forEach((c, i) => {
      const row = el('div', 'commit' + (i > 0 ? ' old' : ''));
      row.appendChild(el('code', '', c.short));
      row.appendChild(el('span', 'msg', c.message));
      row.appendChild(el('span', 'when', ago(c.at)));
      list.appendChild(row);
    });
    body.appendChild(list);
  }

  function renderFoot() {
    if (!fetchedAt) { foot.textContent = ''; return; }
    const at = fetchedAt.toLocaleTimeString('da-DK', { hour: '2-digit', minute: '2-digit', second: '2-digit' });
    foot.textContent = 'Opdateret ' + at + (canCall ? ' · opdaterer sig selv' : ' · tryk Opdater for at hente igen');
  }

  // Codex hands the first result over again whenever its globals change, a resize included, so an
  // answer older than the one shown is dropped rather than turning the card back.
  function apply(result, source) {
    const d = result && (result.structuredContent || (result._meta && result._meta['home/pipeline']));
    if (!d) { trace('no data from ' + source, { keys: result && Object.keys(result) }); return; }
    if (data && data.now && d.now && Date.parse(d.now) < Date.parse(data.now)) return;
    if (!data || (data.prod && data.prod.state) !== (d.prod && d.prod.state)) trace('prod from ' + source, d.prod && d.prod.state);
    data = d;
    if (d.now) offset = Date.parse(d.now) - Date.now();
    fetchedAt = new Date();
    render(); renderFoot(); schedule();
  }

  // Ask the server again through the host. While DEMO deploys, often; once it runs, now and then.
  async function refresh() {
    refreshButton.setAttribute('aria-busy', 'true');
    try {
      apply(await request('tools/call', { name: 'show_pipeline', arguments: data && data.project ? { project: data.project } : {} }), 'tools/call');
      if (!canCall) trace('tools/call ok again');
      canCall = true;
    } catch (e) {
      trace('tools/call failed', e);
      canCall = false;
      renderFoot();
    } finally {
      refreshButton.removeAttribute('aria-busy');
    }
  }
  refreshButton.addEventListener('click', refresh);

  function schedule() {
    clearTimeout(timer);
    if (!canCall) return;
    // Waiting for IT is when a person is most likely watching, so that is asked about often too.
    const busy = demoState() === 'deploying' || prodState() !== 'live';
    timer = setTimeout(refresh, busy ? 3000 : 30000);
  }

  // The deploy counts down on its own between fetches, so the card moves even when the host lets it fetch nothing.
  setInterval(() => { if (data && !data.error) render(); }, 1000);

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
      apply(m.params, 'tool-result');
    } else if (m.method === 'ui/notifications/host-context-changed') {
      applyTheme(m.params);
    } else if (m.method === 'ui/resource-teardown' && m.id !== undefined) {
      clearTimeout(timer); canCall = false;
      post({ jsonrpc: '2.0', id: m.id, result: {} });
    }
  });

  // Apps SDK hosts hand over the result as globals, and say when they change.
  function fromOpenAi() {
    const o = window.openai;
    if (!o) return;
    if (o.theme) document.documentElement.dataset.theme = o.theme;
    if (o.toolOutput) apply({ structuredContent: o.toolOutput }, 'openai');
  }
  window.addEventListener('openai:set_globals', fromOpenAi);
  fromOpenAi();

  request('ui/initialize', {
    protocolVersion: '2026-01-26',
    appCapabilities: { availableDisplayModes: ['inline', 'fullscreen'] },
    // appInfo is the current name; Codex Desktop 26.1027 rejects a request without it. clientInfo is the older one.
    appInfo: { name: 'home-pipeline', version: '1.0.0' },
    clientInfo: { name: 'home-pipeline', version: '1.0.0' },
  }).then((result) => {
    applyTheme(result && result.hostContext);
    notify('ui/notifications/initialized', {});
    resize();
  }, (error) => trace('ui/initialize failed', error));
})();
</script>
</body>
</html>
""";
}
