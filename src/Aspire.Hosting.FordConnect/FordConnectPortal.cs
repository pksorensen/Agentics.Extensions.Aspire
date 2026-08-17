using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using System.Net;
using System.Text;

namespace Aspire.Hosting.FordConnect.Emulator;

/// <summary>
/// A dashboard for the emulated car, and the consent page the authorize hop lands on.
///
/// It is a control surface, not a product. The reason it exists is that the behaviour
/// worth developing against — a car that goes quiet for 40 minutes and then reports
/// three times in five — cannot be reached by waiting. Turn the ignition on, drive ten
/// kilometres, plug in, and the next read answers accordingly.
///
/// It also shows the shared rate-limit bucket, which is otherwise invisible: Ford sends
/// no headers, so in production the only evidence is a 429 that has already cost you a
/// scheduled read.
/// </summary>
public static class FordConnectPortal
{
    public static IEndpointRouteBuilder MapFordConnectPortal(this IEndpointRouteBuilder app)
    {
        app.MapGet("/", (FordConnectState state) =>
            Results.Content(Dashboard(state), "text/html; charset=utf-8"));

        app.MapPost("/emulator/car/{vin}", async (string vin, HttpRequest req, FordConnectState state) =>
        {
            var car = state.Car(vin);
            if (car is null) return Results.NotFound();
            var form = await req.ReadFormAsync();

            switch (form["action"].ToString())
            {
                case "ignition":
                    car.Set(form["value"].ToString(), null, null, null);
                    break;
                case "plug":
                    var plugged = form["value"] == "CONNECTED";
                    car.Set(null, form["value"].ToString(), plugged ? "NotReady" : "NotReady", null);
                    break;
                case "charge":
                    // Plug first: a charge cannot start without a cable, and letting it
                    // would model a car that does not exist.
                    if (form["value"] == "ChargingAC") car.Set(null, "CONNECTED", "ChargingAC", null);
                    else car.Set(null, null, form["value"].ToString(), null);
                    break;
                case "soc":
                    if (double.TryParse(form["value"].ToString(), out var soc)) car.Set(null, null, null, soc);
                    break;
                case "drive":
                    if (double.TryParse(form["value"].ToString(), out var km)) car.Drive(km);
                    break;
                case "report":
                    // Force the car to consider itself due, without changing anything.
                    car.Set(null, null, null, null);
                    break;
            }

            return Results.Redirect("/");
        });

        app.MapPost("/emulator/bucket", (HttpRequest req, FordConnectState state) =>
        {
            if (req.Form["action"] == "revoke") state.Revoke();
            else state.Bucket.Refill();
            return Results.Redirect("/");
        });

        return app;
    }

    /// <summary>The consent page. Ford has a whole login journey here; locally the
    /// useful thing is a list of owners and two buttons, so a link can be granted — and
    /// refused, which is the outcome most clients forget to handle.</summary>
    public static string ConsentPage(FordConnectState state, string redirectUri, string stateParam)
    {
        var owners = new StringBuilder();
        foreach (var owner in state.Owners)
        {
            owners.Append($"""
                <label class="owner">
                  <input type="radio" name="owner" value="{H(owner.Id)}"{(owner == state.Owners[0] ? " checked" : "")}>
                  <span><strong>{H(owner.DisplayName)}</strong><br><small>{H(owner.Email)}</small></span>
                </label>
                """);
        }

        var cars = string.Join("", state.Cars.Select(car =>
            $"<li>{H(car.Seed.ModelName)} {H(car.Seed.ModelYear)} — <code>{H(car.Vin)}</code></li>"));

        return Page("Giv adgang til din Ford", $"""
            <div class="card">
              <p class="eyebrow">Emuleret FordConnect</p>
              <h1>Del din bils data</h1>
              <p>Dette er <strong>ikke</strong> Ford. Det er en lokal emulator, og der er ingen
                 adgangskode — vælg en ejer og tryk igennem.</p>
              <form method="post" action="/emulator/consent">
                <input type="hidden" name="redirect_uri" value="{H(redirectUri)}">
                <input type="hidden" name="state" value="{H(stateParam)}">
                {owners}
                <p class="muted">Appen får adgang til:</p>
                <ul>{cars}</ul>
                <div class="row">
                  <button name="decision" value="allow" class="primary">Tillad</button>
                  <button name="decision" value="deny" class="ghost">Afvis</button>
                </div>
              </form>
              <p class="muted small">Sender tilbage til <code>{H(redirectUri)}</code>. Et afvist
                 samtykke kommer tilbage som <code>error=access_denied</code> på samme adresse —
                 ikke som en fejlside, hvilket er den gren de fleste klienter glemmer.</p>
            </div>
            """);
    }

    private static string Dashboard(FordConnectState state)
    {
        var body = new StringBuilder();
        var b = state.Seed.Behaviour;

        body.Append($"""
            <div class="card">
              <p class="eyebrow">Emuleret FordConnect</p>
              <h1>Bilen og spanden</h1>
              <p class="muted">Alle standardværdier her er <em>målt</em> på en rigtig Mach-E i august
                 2026. En emulator der er mildere end virkeligheden lærer ingen noget.</p>
              <div class="grid">
                <div class="stat"><span>{state.Bucket.TokensLeft}</span><label>kald tilbage i den <strong>fælles</strong> spand</label></div>
                <div class="stat"><span>{b.RateLimitRefillMs / 1000}s</span><label>genopfyldning, akkumulerer {(b.RateLimitAccumulates ? "ja" : "<strong>ikke</strong>")}</label></div>
                <div class="stat"><span>{state.Bucket.WaitMs / 1000}s</span><label>til næste kald er tilladt</label></div>
                <div class="stat"><span>{b.AccessTokenSeconds / 60} min</span><label>access token levetid</label></div>
              </div>
              <form method="post" action="/emulator/bucket" class="row">
                <button name="action" value="refill" class="ghost">Fyld spanden</button>
                <button name="action" value="revoke" class="ghost">Træk samtykket tilbage</button>
              </form>
              <p class="muted small">Ford sender <strong>ingen</strong> rate-limit-headers — heller ikke
                 <code>Retry-After</code>. Tallene ovenfor findes kun her; i produktion er det eneste
                 bevis en 429 der allerede har kostet en planlagt læsning.</p>
            </div>
            """);

        if (state.Replay is { } replay)
        {
            // No controls. There is nothing to drive — the driving already happened, and
            // a button that pretended otherwise would be the one thing a replay must not
            // offer: a way to change the recording.
            var position = replay.Position;
            var span = replay.LogEnd - replay.LogStart;
            var cars = string.Join("", state.Cars.Select(car =>
            {
                var (played, total) = replay.Progress(car.Vin);
                return $"<li><code>{H(car.Vin)}</code> — {played} af {total} læsninger afspillet</li>";
            }));

            body.Append($"""
                <div class="card">
                  <h2>Afspilning</h2>
                  <p class="muted">Optagede payloads, byte for byte, med de pauser de kom i. Intet
                     tidsstempel er skrevet om — en afspilning der friskede dem op ville være en
                     simulator med ekstra trin.</p>
                  <div class="grid">
                    <div class="stat"><span>{replay.RowCount}</span><label>læsninger i loggen</label></div>
                    <div class="stat"><span>{span.TotalHours:0.0} t</span><label>logget tidsrum</label></div>
                    <div class="stat"><span>{replay.Speed:0.##}×</span><label>hastighed</label></div>
                    <div class="stat"><span>{(position is null ? "—" : position.Value.ToString("HH:mm:ss"))}</span><label>afspilningen står her (logtid)</label></div>
                  </div>
                  <ul>{cars}</ul>
                  <p class="muted small">Uret starter ved <em>første</em> kald, ikke ved opstart, så en
                     langsom AppHost-start æder ikke de første minutter. Slutningen holder — den
                     looper ikke, for et loop ville køre kilometertælleren baglæns, og så knækker en
                     klient på en fiktion i stedet for på noget Ford gør.</p>
                </div>
                """);
        }

        foreach (var car in state.Cars)
        {
            if (state.Replay is not null) continue;
            var awake = car.Awake;
            var until = (int)Math.Max(0, (car.NextReportAt - DateTimeOffset.UtcNow).TotalSeconds);
            body.Append($"""
                <div class="card">
                  <h2>{H(car.Seed.ModelName)} <small>{H(car.Vin)}</small></h2>
                  <div class="grid">
                    <div class="stat"><span>{car.StateOfCharge:0.0}%</span><label>traktionsbatteri</label></div>
                    <div class="stat"><span>{car.Odometer:0}</span><label>km</label></div>
                    <div class="stat"><span>{H(car.Ignition)}</span><label>tænding</label></div>
                    <div class="stat"><span>{H(car.ChargeStatus)}</span><label>ladning ({H(car.Plug)})</label></div>
                  </div>
                  <p class="{(awake ? "awake" : "asleep")}">
                    {(awake ? "Vågen" : "Parkeret")} — rapporterer om <strong>{until}s</strong>.
                    {(awake
                        ? "Vågen betyder 28-150 sekunder mellem rapporter."
                        : "Parkeret betyder 30-45 minutter. Det er den målte adfærd, og grunden til at hurtigere polling ikke giver mere data.")}
                  </p>
                  <form method="post" action="/emulator/car/{H(car.Vin)}" class="row">
                    <button name="action" value="ignition" class="ghost">Tænding</button>
                    <input type="hidden" name="value" value="{(car.Ignition == "ON" ? "OFF" : "ON")}">
                  </form>
                  <form method="post" action="/emulator/car/{H(car.Vin)}" class="row">
                    <input type="hidden" name="action" value="drive">
                    <button name="value" value="1" class="ghost">Kør 1 km</button>
                    <button name="value" value="10" class="ghost">Kør 10 km</button>
                    <button name="value" value="50" class="ghost">Kør 50 km</button>
                  </form>
                  <form method="post" action="/emulator/car/{H(car.Vin)}" class="row">
                    <input type="hidden" name="action" value="plug">
                    <button name="value" value="CONNECTED" class="ghost">Sæt kabel i</button>
                    <button name="value" value="DISCONNECTED" class="ghost">Tag kabel ud</button>
                  </form>
                  <form method="post" action="/emulator/car/{H(car.Vin)}" class="row">
                    <input type="hidden" name="action" value="charge">
                    <button name="value" value="ChargingAC" class="primary">Start ladning</button>
                    <button name="value" value="NotReady" class="ghost">Stop ladning</button>
                  </form>
                  <p class="muted small">Position: {car.Latitude:0.0000}, {car.Longitude:0.0000} —
                     og den ligger i telemetrien, som den gør hos Ford. Hvert signal har sit eget
                     tidsstempel, og konvolutten har sit: den ligger <em>efter</em> signalerne inde i
                     sig (målt: foran dem alle i 8 af 12 læsninger) og flytter sig af og til helt
                     alene — målt én gang ud af 12. Ikke ved hvert kald; det ville 204 kald der blev
                     12 læsninger modbevise. Deduplikér på metrikkerne.</p>
                </div>
                """);
        }

        var rows = new StringBuilder();
        foreach (var call in state.RecentCalls)
        {
            var cls = call.Status switch { 200 => "ok", 429 => "limited", _ => "bad" };
            rows.Append($"""
                <tr class="{cls}">
                  <td>{call.At:HH:mm:ss}</td>
                  <td><code>{H(call.Endpoint)}</code></td>
                  <td>{call.Status}</td>
                  <td>{call.TokensLeft}</td>
                  <td>{H(call.Note ?? "")}</td>
                </tr>
                """);
        }

        body.Append($"""
            <div class="card">
              <h2>Kald</h2>
              <p class="muted small">Alle Query-endpoints trækker fra <strong>samme</strong> spand. Det er
                 den vigtigste målte kendsgerning her: hamrer man kun på <code>wallbox</code> til 429,
                 svarer <code>telemetry</code>, <code>alerts</code> og <code>garage</code> også 429 på
                 deres første kald. Én kadence pr. endpoint giver derfor ikke mere data — de stjæler
                 fra hinanden.</p>
              <table>
                <thead><tr><th>Tid</th><th>Endpoint</th><th>Status</th><th>Tilbage</th><th></th></tr></thead>
                <tbody>{(rows.Length > 0 ? rows.ToString() : "<tr><td colspan=5 class=muted>ingen kald endnu</td></tr>")}</tbody>
              </table>
            </div>
            """);

        return Page("FordConnect-emulator", body.ToString());
    }

    private static string H(string? value) => WebUtility.HtmlEncode(value ?? "");

    private static string Page(string title, string body) => $$"""
        <!doctype html>
        <html lang="da">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <title>{{H(title)}}</title>
        <style>
          :root {
            --ground: #f6f5f3; --card: #ffffff; --ink: #1b1a18; --muted: #6b6864;
            --line: #e3e0db; --accent: #00618f; --ok: #1f7a4d; --warn: #b45309; --bad: #a3261f;
          }
          @media (prefers-color-scheme: dark) {
            :root {
              --ground: #17181a; --card: #1f2124; --ink: #eceae7; --muted: #9a9691;
              --line: #303337; --accent: #4aa8d8; --ok: #4ec489; --warn: #e0a355; --bad: #e07a72;
            }
          }
          * { box-sizing: border-box; }
          body { margin: 0; padding: 2rem 1rem; background: var(--ground); color: var(--ink);
                 font: 15px/1.55 ui-sans-serif, system-ui, -apple-system, sans-serif; }
          main { max-width: 54rem; margin: 0 auto; display: flex; flex-direction: column; gap: 1.25rem; }
          .card { background: var(--card); border: 1px solid var(--line); border-radius: 10px;
                   padding: 1.25rem 1.4rem; }
          h1 { font-size: 1.5rem; margin: .2rem 0 .6rem; }
          h2 { font-size: 1.15rem; margin: 0 0 .8rem; }
          h2 small { font-weight: 400; color: var(--muted); font-family: ui-monospace, monospace; }
          .eyebrow { text-transform: uppercase; letter-spacing: .09em; font-size: .7rem;
                      color: var(--accent); margin: 0; font-weight: 600; }
          .muted { color: var(--muted); }
          .small { font-size: .82rem; }
          .grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(9rem, 1fr));
                   gap: .75rem; margin: 1rem 0; }
          .stat { border: 1px solid var(--line); border-radius: 8px; padding: .7rem .8rem; }
          .stat span { display: block; font-size: 1.4rem; font-weight: 600;
                        font-variant-numeric: tabular-nums; }
          .stat label { display: block; font-size: .76rem; color: var(--muted); margin-top: .2rem; }
          .row { display: flex; flex-wrap: wrap; gap: .5rem; align-items: center; margin: .5rem 0 0; }
          button { font: inherit; padding: .45rem .9rem; border-radius: 7px; cursor: pointer;
                    border: 1px solid var(--line); background: transparent; color: var(--ink); }
          button.primary { background: var(--accent); border-color: var(--accent); color: #fff; }
          button:hover { border-color: var(--accent); }
          button:focus-visible { outline: 2px solid var(--accent); outline-offset: 2px; }
          .owner { display: flex; gap: .6rem; align-items: center; border: 1px solid var(--line);
                    border-radius: 8px; padding: .6rem .8rem; margin: .5rem 0; cursor: pointer; }
          .awake { color: var(--ok); }
          .asleep { color: var(--muted); }
          table { width: 100%; border-collapse: collapse; font-size: .85rem; }
          th, td { text-align: left; padding: .35rem .5rem; border-bottom: 1px solid var(--line);
                    font-variant-numeric: tabular-nums; }
          tr.ok td:nth-child(3) { color: var(--ok); }
          tr.limited td:nth-child(3) { color: var(--warn); font-weight: 600; }
          tr.bad td:nth-child(3) { color: var(--bad); }
          code { font-family: ui-monospace, monospace; font-size: .9em; }
          ul { margin: .4rem 0; padding-left: 1.2rem; }
        </style>
        </head>
        <body><main>{{body}}</main></body>
        </html>
        """;
}
