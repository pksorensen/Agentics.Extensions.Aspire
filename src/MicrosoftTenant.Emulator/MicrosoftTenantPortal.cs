using Aspire.Hosting.AzureResourceManager.Emulator;
using Aspire.Hosting.MicrosoftGraph.Emulator;
using Aspire.Hosting.MicrosoftTenant.Emulator;
using System.Net;
using System.Text.Json;

internal static class MicrosoftTenantPortal
{
    public static void MapMicrosoftTenantPortal(this WebApplication app)
    {
        app.MapGet("/_emulator/state", (
            MicrosoftTenantState tenant,
            MicrosoftGraphState graph,
            AzureResourceManagerState arm) => Results.Ok(new
        {
            tenant = new
            {
                tenant.TenantId,
                tenant.PrimaryDomain,
            },
            graph = new
            {
                applications = graph.Applications.Values
                    .OrderBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
                    .Select(item => new
                    {
                        item.Id,
                        item.AppId,
                        item.DisplayName,
                        credentials = item.PasswordKeyIds.Count,
                    }),
                servicePrincipals = graph.ServicePrincipals.Values
                    .OrderBy(item => item.AppId, StringComparer.OrdinalIgnoreCase)
                    .Select(item => new
                    {
                        item.Id,
                        item.AppId,
                        item.AccountEnabled,
                    }),
            },
            arm = new
            {
                subscriptions = arm.Seed.Subscriptions.Select(subscription => new
                {
                    subscription.SubscriptionId,
                    resourceGroups = subscription.ResourceGroups.Select(group => new
                    {
                        group.Name,
                        group.Location,
                        communicationServices = group.CommunicationServices.Select(service => new
                        {
                            service.Name,
                            service.DataLocation,
                            linkedDomains = service.LinkedDomains,
                        }),
                    }),
                    customRoles = subscription.CustomRoles.Select(role => new
                    {
                        role.Name,
                        role.RoleDefinitionId,
                        role.Description,
                        role.Actions,
                        role.NotActions,
                        role.DataActions,
                        role.NotDataActions,
                        role.AssignableScopes,
                    }),
                }),
                resources = arm.Resources
                    .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(item => DescribeResource(item.Key, item.Value)),
            },
        }));

        app.MapGet("/", () => Results.Content(Page, "text/html; charset=utf-8"));
    }

    private static object DescribeResource(string id, JsonElement document)
    {
        var properties = document.TryGetProperty("properties", out var value) ? value : default;
        return new
        {
            id,
            type = ResourceType(id),
            properties = new
            {
                username = StringProperty(properties, "username"),
                displayName = StringProperty(properties, "displayName"),
                entraApplicationId = StringProperty(properties, "entraApplicationId"),
                tenantId = StringProperty(properties, "tenantId"),
                principalId = StringProperty(properties, "principalId"),
                principalType = StringProperty(properties, "principalType"),
                roleDefinitionId = StringProperty(properties, "roleDefinitionId"),
            },
        };
    }

    private static string? StringProperty(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string ResourceType(string id)
    {
        if (id.Contains("/senderUsernames/", StringComparison.OrdinalIgnoreCase)) return "Sender username";
        if (id.Contains("/smtpUsernames/", StringComparison.OrdinalIgnoreCase)) return "SMTP username";
        if (id.Contains("/roleAssignments/", StringComparison.OrdinalIgnoreCase)) return "Role assignment";
        return "ARM resource";
    }

    private const string Page = """
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width,initial-scale=1">
  <title>Microsoft tenant emulator</title>
  <style>
    :root { color-scheme: dark; --bg:#08111f; --panel:#0f1c2e; --line:#24344c; --text:#e7edf7; --muted:#93a4bd; --blue:#61a7ff; --green:#58d6a9; --amber:#ffc766; }
    * { box-sizing:border-box } body { margin:0; background:radial-gradient(circle at 10% 0,#132945 0,transparent 34%),var(--bg); color:var(--text); font:14px/1.5 ui-sans-serif,system-ui,sans-serif }
    main { width:min(1180px,calc(100% - 32px)); margin:0 auto; padding:40px 0 64px }
    header { display:flex; align-items:flex-end; justify-content:space-between; gap:24px; margin-bottom:28px }
    h1 { margin:0; font-size:clamp(26px,4vw,42px); letter-spacing:-.04em } h2 { font-size:16px; margin:0 0 14px } p { margin:6px 0 0; color:var(--muted) }
    .pill { border:1px solid #276a58; background:#102c29; color:var(--green); padding:6px 10px; border-radius:999px; white-space:nowrap }
    .summary { display:grid; grid-template-columns:repeat(4,1fr); gap:12px; margin-bottom:18px }
    .metric,.card { background:color-mix(in srgb,var(--panel) 92%,transparent); border:1px solid var(--line); border-radius:14px; box-shadow:0 18px 50px #0003 }
    .metric { padding:16px } .metric b { display:block; font-size:25px; color:var(--blue) } .metric span { color:var(--muted); font-size:12px; text-transform:uppercase; letter-spacing:.08em }
    .grid { display:grid; grid-template-columns:1fr 1fr; gap:18px } .card { padding:18px; overflow:hidden } .wide { grid-column:1/-1 }
    .route { display:none; margin-bottom:18px; border-color:#356da8; background:linear-gradient(135deg,#122d4b,#0f1c2e) } .route.visible { display:block } .route h2 { color:var(--blue); font-size:20px } .route-grid { display:grid; grid-template-columns:repeat(2,minmax(0,1fr)); gap:10px 24px; margin-top:16px } .route-grid div { min-width:0 } .route-grid span { display:block; color:var(--muted); font-size:11px; text-transform:uppercase; letter-spacing:.08em }
    table { width:100%; border-collapse:collapse } th { color:var(--muted); font-size:11px; letter-spacing:.08em; text-transform:uppercase; text-align:left } td,th { padding:9px 8px; border-bottom:1px solid var(--line); vertical-align:top } tr:last-child td { border:0 }
    code { color:#b7d5ff; font:12px ui-monospace,SFMono-Regular,monospace; overflow-wrap:anywhere } .ok { color:var(--green) } .off { color:var(--amber) } .empty { color:var(--muted); padding:18px 8px }
    details { border-top:1px solid var(--line); padding:10px 0 } details:first-of-type { border-top:0 } summary { cursor:pointer; font-weight:650 } ul { margin:8px 0; padding-left:20px; color:var(--muted) }
    @media(max-width:760px){ .summary{grid-template-columns:1fr 1fr}.grid{grid-template-columns:1fr}.wide{grid-column:auto}header{align-items:flex-start;flex-direction:column} }
  </style>
</head>
<body><main>
  <header><div><h1>Microsoft tenant emulator</h1><p id="tenant">Loading tenant state…</p></div><span class="pill">Read-only portal</span></header>
  <article class="card route" id="route"></article>
  <section class="summary" id="summary"></section>
  <section class="grid">
    <article class="card"><h2>App registrations</h2><div id="apps"></div></article>
    <article class="card"><h2>Service principals</h2><div id="principals"></div></article>
    <article class="card"><h2>Azure resources</h2><div id="resources"></div></article>
    <article class="card"><h2>Custom roles</h2><div id="roles"></div></article>
    <article class="card wide"><h2>Provisioned ARM objects</h2><div id="objects"></div></article>
  </section>
</main><script>
const esc=v=>String(v??'').replace(/[&<>\"]/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','\"':'&quot;'}[c]));
const short=v=>v?`<code>${esc(v)}</code>`:'<span class="off">—</span>';
const table=(heads,rows)=>rows.length?`<table><thead><tr>${heads.map(x=>`<th>${x}</th>`).join('')}</tr></thead><tbody>${rows.join('')}</tbody></table>`:'<div class="empty">Nothing provisioned yet.</div>';
const routePath=()=>{
  let value=location.hash.slice(1).replace(/^\/+/, '');
  try { value=decodeURIComponent(value); } catch {}
  if(value.startsWith('@')) value=value.includes('/')?value.slice(value.indexOf('/')+1):'';
  return value;
};
const routeDetails=(title,description,fields)=>`<h2>${esc(title)}</h2><p>${esc(description)}</p><div class="route-grid">${fields.map(([label,value])=>`<div><span>${esc(label)}</span>${short(value)}</div>`).join('')}</div>`;
const renderRoute=s=>{
  const route=routePath(), card=document.querySelector('#route');
  if(!route){card.classList.remove('visible');card.innerHTML='';return;}

  let content;
  const principal=route.match(/Microsoft_AAD_IAM\/ManagedAppMenuBlade\/~\/([^/]+)\/objectId\/([^/]+)\/appId\/([^/]+)/i);
  const application=route.match(/Microsoft_AAD_RegisteredApps\/ApplicationMenuBlade\/~\/([^/]+)\/appId\/([^/]+)/i);
  if(principal){
    const [,blade,objectId,appId]=principal;
    const item=s.graph.servicePrincipals.find(x=>x.id.toLowerCase()===objectId.toLowerCase()||x.appId.toLowerCase()===appId.toLowerCase());
    content=routeDetails('Enterprise application',`${blade} · emulated service principal`,[['Application ID',item?.appId??appId],['Object ID',item?.id??objectId],['State',item?(item.accountEnabled?'Enabled':'Disabled'):'Not present in current emulator state']]);
  } else if(application){
    const [,blade,appId]=application;
    const item=s.graph.applications.find(x=>x.appId.toLowerCase()===appId.toLowerCase());
    content=routeDetails('App registration',`${blade} · emulated Microsoft Graph application`,[['Name',item?.displayName??'Not present in current emulator state'],['Application ID',item?.appId??appId],['Object ID',item?.id],['Credentials',item?.credentials]]);
  } else if(route.toLowerCase().startsWith('resource/')){
    let target=route.slice('resource'.length);
    const blade=target.match(/\/(email_smtp_username|overview|users)$/i)?.[1]??'overview';
    target=target.replace(/\/(email_smtp_username|overview|users)$/i,'');
    const related=s.arm.resources.filter(x=>x.id.toLowerCase()===target.toLowerCase()||x.id.toLowerCase().startsWith(`${target.toLowerCase()}/`));
    const kind=target.toLowerCase().includes('/communicationservices/')?'Communication Service':target.toLowerCase().includes('/emailservices/')?'Email domain':'Azure resource';
    content=routeDetails(kind,`${blade} · emulated Azure Resource Manager resource`,[['Resource ID',target],['Provisioned child objects',related.length],['Object types',related.map(x=>x.type).join(', ')||'None']]);
  }

  card.innerHTML=content??routeDetails('Azure portal route','No matching object exists in the emulated tenant.',[['Route',route]]);
  card.classList.add('visible');
};
let portalState;
fetch('/_emulator/state',{cache:'no-store'}).then(r=>r.json()).then(s=>{
  portalState=s;
  document.querySelector('#tenant').innerHTML=`${esc(s.tenant.primaryDomain)} · <code>${esc(s.tenant.tenantId)}</code>`;
  const groups=s.arm.subscriptions.flatMap(x=>x.resourceGroups), roles=s.arm.subscriptions.flatMap(x=>x.customRoles), services=groups.flatMap(x=>x.communicationServices);
  document.querySelector('#summary').innerHTML=[['Applications',s.graph.applications.length],['Subscriptions',s.arm.subscriptions.length],['Resource groups',groups.length],['ARM objects',s.arm.resources.length]].map(x=>`<div class="metric"><b>${x[1]}</b><span>${x[0]}</span></div>`).join('');
  document.querySelector('#apps').innerHTML=table(['Name','Client ID','Credentials'],s.graph.applications.map(a=>`<tr><td>${esc(a.displayName)}</td><td>${short(a.appId)}</td><td>${a.credentials}</td></tr>`));
  document.querySelector('#principals').innerHTML=table(['Application','Object ID','State'],s.graph.servicePrincipals.map(p=>`<tr><td>${short(p.appId)}</td><td>${short(p.id)}</td><td class="${p.accountEnabled?'ok':'off'}">${p.accountEnabled?'Enabled':'Disabled'}</td></tr>`));
  document.querySelector('#resources').innerHTML=s.arm.subscriptions.map(sub=>`<details open><summary>Subscription ${esc(sub.subscriptionId)}</summary>${sub.resourceGroups.map(g=>`<details open><summary>${esc(g.name)} <span class="off">${esc(g.location)}</span></summary><ul>${g.communicationServices.map(c=>`<li><b>${esc(c.name)}</b> · ${esc(c.dataLocation)}<br>${c.linkedDomains.map(d=>short(d.domain)).join(' ')}</li>`).join('')}</ul></details>`).join('')}</details>`).join('')||'<div class="empty">No subscriptions seeded.</div>';
  document.querySelector('#roles').innerHTML=roles.map(r=>`<details><summary>${esc(r.name)}</summary><p>${esc(r.description??'')}</p><p>${short(r.roleDefinitionId)}</p><ul>${r.actions.map(a=>`<li><code>${esc(a)}</code></li>`).join('')}${r.dataActions.map(a=>`<li><code>${esc(a)}</code> <span class="ok">data</span></li>`).join('')}${r.notActions.map(a=>`<li><code>${esc(a)}</code> <span class="off">excluded</span></li>`).join('')}${r.notDataActions.map(a=>`<li><code>${esc(a)}</code> <span class="off">data excluded</span></li>`).join('')}</ul></details>`).join('')||'<div class="empty">No custom roles seeded.</div>';
  document.querySelector('#objects').innerHTML=table(['Type','Resource ID','Details'],s.arm.resources.map(r=>{const p=Object.entries(r.properties).filter(x=>x[1]).map(x=>`${esc(x[0])}: ${esc(x[1])}`).join('<br>');return `<tr><td>${esc(r.type)}</td><td>${short(r.id)}</td><td><code>${p||'—'}</code></td></tr>`}));
  renderRoute(s);
}).catch(e=>document.querySelector('#tenant').textContent=`Could not load emulator state: ${e}`);
addEventListener('hashchange',()=>portalState&&renderRoute(portalState));
</script></body></html>
""";
}
