'use strict';
/* Document Organizer — single-page UI. No frameworks; talks to the /api endpoints. */

const $ = (s, el = document) => el.querySelector(s);
const esc = v => String(v ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
const fmt = n => (n ?? 0).toLocaleString('en-IN');
const whenShort = iso => iso ? new Date(iso).toLocaleString('en-IN', { day: '2-digit', month: 'short', hour: '2-digit', minute: '2-digit', hour12: false }) : '—';
const when = iso => iso ? new Date(iso).toLocaleString('en-IN', { day: '2-digit', month: 'short', year: 'numeric', hour: '2-digit', minute: '2-digit' }) : '—';
const STATUSES = ['READY', 'PROCESSED', 'UNMAPPED', 'MISSING', 'AMBIGUOUS', 'DUPLICATE', 'INVALID', 'FAILED'];
const LABEL = { READY: 'Ready', PROCESSED: 'Processed', UNMAPPED: 'Unmapped', MISSING: 'Missing', AMBIGUOUS: 'Ambiguous', DUPLICATE: 'Duplicate', INVALID: 'Validation errors', FAILED: 'Failed' };
const pill = s => `<span class="pill s-${esc(s)}">${esc(s)}</span>`;
const jobPill = s => { const c = s === 'Completed' ? 'j-Completed' : s === 'Completed with exceptions' ? 'j-exceptions' : s === 'Awaiting approval' ? 'j-Awaiting' : 'j-' + (s || '').split(' ')[0]; return `<span class="pill ${c}">${esc(s)}</span>`; };

const S = { me: null, meta: null, job: null, jobStep: {}, items: { status: 'EXCEPTIONS', q: '', page: 1 }, poll: null, demo: null, draft: null, mapping: {} };
const canOperate = () => S.me && S.me.role !== 'Viewer';
const isAdmin = () => S.me && S.me.role === 'Administrator';

async function api(method, path, body) {
  const opts = { method, headers: { 'X-DocOrg': '1' } };
  if (body instanceof FormData) opts.body = body;
  else if (body !== undefined) { opts.headers['content-type'] = 'application/json'; opts.body = JSON.stringify(body); }
  const res = await fetch(path, opts);
  if (res.status === 401) { location.href = '/login.html'; throw new Error('Signed out'); }
  const text = await res.text();
  let data; try { data = text ? JSON.parse(text) : null; } catch { data = text; }
  if (!res.ok) throw new Error((data && data.error) || `Request failed (${res.status}).`);
  return data;
}

function toast(msg, err) {
  const t = document.createElement('div'); t.className = 'toast' + (err ? ' err' : ''); t.textContent = msg; t.setAttribute('role', 'status');
  document.body.appendChild(t); setTimeout(() => t.remove(), err ? 6000 : 3200);
}
async function guard(fn, btn) {
  if (btn) btn.disabled = true;
  try { await fn(); } catch (e) { toast(e.message, true); } finally { if (btn && document.body.contains(btn)) btn.disabled = false; }
}

/* ---------------- shell ---------------- */
const ICON = {
  dash: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8"><rect x="3" y="3" width="7" height="9" rx="1"/><rect x="14" y="3" width="7" height="5" rx="1"/><rect x="14" y="12" width="7" height="9" rx="1"/><rect x="3" y="16" width="7" height="5" rx="1"/></svg>',
  jobs: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8"><path d="M3 7h6l2 2h10v10a1 1 0 0 1-1 1H4a1 1 0 0 1-1-1z"/><path d="M3 7V5a1 1 0 0 1 1-1h5l2 2"/></svg>',
  plus: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><path d="M12 5v14M5 12h14"/></svg>',
  audit: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8"><path d="M9 4h6l1 2h3v15H5V6h3z"/><path d="M9 12h6M9 16h4"/></svg>',
  gear: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8"><circle cx="12" cy="12" r="3"/><path d="M12 2v3M12 19v3M4.2 4.2l2.1 2.1M17.7 17.7l2.1 2.1M2 12h3M19 12h3M4.2 19.8l2.1-2.1M17.7 6.3l2.1-2.1"/></svg>'
};
function renderNav() {
  const r = location.hash.split('/')[1] || 'dashboard';
  const items = [['dashboard', 'Dashboard', ICON.dash], ['jobs', 'Jobs', ICON.jobs]];
  if (canOperate()) items.push(['new', 'New job', ICON.plus]);
  items.push(['audit', 'Audit log', ICON.audit], ['settings', 'Settings', ICON.gear]);
  $('#nav').innerHTML = items.map(([k, l, ic]) => `<a href="#/${k}" class="${r === k || (k === 'jobs' && r === 'job') ? 'on' : ''}">${ic}<span>${l}</span></a>`).join('');
  const initials = (S.me.displayName || S.me.userName).split(/\s+/).map(x => x[0]).join('').slice(0, 2).toUpperCase();
  $('#who').innerHTML = `<i>${esc(initials)}</i><div>${esc(S.me.displayName || S.me.userName)}<small>${esc(S.me.role)}</small></div>`;
}
const header = (eb, title, actions = '') => `<div class="ph"><div><div class="eyebrow">${eb}</div><h1>${title}</h1></div><div style="display:flex;gap:8px;flex-wrap:wrap;align-items:center">${actions}</div></div>`;

async function route() {
  clearInterval(S.poll); S.poll = null;
  $('#layer').innerHTML = '';
  const [, view, arg] = location.hash.split('/');
  renderNav();
  const main = $('#main');
  try {
    if (view === 'jobs') main.innerHTML = await vJobs();
    else if (view === 'new') main.innerHTML = vNew();
    else if (view === 'job') { S.jobId = arg; await loadJob(); return; }
    else if (view === 'audit') { S.audit = { q: '', page: 1 }; main.innerHTML = await vAudit(); }
    else if (view === 'settings') main.innerHTML = await vSettings();
    else main.innerHTML = await vDashboard();
  } catch (e) { main.innerHTML = `<div class="callout bad">${esc(e.message)}</div>`; }
  window.scrollTo(0, 0);
}

/* ---------------- dashboard ---------------- */
async function vDashboard() {
  const d = await api('GET', '/api/dashboard');
  const t = d.totals;
  const all = STATUSES.reduce((a, s) => a + (t[s] || 0), 0);
  const tiles = [['Total jobs', d.totalJobs, 'var(--accent)'], ['Total documents', d.totalDocuments, 'var(--fg-3)'], ['Processed', t.PROCESSED, 'var(--ok)'], ['Failed', t.FAILED, 'var(--bad)'],
    ['Unmapped', t.UNMAPPED, 'var(--warn)'], ['Missing', t.MISSING, 'var(--bad)'], ['Ambiguous', t.AMBIGUOUS, 'var(--vio)'], ['Duplicates', t.DUPLICATE, 'var(--slate)']];
  const actions = canOperate() ? `${S.meta.demoEnabled ? '<button class="btn" data-action="demo">Load demo data</button>' : ''}<a class="btn primary" href="#/new">${ICON.plus.replace('<svg', '<svg width="15" height="15"')} New processing job</a>` : '';
  const cover = all ? (100 * (t.PROCESSED || 0) / all) : 0;
  return header('Overview', 'Document Organizer dashboard', actions) +
    `<div class="kpis">${tiles.map(([l, v, c]) => `<div class="kpi"><div class="k"><span class="dot" style="background:${c}"></span>${l}</div><div class="v">${fmt(v)}</div></div>`).join('')}</div>
    <div class="panel" style="margin-top:16px"><div class="panel-h"><h2>Status of all documents across jobs</h2><span class="small muted">${fmt(all)} items · ${cover.toFixed(1)}% processed</span></div><div class="panel-b">
      ${all ? `<div class="bar">${STATUSES.filter(s => t[s]).map(s => `<span class="c-${s}" style="width:${(100 * t[s] / all).toFixed(3)}%" title="${LABEL[s]}: ${fmt(t[s])}"></span>`).join('')}</div>
      <div class="legend">${STATUSES.filter(s => t[s]).map(s => `<span><i class="dot c-${s}"></i>${LABEL[s]} ${fmt(t[s])}</span>`).join('')}</div>`
      : `<div class="muted">No jobs have been scanned yet.${canOperate() ? ' Create a job, or load the demo data to try the full flow.' : ''}</div>`}
    </div></div>
    <div class="panel" style="margin-top:16px"><div class="panel-h"><h2>Recent jobs</h2><a class="btn" href="#/jobs">View all</a></div>${jobsTable(d.recent)}</div>`;
}

function jobsTable(rows) {
  if (!rows.length) return `<div class="empty">No jobs yet.</div>`;
  return `<div class="tbl-wrap"><table><thead><tr><th>Job ID</th><th>Label and source</th><th>Destination</th><th>Volume</th><th>Status</th><th>Started / ended</th><th>Operator</th></tr></thead><tbody>${
    rows.map(j => `<tr class="click" data-href="#/job/${esc(j.id)}"><td class="mono">${esc(j.id)}</td><td class="lbl-cell">${esc(j.name)}<div class="small muted src-cell" title="${esc(j.source)}">${esc(j.source)}</div></td><td class="small muted src-cell" title="${esc(j.destination)}">${esc(j.destination)}</td><td class="num">${fmt(j.volume)}</td><td>${jobPill(j.status)}</td><td class="small nowrap">${whenShort(j.startedAt || j.createdAt)}<div class="muted">${j.endedAt ? whenShort(j.endedAt) : 'not finished'}</div></td><td>${esc(j.createdBy)}</td></tr>`).join('')}</tbody></table></div>`;
}

async function vJobs() {
  const rows = await api('GET', '/api/jobs');
  return header('All processing jobs', 'Jobs', canOperate() ? '<a class="btn primary" href="#/new">New processing job</a>' : '') + `<div class="panel">${jobsTable(rows)}</div>`;
}

/* ---------------- new job (step 1) ---------------- */
const LOC_TYPES = [
  { type: 'AzureBlob', title: 'Azure Blob Storage', desc: 'A container and optional prefix (virtual folder).', box: 'Container', flag: 'azureEnabled' },
  { type: 'S3', title: 'Amazon S3', desc: 'A bucket and optional prefix. S3-compatible storage works too.', box: 'Bucket', flag: 's3Enabled' },
  { type: 'Local', title: 'Local or network folder', desc: 'A folder on this server or a mapped share.', flag: 'localEnabled' }
];
const connsFor = type => S.meta.connections.filter(c => c.provider === type);
const typeUsable = t => S.meta[t.flag] && (t.type === 'Local' || connsFor(t.type).length > 0);
function newLoc(type) {
  return type === 'Local' ? { type: 'Local', path: '' } : { type, connection: (connsFor(type)[0] || {}).name || '', container: '', prefix: '' };
}
function defaultDraft() {
  const first = LOC_TYPES.find(typeUsable) || LOC_TYPES[2];
  return { name: '', source: newLoc(first.type), destination: newLoc(first.type) };
}
function locFields(which, loc) {
  const typeBtn = t => `<button type="button" class="opt ${loc.type === t.type ? 'on' : ''}" data-action="loc-type" data-which="${which}" data-type="${t.type}" ${typeUsable(t) ? '' : 'disabled'}><b>${t.title}</b><span class="small muted">${t.desc}</span></button>`;
  const types = `<div class="choice choice-3">${LOC_TYPES.map(typeBtn).join('')}</div>`;
  const f = (id, label, val, hint, mono = true) => `<div class="field"><label for="${which}-${id}">${label}</label><input class="input ${mono ? 'mono' : ''}" id="${which}-${id}" data-bind="${which}.${id}" value="${esc(val)}">${hint ? `<span class="hint">${hint}</span>` : ''}</div>`;
  let body;
  const lt = LOC_TYPES.find(t => t.type === loc.type);
  if (loc.type !== 'Local') {
    const conns = connsFor(loc.type);
    const box = lt.box, boxL = box.toLowerCase();
    if (!conns.length) body = `<div class="callout warn">No ${esc(lt.title)} connections are configured. Add one under DocumentOrganizer:StorageConnections in appsettings.json or environment variables.</div>`;
    else {
      const c = conns.find(x => x.name === loc.connection) || conns[0];
      body = `<div class="grid g3">
        <div class="field"><label for="${which}-connection">Storage connection</label><select class="input" id="${which}-connection" data-bind="${which}.connection">${conns.map(x => `<option ${x.name === loc.connection ? 'selected' : ''}>${esc(x.name)}</option>`).join('')}</select><span class="hint">${esc(c.auth)}${c.endpoint ? ' · ' + esc(c.endpoint) : ''}</span></div>
        ${f('container', box, loc.container || '', which === 'source' ? 'Read and list access is enough.' : `Created if it does not exist and you have rights.`)}
        ${f('prefix', 'Prefix (virtual folder)', loc.prefix || '', which === 'source' ? `Leave empty to scan the whole ${boxL}.` : 'Optional, e.g. organized/2024/')}
      </div>`;
    }
  } else body = f('path', 'Folder path', loc.path || '', which === 'source' ? 'Scanned recursively. Read access is enough.' : 'A new, empty folder is recommended. It must not be inside the source.');
  return `${types}${body}<div><button type="button" class="btn sm" data-action="test-loc" data-which="${which}">Check access</button> <span class="small" id="${which}-test"></span></div>`;
}
function vNew() {
  if (!canOperate()) return `<div class="callout warn">Viewers cannot create jobs.</div>`;
  S.draft = S.draft || defaultDraft();
  const d = S.draft;
  return header('New processing job', 'Where are the documents?', '<span class="small muted">Copy-only · the source is never modified</span>') + stepperHtml(1, null) +
    `<div class="stack-16">
      <div class="panel"><div class="panel-h"><h2>Source</h2></div><div class="panel-b">${locFields('source', d.source)}</div></div>
      <div class="panel"><div class="panel-h"><h2>Destination for the organized copy</h2></div><div class="panel-b">${locFields('destination', d.destination)}</div></div>
      <div class="panel"><div class="panel-h"><h2>Job details</h2></div><div class="panel-b"><div class="grid g2">
        <div class="field"><label for="jobname">Job label</label><input class="input" id="jobname" data-bind="name" value="${esc(d.name)}" placeholder="e.g. Client A – full re-organization"></div>
      </div></div></div>
    </div>
    ${S.demo ? `<div class="callout" style="margin-top:16px"><div><b>Demo data is ready.</b> <button class="btn sm" data-action="use-demo" data-mode="local">Fill in local demo folders</button> ${(S.demo.cloud || []).map(c => `<button class="btn sm" data-action="use-demo" data-mode="cloud" data-conn="${esc(c.connection)}">Fill in ${esc(c.connection)} demo ${c.provider === 'S3' ? 'buckets' : 'containers'}</button>`).join(' ')}</div></div>` : ''}
    <div class="foot"><a class="btn" href="#/jobs">Cancel</a><button class="btn primary" data-action="create-job">Check access and create job</button></div>`;
}

/* ---------------- job pages ---------------- */
const STEPS = ['Source', 'Mapping file', 'Column mapping', 'Rules', 'Scan & validate', 'Review & approve', 'Process', 'Reconcile'];
function stepperHtml(cur, job) {
  const editable = job && job.status === 'Draft' || job && job.status === 'Awaiting approval';
  return `<div class="stepper">${STEPS.map((s, i) => {
    const n = i + 1, cls = n < cur ? 'done' : n === cur ? 'cur' : '';
    const nav = editable && n >= 2 && n <= 4 && n !== cur && (n <= 2 || job.mapping) ? `data-action="goto-step" data-step="${n}"` : '';
    return `<button class="st ${cls}" ${nav} ${nav ? '' : 'tabindex="-1"'}><b>${n < cur ? '✓' : n}</b>${s}</button>`;
  }).join('')}</div>`;
}
function stepFor(job) {
  switch (job.status) {
    case 'Scanning': return 5;
    case 'Awaiting approval': return S.jobStep[job.id] && S.jobStep[job.id] < 5 ? S.jobStep[job.id] : 6;
    case 'Processing': case 'Paused': return 7;
    case 'Completed': case 'Completed with exceptions': return S.jobStep[job.id] === 7 ? 7 : 8;
    default: return S.jobStep[job.id] || (job.mapping ? 3 : 2);
  }
}
async function loadJob(keepScroll) {
  const job = await api('GET', `/api/jobs/${encodeURIComponent(S.jobId)}`);
  const prevStatus = S.job && S.job.id === job.id ? S.job.status : null;
  S.job = job;
  const step = stepFor(job);
  const y = window.scrollY;
  let body;
  if (step === 2) body = jobMapping(job);
  else if (step === 3) body = jobColumns(job);
  else if (step === 4) body = jobRules(job);
  else if (step === 5) body = jobScan(job);
  else if (step === 6) body = await jobReview(job);
  else if (step === 7) body = jobProcess(job);
  else body = await jobRecon(job);
  const actions = (isAdmin() || (job.status === 'Draft' && job.createdBy === S.me.userName)) && !job.running ? '<button class="btn sm danger" data-action="delete-job">Delete job</button>' : '';
  $('#main').innerHTML = header(`${esc(job.id)}`, esc(job.name), `${jobPill(job.status)} ${actions}`) +
    `<div class="small muted" style="margin:-10px 0 14px">Source <span class="mono">${esc(job.sourceText)}</span> → Destination <span class="mono">${esc(job.destinationText)}</span></div>` +
    stepperHtml(step, job) + (job.message ? `<div class="callout warn" style="margin-bottom:16px">${esc(job.message)}</div>` : '') + body;
  if (keepScroll) window.scrollTo(0, y);
  const live = job.running || job.status === 'Scanning' || job.status === 'Processing';
  clearInterval(S.poll); S.poll = null;
  if (live) S.poll = setInterval(async () => {
    if (location.hash !== `#/job/${S.jobId}` || $('#layer').innerHTML) return;
    try { await loadJob(true); } catch { }
  }, 1500);
  if (prevStatus && prevStatus !== job.status) toast(`Job is now: ${job.status}`);
}

function jobMapping(job) {
  const m = job.mapping;
  const preview = m && m.info ? `<div><div class="lbl" style="margin-bottom:8px">First rows</div><div class="panel tbl-wrap"><table><thead><tr>${m.info.headers.map(h => `<th>${esc(h)}</th>`).join('')}</tr></thead><tbody>${m.info.preview.map(r => `<tr>${m.info.headers.map((_, i) => `<td class="small">${esc(r[i] || '')}</td>`).join('')}</tr>`).join('')}</tbody></table></div></div>` : '';
  return `<div class="panel"><div class="panel-h"><h2>Mapping file</h2><span class="small muted">.xlsx or .csv · header row and columns are detected automatically</span></div><div class="panel-b">
    ${m ? `<div class="callout ok"><div><b>${esc(m.fileName)}</b> · ${fmt(m.rows)} data rows · header on row ${m.info.headerRow + 1} · SHA-256 <span class="mono">${esc(m.sha256.slice(0, 16))}…</span></div></div>` : ''}
    ${canOperate() ? `<div class="row-between"><div class="field" style="flex:1"><label for="mapfile">${m ? 'Replace with another file' : 'Choose the file'}</label><input type="file" id="mapfile" class="input" accept=".xlsx,.csv"></div>
      <div style="display:flex;gap:8px;align-self:flex-end"><button class="btn primary" data-action="upload-map">Upload</button>${S.meta.demoEnabled ? '<button class="btn" data-action="demo-map">Use demo mapping file</button>' : ''}</div></div>` : ''}
    ${preview}
    <div class="callout"><div>Large files are read row by row. The original <span class="mono">ID</span> of every row is kept for reconciliation.</div></div>
  </div></div><div class="foot"><a class="btn" href="#/jobs">Back to jobs</a>${m ? '<button class="btn primary" data-action="goto-step" data-step="3">Continue to column mapping</button>' : ''}</div>`;
}

function currentCfg(job) { return S.cfgEdit && S.cfgEdit.id === job.id ? S.cfgEdit.cfg : (S.cfgEdit = { id: job.id, cfg: JSON.parse(JSON.stringify(job.config)) }).cfg; }

function jobColumns(job) {
  const cfg = currentCfg(job);
  const info = job.mapping.info;
  const required = { EmployeeCode: 1, DocumentType: 1, FileName: 1, UploadedOn: cfg.yearRule === 'UploadedOn', Year: cfg.yearRule === 'YearColumn' };
  const key = f => f[0].toLowerCase() + f.slice(1);
  const sample = col => { const i = info.headers.indexOf(col); return i >= 0 && info.preview[0] ? info.preview[0][i] || '' : ''; };
  const rows = S.meta.fields.map(f => {
    const v = cfg.columns[key(f.field)] || '';
    return `<tr><td><b>${esc(f.label)}</b></td><td>${required[f.field] ? 'Required' : '<span class="muted">Optional</span>'}</td>
      <td><select class="input" style="min-width:200px" data-col="${key(f.field)}" aria-label="Column for ${esc(f.label)}"><option value="">— Not mapped —</option>${info.headers.map(h => `<option ${h === v ? 'selected' : ''}>${esc(h)}</option>`).join('')}</select></td>
      <td class="mono">${esc(sample(v))}</td><td>${v ? '<span class="ok-t">✓</span>' : required[f.field] ? '<span class="bad-t">Missing</span>' : ''}</td></tr>`;
  }).join('');
  return `<div class="panel"><div class="panel-h"><h2>Map columns to application fields</h2><span class="small muted">Detected automatically. Change any column that differs for this client.</span></div>
    <div class="tbl-wrap"><table><thead><tr><th>Application field</th><th>Required</th><th>Column in file</th><th>Sample value</th><th></th></tr></thead><tbody>${rows}</tbody></table></div></div>
    <div class="foot"><button class="btn" data-action="goto-step" data-step="2">Back</button><button class="btn primary" data-action="goto-step" data-step="4">Continue to rules</button></div>`;
}

function exampleDest(job, cfg) {
  const info = job.mapping.info, p = info.preview[0] || [];
  const col = c => { const i = info.headers.indexOf(cfg.columns[c]); return i >= 0 ? p[i] || '' : ''; };
  let year = '????';
  if (cfg.yearRule === 'UploadedOn') { const m = String(col('uploadedOn')).match(/(19|20)\d{2}/); if (m) year = m[0]; }
  if (cfg.yearRule === 'YearColumn') { const m = String(col('year')).match(/(19|20)\d{2}/); if (m) year = m[0]; }
  const file = String(col('fileName')).split(/[\\/]/).pop();
  const vals = { EmployeeCode: col('employeeCode'), DocumentType: col('documentType'), Year: year, FileName: file, ModuleCode: col('moduleCode'), SubModuleCode: col('subModuleCode'), DocumentName: col('documentName'), EmployeeName: col('employeeName') };
  return (cfg.template || '').replace(/<(\w+)>/g, '{$1}').replace(/\{(\w+)\}/g, (_, t) => vals[Object.keys(vals).find(k => k.toLowerCase() === t.toLowerCase())] ?? `{${t}}`);
}

function jobRules(job) {
  const cfg = currentCfg(job);
  const r = (name, val, title, desc, cur, disabled) => `<label class="radio ${cur === val ? 'on' : ''}"><input type="radio" name="${name}" value="${val}" ${cur === val ? 'checked' : ''} ${disabled ? 'disabled' : ''}><div><b>${title}</b><div class="small muted">${desc}</div></div></label>`;
  const blobDest = job.destination.type !== 'Local';
  return `<div class="grid g2">
    <div class="panel"><div class="panel-h"><h2>Year source</h2></div><div class="panel-b" style="gap:8px">
      ${r('yearRule', 'UploadedOn', 'UploadedOn year', 'May be the upload year rather than the document year.', cfg.yearRule)}
      ${r('yearRule', 'YearColumn', 'Dedicated Year column', 'Map the Year column under Column mapping.', cfg.yearRule)}
      ${r('yearRule', 'FileNamePattern', 'Year from file name pattern', 'Regular expression applied to the file name.', cfg.yearRule)}
      ${r('yearRule', 'PathPattern', 'Year from folder or blob path', 'Regular expression applied to the source path.', cfg.yearRule)}
      ${cfg.yearRule.endsWith('Pattern') ? `<div class="field"><label for="yp">Pattern</label><input class="input mono" id="yp" data-cfg="yearPattern" value="${esc(cfg.yearPattern || '')}"><span class="hint">The first 4-digit year (1980 to next year) in the match is used.</span></div>` : ''}
      <label class="check"><input type="checkbox" data-cfg="yearMandatory" ${cfg.yearMandatory ? 'checked' : ''}><span>Year is mandatory. Records without a valid year go to exceptions instead of an “Unknown Year” folder.</span></label>
    </div></div>
    <div class="panel"><div class="panel-h"><h2>Destination structure</h2></div><div class="panel-b">
      <div class="field"><label for="tpl">Folder template</label><input class="input mono" id="tpl" data-cfg="template" value="${esc(cfg.template)}"><span class="hint">Tokens: ${S.meta.tokens.map(t => `{${t}}`).join(' ')}</span></div>
      <div><div class="lbl" style="margin-bottom:6px">Example from the first mapping row</div><div class="path" id="example">${esc(exampleDest(job, cfg))}</div></div>
      <div class="lbl">Matching (in this order; no guessing)</div>
      <div class="small">1. Exact file name · 2. Relative path + file name, when the mapping includes a path</div>
      <label class="check"><input type="checkbox" data-cfg="caseInsensitiveMatch" ${cfg.caseInsensitiveMatch ? 'checked' : ''}><span>3. Case-insensitive file name</span></label>
      <label class="check"><input type="checkbox" data-cfg="normalizedMatch" ${cfg.normalizedMatch ? 'checked' : ''}><span>4. Normalized file name (spaces, dashes and underscores treated alike)</span></label>
      <div class="hint">When more than one mapping record matches a file, it is marked AMBIGUOUS for a person to resolve.</div>
    </div></div>
    <div class="panel"><div class="panel-h"><h2>If the destination file already exists</h2></div><div class="panel-b" style="gap:8px">
      ${r('duplicateRule', 'Skip', 'Skip and report', 'Default. Nothing is overwritten.', cfg.duplicateRule)}
      ${r('duplicateRule', 'Rename', 'Rename with a suffix', 'Deterministic suffix, e.g. _dup1.', cfg.duplicateRule)}
      ${r('duplicateRule', 'CompareHash', 'Compare hash, then decide', 'Skip if identical; rename if different.', cfg.duplicateRule)}
      ${r('duplicateRule', 'Overwrite', 'Overwrite', S.meta.allowOverwrite ? 'Replaces the existing destination file.' : 'Not permitted by the administrator.', cfg.duplicateRule, !S.meta.allowOverwrite)}
    </div></div>
    <div class="panel"><div class="panel-h"><h2>Operation and integrity</h2></div><div class="panel-b" style="gap:0">
      <div class="toggle"><div><b>Operation</b><div class="small muted">COPY. Source documents are never moved, changed or deleted.</div></div><span class="pill s-READY">COPY</span></div>
      <div class="toggle"><div><b>Integrity check after copy</b><div class="small muted">SHA-256 reads every source and destination file. Content-MD5 uses the stored checksum where available.</div></div>
        <select class="input" style="width:150px" data-cfg="integrity">${[['Sha256', 'SHA-256'], ['Md5', 'Content-MD5'], ['None', 'None']].map(([v, l]) => `<option value="${v}" ${cfg.integrity === v ? 'selected' : ''}>${l}</option>`).join('')}</select></div>
      <div class="toggle"><div><b>Batch size</b><div class="small muted">Files per batch. Progress is saved after every batch.</div></div>
        <select class="input" style="width:110px" data-cfg="batchSize">${[1000, 5000, 10000].map(v => `<option ${cfg.batchSize === v ? 'selected' : ''}>${v}</option>`).join('')}</select></div>
      ${blobDest ? `<div class="toggle"><div><b>Keep object metadata</b><div class="small muted">Keeps source metadata and adds Job ID, record ID and employee code.</div></div><input type="checkbox" data-cfg="keepBlobMetadata" ${cfg.keepBlobMetadata ? 'checked' : ''} aria-label="Keep object metadata"></div>` : ''}
    </div></div>
  </div>
  <div class="foot"><button class="btn" data-action="goto-step" data-step="3">Back</button>${canOperate() ? '<button class="btn primary" data-action="save-scan">Save and start scan</button>' : ''}</div>`;
}

function jobScan(job) {
  const s = job.scan || { phase: 'Starting', done: 0, total: 0 };
  const pct = s.total ? Math.min(100, 100 * s.done / s.total) : 0;
  return `<div class="panel"><div class="panel-h"><h2>Scanning and validating</h2><span class="pill j-Scanning">Running</span></div><div class="panel-b">
    <div class="row-between"><b>${esc(s.phase)}</b><span class="small muted num">${fmt(s.done)}${s.total ? ' of ' + fmt(s.total) : ''}</span></div>
    <div class="prog"><i style="width:${s.total ? pct : 8}%"></i></div>
    <div class="small muted">Started ${when(job.scanStartedAt)}. You can leave this page; the scan continues in the background.</div>
  </div></div>`;
}

async function itemsPanel(job, opts) {
  const st = S.items;
  const qs = new URLSearchParams({ status: st.status, q: st.q, page: st.page, pageSize: 50 });
  const data = await api('GET', `/api/jobs/${encodeURIComponent(job.id)}/items?${qs}`);
  const c = job.counts;
  const tabs = [['EXCEPTIONS', 'All exceptions'], ...opts.tabs.map(s => [s, LABEL[s]]), ['ALL', 'Everything']];
  const pages = Math.max(1, Math.ceil(data.total / data.pageSize));
  const canResolve = canOperate() && job.status === 'Awaiting approval';
  const rows = data.items.map(it => `<tr>
      <td class="mono">${esc(it.recordId || '—')}</td>
      <td class="mono" title="${esc(it.sourceKey || '')}">${esc(it.sourceKey || it.fileName || '—')}</td>
      <td>${esc(it.employeeCode || '—')}</td><td>${esc(it.documentType || '—')}</td><td>${esc(it.year || '—')}</td>
      <td>${pill(it.status)}</td>
      <td class="small wrap">${it.status === 'READY' || it.status === 'PROCESSED' ? `<span class="mono">${esc(it.destKey || '')}</span>${it.reason ? '<br><span class="muted">' + esc(it.reason) + '</span>' : ''}` : esc(it.reason || '')}</td>
      <td>${it.status === 'AMBIGUOUS' && canResolve ? `<button class="btn sm" data-action="resolve" data-id="${it.id}">Resolve</button>` : ''}</td></tr>`).join('');
  return `<div class="panel"><div class="panel-h"><h2>${opts.title}</h2>
      <div class="tabs">${tabs.map(([k, l]) => `<button class="tab ${st.status === k ? 'on' : ''}" data-action="items-tab" data-status="${k}">${l}${c[k] !== undefined ? ' ' + fmt(c[k]) : ''}</button>`).join('')}</div>
      <input class="input" style="max-width:240px" id="itemq" placeholder="Search file, record, employee" value="${esc(st.q)}"></div>
    ${data.items.length ? `<div class="tbl-wrap"><table><thead><tr><th>Record ID</th><th>Source file</th><th>Employee</th><th>Type</th><th>Year</th><th>Status</th><th>Destination / reason</th><th></th></tr></thead><tbody>${rows}</tbody></table></div>` : '<div class="empty">Nothing to show for this filter.</div>'}
    <div class="pager"><span>${fmt(data.total)} items</span><button class="btn sm" data-action="page" data-page="${st.page - 1}" ${st.page <= 1 ? 'disabled' : ''}>Previous</button><span>Page ${st.page} of ${pages}</span><button class="btn sm" data-action="page" data-page="${st.page + 1}" ${st.page >= pages ? 'disabled' : ''}>Next</button></div></div>`;
}

async function jobReview(job) {
  const c = job.counts;
  const fileStatuses = ['READY', 'UNMAPPED', 'AMBIGUOUS', 'DUPLICATE', 'INVALID'];
  const tree = await api('GET', `/api/jobs/${encodeURIComponent(job.id)}/tree`);
  const treeText = buildTree(tree);
  const tiles = [['Files discovered', job.discovered, null], ['Ready', c.READY, 'READY'], ['Unmapped files', c.UNMAPPED, 'UNMAPPED'], ['Missing source files', c.MISSING, 'MISSING'],
    ['Ambiguous matches', c.AMBIGUOUS, 'AMBIGUOUS'], ['Duplicates', c.DUPLICATE, 'DUPLICATE'], ['Validation errors', c.INVALID, 'INVALID'], ['Mapping records', job.mapping.rows, null]];
  const total = fileStatuses.reduce((a, s) => a + c[s], 0) || 1;
  return `<div class="kpis">${tiles.map(([l, v, s]) => `<div class="kpi ${s ? 'click' : ''}" ${s ? `data-action="items-tab" data-status="${s}"` : ''}><div class="k">${s ? `<i class="dot c-${s}"></i>` : ''}${l}</div><div class="v">${fmt(v)}</div></div>`).join('')}</div>
    <div class="panel" style="margin-top:16px"><div class="panel-b">
      <div class="bar">${fileStatuses.filter(s => c[s]).map(s => `<span class="c-${s}" style="width:${(100 * c[s] / total).toFixed(3)}%" title="${LABEL[s]}"></span>`).join('')}</div>
      <div class="eq">Items: <b>${fmt(c.READY)}</b> ready + ${fmt(c.UNMAPPED)} unmapped + ${fmt(c.AMBIGUOUS)} ambiguous + ${fmt(c.DUPLICATE)} duplicate + ${fmt(c.INVALID)} validation errors + ${fmt(c.MISSING)} missing. Every source file and every mapping record has a status; nothing is skipped silently.</div>
    </div></div>
    <div style="margin-top:16px">${await itemsPanel(job, { title: 'Exceptions and results', tabs: ['READY', 'UNMAPPED', 'MISSING', 'AMBIGUOUS', 'DUPLICATE', 'INVALID'] })}</div>
    <div class="grid g2" style="margin-top:16px">
      <div class="panel"><div class="panel-h"><h2>Proposed output structure</h2><span class="small muted">sample</span></div><div class="panel-b"><pre class="tree">${esc(treeText)}</pre></div></div>
      <div class="panel" style="align-self:start"><div class="panel-h"><h2>Approval</h2><span class="small muted">required before any copy</span></div><div class="panel-b">
        <div class="eq">Operation <b>COPY</b> · Duplicates <b>${esc(job.config.duplicateRule)}</b><br>Year <b>${esc(job.config.yearRule)}</b> · Integrity <b>${esc(job.config.integrity)}</b><br>Destination <b>${esc(job.destinationText)}</b></div>
        <div class="row-between"><a class="btn" href="/api/jobs/${encodeURIComponent(job.id)}/report?kind=validation&format=xlsx">Download validation report</a><a class="btn sm" href="/api/jobs/${encodeURIComponent(job.id)}/report?kind=validation&format=csv">CSV</a></div>
        ${canOperate() ? `<label class="check"><input type="checkbox" id="ack"><span>I have reviewed the preview and exceptions. Copy <b class="num">${fmt(c.READY)}</b> ready files. Exceptions stay in the report and are not copied.</span></label>
        <div><button class="btn primary" data-action="approve" ${c.READY ? '' : 'disabled'}>Approve and start copy</button></div>` : '<div class="small muted">Viewers cannot approve processing.</div>'}
      </div></div>
    </div>`;
}

function buildTree(t) {
  if (!t.sample.length) return `${t.root}\n(no files ready yet)`;
  const lines = [t.root];
  const root = {};
  t.sample.forEach(p => { let n = root; p.split('/').forEach(seg => { n[seg] = n[seg] || {}; n = n[seg]; }); });
  const walk = (node, prefix) => {
    const keys = Object.keys(node);
    keys.forEach((k, i) => {
      const last = i === keys.length - 1;
      lines.push(prefix + (last ? '└─ ' : '├─ ') + k + (Object.keys(node[k]).length ? '/' : ''));
      walk(node[k], prefix + (last ? '   ' : '│  '));
    });
  };
  walk(root, '');
  lines.push(`… ${fmt(t.employees)} employee folders · ${fmt(t.documentTypes)} document types · ${t.firstYear || '?'}–${t.lastYear || '?'} · ${fmt(t.files)} files`);
  return lines.join('\n');
}

function jobProcess(job) {
  const c = job.counts, p = job.progress;
  const pct = p.total ? 100 * p.done / p.total : 0;
  const running = job.status === 'Processing';
  return `<div class="panel"><div class="panel-h"><h2>${running ? 'Copying documents' : job.status === 'Paused' ? 'Paused' : 'Copy finished'}</h2>${jobPill(job.status)}</div><div class="panel-b">
    <div class="row-between"><span style="font:600 30px var(--f-display)" class="num">${pct.toFixed(0)}%</span><span class="small muted num">${fmt(p.done)} of ${fmt(p.total)} files</span></div>
    <div class="prog" style="height:12px"><i style="width:${pct}%"></i></div>
    <div class="grid g3">
      <div><div class="lbl">Processed</div><div class="num" style="font:600 20px var(--f-display);color:var(--ok)">${fmt(c.PROCESSED)}</div></div>
      <div><div class="lbl">Failed</div><div class="num" style="font:600 20px var(--f-display);color:var(--bad)">${fmt(c.FAILED)}</div></div>
      <div><div class="lbl">Skipped as duplicate</div><div class="num" style="font:600 20px var(--f-display)">${fmt(c.DUPLICATE)}</div></div>
    </div>
    <div class="small muted">Approved by ${esc(job.approvedBy || '—')} on ${when(job.approvedAt)}. Progress is saved after each batch; if the application restarts, resume continues from the remaining files.</div>
  </div></div>
  <div class="foot"><div style="display:flex;gap:8px">${canOperate() && running ? '<button class="btn" data-action="pause">Pause</button>' : ''}${canOperate() && job.status === 'Paused' ? '<button class="btn primary" data-action="resume">Resume</button>' : ''}
  ${canOperate() && !running && c.FAILED ? `<button class="btn" data-action="retry">Retry ${fmt(c.FAILED)} failed</button>` : ''}</div>
  ${job.status.startsWith('Completed') ? '<button class="btn primary" data-action="goto-step" data-step="8">View reconciliation</button>' : ''}</div>`;
}

async function jobRecon(job) {
  const c = job.counts;
  const tiles = [['Processed', 'PROCESSED'], ['Failed', 'FAILED'], ['Unmapped', 'UNMAPPED'], ['Missing', 'MISSING'], ['Ambiguous', 'AMBIGUOUS'], ['Duplicates', 'DUPLICATE'], ['Validation errors', 'INVALID']];
  const dur = job.startedAt && job.endedAt ? Math.round((new Date(job.endedAt) - new Date(job.startedAt)) / 60000) : null;
  if (!['FAILED', 'UNMAPPED', 'MISSING', 'AMBIGUOUS', 'DUPLICATE', 'INVALID', 'PROCESSED', 'EXCEPTIONS', 'ALL'].includes(S.items.status)) S.items.status = 'EXCEPTIONS';
  const base = `/api/jobs/${encodeURIComponent(job.id)}/report?kind=reconciliation`;
  return `<div class="callout ${c.FAILED ? 'warn' : 'ok'}" style="margin-bottom:16px"><div><b>Reconciliation ${c.FAILED ? 'ready, with failed files to retry' : 'complete'}.</b> Every source file and mapping record has a final status. The source was not modified.</div></div>
    <div class="kpis">${tiles.map(([l, s]) => `<div class="kpi click" data-action="items-tab" data-status="${s}"><div class="k"><i class="dot c-${s}"></i>${l}</div><div class="v">${fmt(c[s])}</div></div>`).join('')}<div class="kpi"><div class="k">Duration</div><div class="v">${dur === null ? '—' : dur < 60 ? dur + ' min' : Math.floor(dur / 60) + 'h ' + (dur % 60) + 'm'}</div></div></div>
    <div class="panel" style="margin-top:16px"><div class="panel-b row-between">
      <div class="small muted">Approved by ${esc(job.approvedBy)} · ${when(job.approvedAt)} · finished ${when(job.endedAt)}</div>
      <div style="display:flex;gap:8px;flex-wrap:wrap">${canOperate() && c.FAILED ? `<button class="btn" data-action="retry">Retry ${fmt(c.FAILED)} failed</button>` : ''}
        <a class="btn" href="${base}&format=csv">Reconciliation CSV</a><a class="btn primary" href="${base}&format=xlsx">Reconciliation Excel</a></div>
    </div></div>
    <div style="margin-top:16px">${await itemsPanel(job, { title: 'Results', tabs: ['PROCESSED', 'FAILED', 'UNMAPPED', 'MISSING', 'AMBIGUOUS', 'DUPLICATE', 'INVALID'] })}</div>
    <div class="foot"><a class="btn" href="#/audit">View audit log</a><a class="btn" href="#/dashboard">Back to dashboard</a></div>`;
}

/* ---------------- drawer & modal ---------------- */
async function openResolve(itemId) {
  const data = await api('GET', `/api/jobs/${encodeURIComponent(S.job.id)}/items?status=AMBIGUOUS&pageSize=500`);
  const it = data.items.find(x => x.id === itemId);
  if (!it) return toast('That item is no longer ambiguous.', true);
  S.resolve = { it, pick: null };
  renderResolve();
}
function renderResolve() {
  const { it, pick } = S.resolve;
  $('#layer').innerHTML = `<div class="scrim" data-action="close-layer"><div class="drawer" role="dialog" aria-modal="true" aria-label="Resolve ambiguous match">
    <div><div class="eyebrow">Ambiguous match</div><h1 style="font-size:20px">Which record owns this file?</h1></div>
    <div class="path">${esc(it.sourceKey)}</div>
    <div class="callout warn"><div>${it.candidates.length} mapping records name this file. The system will not choose one. Pick the correct record, or leave it flagged so it stays in the exception report.</div></div>
    ${it.candidates.map(c => `<label class="cand ${pick === c.recordId ? 'on' : ''}"><input type="radio" name="cand" value="${esc(c.recordId)}" ${pick === c.recordId ? 'checked' : ''}><dl>
      <dt>Record ID</dt><dd class="mono">${esc(c.recordId)}</dd><dt>EmployeeCode</dt><dd>${esc(c.employeeCode || '—')}</dd><dt>Employee name</dt><dd>${esc(c.employeeName || '—')}</dd>
      <dt>Type</dt><dd>${esc(c.documentType || '—')}</dd><dt>Document name</dt><dd>${esc(c.documentName || '—')}</dd><dt>UploadedOn</dt><dd>${esc(c.uploadedOn || '—')}</dd></dl></label>`).join('')}
    <div class="field"><label for="note">Resolution note (saved to the audit log)</label><input class="input" id="note" placeholder="e.g. Confirmed with client HR by email"></div>
    <div class="foot" style="margin-top:auto"><button class="btn" data-action="close-layer">Leave flagged</button><button class="btn primary" data-action="do-resolve" ${pick ? '' : 'disabled'}>Assign to record ${esc(pick || '')}</button></div>
  </div></div>`;
}
function openApprove() {
  const c = S.job.counts;
  $('#layer').innerHTML = `<div class="scrim modal-wrap" data-action="close-layer"><div class="modal" role="dialog" aria-modal="true" aria-label="Confirm approval">
    <h2 style="font:600 19px var(--f-display)">Start copying ${fmt(c.READY)} files?</h2>
    <div class="small muted">Files are copied to <span class="mono">${esc(S.job.destinationText)}</span> in batches of ${fmt(S.job.config.batchSize)}${S.job.config.integrity !== 'None' ? ' with ' + esc(S.job.config.integrity) + ' verification' : ''}. The source is not modified. Your approval is recorded in the audit log.</div>
    <div class="foot" style="margin-top:4px"><button class="btn" data-action="close-layer">Cancel</button><button class="btn primary" data-action="confirm-approve">Approve and start</button></div>
  </div></div>`;
}

/* ---------------- audit ---------------- */
async function vAudit() {
  const a = S.audit;
  const data = await api('GET', `/api/audit?${new URLSearchParams({ q: a.q, page: a.page })}`);
  const pages = Math.max(1, Math.ceil(data.total / data.pageSize));
  return header('Every sign-in, configuration, approval, run and export', 'Audit log') +
    `<div class="panel"><div class="panel-h"><span class="small muted">${fmt(data.total)} events</span><input class="input" id="auditq" style="max-width:280px" placeholder="Search events, users, job IDs" value="${esc(a.q)}"></div>
    ${data.items.length ? `<div class="tbl-wrap"><table><thead><tr><th>Time</th><th>User</th><th>Role</th><th>Event</th><th>Job</th><th>Details</th></tr></thead><tbody>${data.items.map(e => `<tr><td class="mono">${esc(new Date(e.ts).toLocaleString('en-IN'))}</td><td>${esc(e.user || '—')}</td><td class="muted">${esc(e.role || '—')}</td><td><b>${esc(e.event)}</b></td><td class="mono">${e.jobId ? `<a href="#/job/${esc(e.jobId)}">${esc(e.jobId)}</a>` : '—'}</td><td class="small muted wrap">${esc(e.details || '')}</td></tr>`).join('')}</tbody></table></div>` : '<div class="empty">No events match.</div>'}
    <div class="pager"><button class="btn sm" data-action="audit-page" data-page="${a.page - 1}" ${a.page <= 1 ? 'disabled' : ''}>Newer</button><span>Page ${a.page} of ${pages}</span><button class="btn sm" data-action="audit-page" data-page="${a.page + 1}" ${a.page >= pages ? 'disabled' : ''}>Older</button></div></div>`;
}

/* ---------------- settings ---------------- */
async function vSettings() {
  const s = await api('GET', '/api/settings');
  const st = s.settings; const dis = isAdmin() ? '' : 'disabled';
  const users = isAdmin() ? await api('GET', '/api/users') : [];
  const sw = (key, label, desc) => `<div class="toggle"><div><b>${label}</b><div class="small muted">${desc}</div></div><input type="checkbox" data-set="${key}" ${st[key] ? 'checked' : ''} ${dis} aria-label="${label}"></div>`;
  const num = (key, label, desc, min, max) => `<div class="toggle"><div><b>${label}</b><div class="small muted">${desc}</div></div><input type="number" class="input" style="width:110px" data-set="${key}" value="${st[key]}" min="${min}" max="${max}" ${dis}></div>`;
  return header('Administration', 'Settings', isAdmin() ? '<button class="btn primary" data-action="save-settings">Save changes</button>' : `<span class="small muted">Read-only for ${esc(S.me.role)}s</span>`) +
    `<div class="grid g2">
      <div class="panel"><div class="panel-h"><h2>Storage</h2></div><div class="panel-b" style="gap:0">
        ${sw('azureBlobEnabled', 'Azure Blob Storage', 'Containers in Azure storage accounts.')}
        ${sw('s3Enabled', 'Amazon S3', 'Buckets in Amazon S3 or S3-compatible storage.')}
        ${sw('localSourceEnabled', 'Local or network folders', 'Folders on this server or mapped shares.')}
        <div style="padding-top:12px"><div class="lbl" style="margin-bottom:6px">Configured connections</div>${S.meta.connections.length ? S.meta.connections.map(c => `<div class="small"><b>${esc(c.name)}</b> · ${esc(c.provider === 'S3' ? 'Amazon S3' : c.provider === 'AzureBlob' ? 'Azure Blob' : c.provider)} · ${esc(c.auth)} <span class="mono muted">${esc(c.endpoint)}</span></div>`).join('') : '<div class="small muted">None</div>'}
        <div class="hint" style="margin-top:6px">Connections and their secrets are set in appsettings.json or environment variables, never in the database.</div></div>
      </div></div>
      <div class="panel"><div class="panel-h"><h2>Processing permissions</h2></div><div class="panel-b" style="gap:0">
        <div class="toggle"><div><b>Operation</b><div class="small muted">COPY only. Move and delete are not available in this version.</div></div><span class="pill s-READY">COPY</span></div>
        ${sw('allowOverwrite', 'Allow overwrite on duplicate', 'Lets operators choose Overwrite when the destination file exists.')}
        ${num('maxParallelCopies', 'Parallel copies', 'Files copied at the same time (1–64). Lower it if storage throttles.', 1, 64)}
      </div></div>
      <div class="panel"><div class="panel-h"><h2>Retention and security</h2></div><div class="panel-b" style="gap:0">
        ${num('jobRetentionDays', 'Keep jobs and reports (days)', 'Older jobs, their items and mapping files are deleted at start-up.', 30, 3650)}
        ${num('auditRetentionDays', 'Keep audit log (days)', 'Minimum 365.', 365, 3650)}
        <div class="toggle"><div><b>Session timeout</b><div class="small muted">Set in appsettings.json.</div></div><span>${s.sessionTimeoutMinutes} minutes</span></div>
        <div class="toggle"><div><b>Documents sent to external AI services</b><div class="small muted">Never.</div></div><span class="pill s-DUPLICATE">OFF</span></div>
      </div></div>
      <div class="panel"><div class="panel-h"><h2>My password</h2></div><div class="panel-b">
        <div class="field"><label for="pw0">Current password</label><input type="password" class="input" id="pw0" autocomplete="current-password"></div>
        <div class="field"><label for="pw1">New password</label><input type="password" class="input" id="pw1" autocomplete="new-password"><span class="hint">At least 10 characters with three of: upper-case, lower-case, digits, symbols.</span></div>
        <div><button class="btn" data-action="change-pw">Change password</button></div>
      </div></div>
    </div>
    ${isAdmin() ? `<div class="panel" style="margin-top:16px"><div class="panel-h"><h2>Users and roles</h2></div>
      <div class="tbl-wrap"><table><thead><tr><th>User</th><th>Name</th><th>Role</th><th>Status</th><th>Last sign-in</th><th></th></tr></thead><tbody>${users.map(u => `<tr>
        <td class="mono">${esc(u.userName)}</td><td>${esc(u.displayName)}</td>
        <td><select class="input" style="width:150px" data-user-role="${esc(u.userName)}">${['Administrator', 'Operator', 'Viewer'].map(r => `<option ${r === u.role ? 'selected' : ''}>${r}</option>`).join('')}</select></td>
        <td>${u.active ? '<span class="pill s-PROCESSED">Active</span>' : '<span class="pill s-DUPLICATE">Disabled</span>'}</td><td class="small">${when(u.lastLogin)}</td>
        <td style="white-space:nowrap"><button class="btn sm" data-action="user-toggle" data-user="${esc(u.userName)}" data-active="${u.active}">${u.active ? 'Disable' : 'Enable'}</button> <button class="btn sm" data-action="user-reset" data-user="${esc(u.userName)}">Reset password</button></td></tr>`).join('')}</tbody></table></div>
      <div class="panel-b" style="border-top:1px solid var(--line)"><div class="lbl">Add a user</div><div class="grid g3">
        <div class="field"><label for="nu">User name</label><input class="input" id="nu"></div>
        <div class="field"><label for="nd">Display name</label><input class="input" id="nd"></div>
        <div class="field"><label for="nr">Role</label><select class="input" id="nr"><option>Operator</option><option>Viewer</option><option>Administrator</option></select></div>
        <div class="field"><label for="np">Initial password</label><input class="input" id="np" type="password" autocomplete="new-password"></div>
      </div><div><button class="btn" data-action="add-user">Add user</button></div>
      <div class="hint">Administrator: settings, users and everything below. Operator: create jobs, upload mappings, scan, resolve, approve, retry and export. Viewer: view jobs, results and reports only.</div></div></div>` : ''}`;
}

/* ---------------- events ---------------- */
function setPath(obj, path, val) { const k = path.split('.'); let o = obj; while (k.length > 1) o = o[k.shift()]; o[k[0]] = val; }

document.addEventListener('input', e => {
  const t = e.target;
  if (t.dataset.bind && S.draft) setPath(S.draft, t.dataset.bind, t.value);
  if (t.dataset.cfg && S.job) {
    const cfg = currentCfg(S.job);
    if (t.type === 'checkbox') cfg[t.dataset.cfg] = t.checked;
    else if (t.dataset.cfg === 'batchSize') cfg.batchSize = +t.value;
    else cfg[t.dataset.cfg] = t.value;
    const ex = $('#example'); if (ex) ex.textContent = exampleDest(S.job, cfg);
  }
});
let searchTimer;
document.addEventListener('keyup', e => {
  if (e.target.id === 'itemq') { clearTimeout(searchTimer); searchTimer = setTimeout(() => { S.items.q = e.target.value; S.items.page = 1; loadJob(true).then(() => { const q = $('#itemq'); if (q) { q.focus(); q.setSelectionRange(q.value.length, q.value.length); } }); }, 350); }
  if (e.target.id === 'auditq') { clearTimeout(searchTimer); searchTimer = setTimeout(async () => { S.audit.q = e.target.value; S.audit.page = 1; $('#main').innerHTML = await vAudit(); const q = $('#auditq'); q.focus(); q.setSelectionRange(q.value.length, q.value.length); }, 350); }
  if (e.key === 'Escape' && $('#layer').innerHTML) $('#layer').innerHTML = '';
});
document.addEventListener('change', e => {
  const t = e.target;
  if (t.dataset.col && S.job) { currentCfg(S.job).columns[t.dataset.col] = t.value || null; loadJob(true); }
  if (t.name === 'yearRule' || t.name === 'duplicateRule') { currentCfg(S.job)[t.name] = t.value; loadJob(true); }
  if (t.dataset.bind && t.tagName === 'SELECT' && S.draft) { setPath(S.draft, t.dataset.bind, t.value); $('#main').innerHTML = vNew(); }
  if (t.name === 'cand' && S.resolve) { S.resolve.pick = t.value; renderResolve(); }
  if (t.dataset.userRole) guard(async () => { await api('PUT', `/api/users/${encodeURIComponent(t.dataset.userRole)}`, { role: t.value }); toast('Role changed.'); });
});

document.addEventListener('click', e => {
  const row = e.target.closest('tr[data-href]');
  if (row) { location.hash = row.dataset.href; return; }
  const el = e.target.closest('[data-action]');
  if (!el) return;
  const a = el.dataset.action;
  if (a === 'close-layer') { if (e.target === el || el.tagName === 'BUTTON') { $('#layer').innerHTML = ''; S.resolve = null; } return; }
  const handlers = {
    'demo': () => guard(async () => {
      toast('Creating demo data…');
      S.demo = await api('POST', '/api/demo');
      toast(`Demo ready: ${fmt(S.demo.files)} files, ${fmt(S.demo.records)} mapping records.`);
      if (S.demo.cloudNote) setTimeout(() => toast(S.demo.cloudNote), 3300);
      S.draft = null; if (location.hash === '#/new') route(); else location.hash = '#/new';
    }, el),
    'use-demo': () => {
      const d = S.demo; S.draft = defaultDraft();
      S.draft.name = 'Demo job';
      const c = el.dataset.mode === 'cloud' && (d.cloud || []).find(x => x.connection === el.dataset.conn);
      if (c) {
        S.draft.name = `Demo job – ${c.connection}`;
        S.draft.source = { type: c.provider, connection: c.connection, container: c.sourceContainer, prefix: c.sourcePrefix };
        S.draft.destination = { type: c.provider, connection: c.connection, container: c.destinationContainer, prefix: 'demo/' };
      } else { S.draft.source = { type: 'Local', path: d.localSource }; S.draft.destination = { type: 'Local', path: d.localDestination }; }
      $('#main').innerHTML = vNew();
    },
    'loc-type': () => { const w = el.dataset.which; S.draft[w] = newLoc(el.dataset.type); $('#main').innerHTML = vNew(); },
    'test-loc': () => guard(async () => {
      const w = el.dataset.which, out = $(`#${w}-test`); out.textContent = 'Checking…'; out.className = 'small muted';
      try { const r = await api('POST', '/api/connections/test', { location: S.draft[w], needWrite: w === 'destination' }); out.textContent = r.message; out.className = 'small ok-t'; }
      catch (err) { out.textContent = err.message; out.className = 'small bad-t'; }
    }, el),
    'create-job': () => guard(async () => {
      const r = await api('POST', '/api/jobs', S.draft);
      toast(`Job ${r.id} created.`); S.draft = null; S.jobStep[r.id] = 2; location.hash = `#/job/${r.id}`;
    }, el),
    'goto-step': () => { S.jobStep[S.job.id] = +el.dataset.step; S.items = { status: 'EXCEPTIONS', q: '', page: 1 }; loadJob(); window.scrollTo(0, 0); },
    'upload-map': () => guard(async () => {
      const f = $('#mapfile').files[0]; if (!f) throw new Error('Choose a file first.');
      const fd = new FormData(); fd.append('file', f);
      toast('Uploading and reading the file…');
      const r = await api('POST', `/api/jobs/${encodeURIComponent(S.job.id)}/mapping`, fd);
      S.cfgEdit = null; toast(`Read ${fmt(r.rows)} rows. Columns detected.`); S.jobStep[S.job.id] = 3; await loadJob();
    }, el),
    'demo-map': () => guard(async () => {
      const r = await api('POST', `/api/jobs/${encodeURIComponent(S.job.id)}/mapping/demo`);
      S.cfgEdit = null; toast(`Read ${fmt(r.rows)} rows. Columns detected.`); S.jobStep[S.job.id] = 3; await loadJob();
    }, el),
    'save-scan': () => guard(async () => {
      await api('PUT', `/api/jobs/${encodeURIComponent(S.job.id)}/config`, currentCfg(S.job));
      await api('POST', `/api/jobs/${encodeURIComponent(S.job.id)}/scan`);
      S.cfgEdit = null; S.jobStep[S.job.id] = null; S.items = { status: 'EXCEPTIONS', q: '', page: 1 }; await loadJob();
    }, el),
    'items-tab': () => { S.items.status = el.dataset.status; S.items.page = 1; loadJob(true); },
    'page': () => { S.items.page = +el.dataset.page; loadJob(true); },
    'resolve': () => guard(() => openResolve(+el.dataset.id), el),
    'do-resolve': () => guard(async () => {
      const r = await api('POST', `/api/jobs/${encodeURIComponent(S.job.id)}/items/${S.resolve.it.id}/resolve`, { recordId: S.resolve.pick, note: $('#note').value });
      $('#layer').innerHTML = ''; S.resolve = null; toast(r.message); await loadJob(true);
    }, el),
    'approve': () => { if (!$('#ack').checked) return toast('Tick the confirmation box first.', true); openApprove(); },
    'confirm-approve': () => guard(async () => {
      await api('POST', `/api/jobs/${encodeURIComponent(S.job.id)}/approve`, { acknowledge: true });
      $('#layer').innerHTML = ''; S.items = { status: 'EXCEPTIONS', q: '', page: 1 }; await loadJob(); window.scrollTo(0, 0);
    }, el),
    'pause': () => guard(async () => { const r = await api('POST', `/api/jobs/${encodeURIComponent(S.job.id)}/pause`); toast(r.message); }, el),
    'resume': () => guard(async () => { await api('POST', `/api/jobs/${encodeURIComponent(S.job.id)}/resume`); await loadJob(); }, el),
    'retry': () => guard(async () => { const r = await api('POST', `/api/jobs/${encodeURIComponent(S.job.id)}/retry-failed`); toast(`${fmt(r.retried)} failed files queued again.`); S.jobStep[S.job.id] = null; await loadJob(); }, el),
    'delete-job': () => {
      $('#layer').innerHTML = `<div class="scrim modal-wrap" data-action="close-layer"><div class="modal" role="dialog" aria-modal="true"><h2>Delete ${esc(S.job.id)}?</h2><div class="small muted">The job, its results and its uploaded mapping file are removed. Documents already copied to the destination are not touched. This is recorded in the audit log.</div><div class="foot"><button class="btn" data-action="close-layer">Cancel</button><button class="btn danger" data-action="confirm-delete">Delete job</button></div></div></div>`;
    },
    'confirm-delete': () => guard(async () => { await api('DELETE', `/api/jobs/${encodeURIComponent(S.job.id)}`); $('#layer').innerHTML = ''; toast('Job deleted.'); location.hash = '#/jobs'; }, el),
    'audit-page': () => guard(async () => { S.audit.page = +el.dataset.page; $('#main').innerHTML = await vAudit(); }),
    'save-settings': () => guard(async () => {
      const s = (await api('GET', '/api/settings')).settings;
      document.querySelectorAll('[data-set]').forEach(i => { s[i.dataset.set] = i.type === 'checkbox' ? i.checked : +i.value; });
      await api('PUT', '/api/settings', s); S.meta = await api('GET', '/api/meta'); toast('Settings saved and recorded in the audit log.'); route();
    }, el),
    'change-pw': () => guard(async () => {
      const r = await api('POST', '/api/account/password', { currentPassword: $('#pw0').value, newPassword: $('#pw1').value });
      $('#pw0').value = ''; $('#pw1').value = ''; toast(r.message);
    }, el),
    'add-user': () => guard(async () => {
      await api('POST', '/api/users', { userName: $('#nu').value.trim(), displayName: $('#nd').value.trim(), role: $('#nr').value, password: $('#np').value });
      toast('User added.'); route();
    }, el),
    'user-toggle': () => guard(async () => { await api('PUT', `/api/users/${encodeURIComponent(el.dataset.user)}`, { active: el.dataset.active !== 'true' }); route(); }, el),
    'user-reset': () => {
      const u = el.dataset.user;
      $('#layer').innerHTML = `<div class="scrim modal-wrap" data-action="close-layer"><div class="modal" role="dialog" aria-modal="true"><h2>Reset password for ${esc(u)}</h2>
        <div class="field"><label for="rp">New password</label><input class="input" id="rp" type="password" autocomplete="new-password"><span class="hint">Share it with the user securely. They can change it under Settings.</span></div>
        <div class="foot"><button class="btn" data-action="close-layer">Cancel</button><button class="btn primary" data-action="confirm-reset" data-user="${esc(u)}">Reset password</button></div></div></div>`;
      $('#rp').focus();
    },
    'confirm-reset': () => guard(async () => { await api('PUT', `/api/users/${encodeURIComponent(el.dataset.user)}`, { password: $('#rp').value }); $('#layer').innerHTML = ''; toast('Password reset.'); }, el)
  };
  if (handlers[a]) handlers[a]();
});

$('#logout').addEventListener('click', async () => { try { await api('POST', '/api/auth/logout'); } catch { } location.href = '/login.html'; });
window.addEventListener('hashchange', route);

(async function init() {
  try {
    S.me = await api('GET', '/api/auth/me');
    S.meta = await api('GET', '/api/meta');
  } catch { return; }
  if (!location.hash) location.hash = '#/dashboard';
  route();
})();
