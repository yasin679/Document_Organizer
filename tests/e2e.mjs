// End-to-end API test for Document Organizer.
// Usage: node tests/e2e.mjs [baseUrl] [adminPassword] [mode: local|cloud|all]
// 'cloud' runs one job per storage connection marked UseForDemo that is reachable (Azurite, MinIO...),
// plus a cross-provider job (Azure Blob -> S3) when both kinds are available.
// Requires Node 18+. Uses demo data, so EnableDemoData must be true.
const base = process.argv[2] || 'http://localhost:5080';
const password = process.argv[3] || process.env.DOCORG_ADMIN_PASSWORD;
const mode = process.argv[4] || 'all';
let cookie = '';
let failures = 0;

async function call(method, path, body, raw = false) {
  const headers = { 'X-DocOrg': '1', cookie };
  let payload;
  if (body instanceof FormData) payload = body;
  else if (body !== undefined) { headers['content-type'] = 'application/json'; payload = JSON.stringify(body); }
  const res = await fetch(base + path, { method, headers, body: payload, redirect: 'manual' });
  const set = res.headers.get('set-cookie'); if (set) cookie = set.split(';')[0];
  if (raw) return res;
  const text = await res.text();
  let json; try { json = text ? JSON.parse(text) : null; } catch { json = text; }
  if (!res.ok) throw new Error(`${method} ${path} -> ${res.status}: ${JSON.stringify(json)}`);
  return json;
}
function check(cond, msg) { console.log(`${cond ? 'PASS' : 'FAIL'}  ${msg}`); if (!cond) failures++; }
const sleep = ms => new Promise(r => setTimeout(r, ms));
async function waitFor(id, statuses, timeoutMs = 120000) {
  const t0 = Date.now();
  while (Date.now() - t0 < timeoutMs) {
    const j = await call('GET', `/api/jobs/${id}`);
    if (statuses.includes(j.status) && !j.running) return j;
    await sleep(500);
  }
  throw new Error(`Timed out waiting for ${statuses}`);
}

async function runJob(label, source, destination) {
  console.log(`\n=== ${label} ===`);
  const created = await call('POST', '/api/jobs', { name: `E2E ${label}`, source, destination });
  const id = created.id; check(!!id, `job created ${id}`);
  const m = await call('POST', `/api/jobs/${id}/mapping/demo`);
  check(m.rows > 400, `mapping read: ${m.rows} rows, headers ${m.headers.join(',')}`);
  check(m.columns.employeeCode === 'EmployeeCode' && m.columns.fileName === 'FileName', 'columns auto-mapped');
  const job0 = await call('GET', `/api/jobs/${id}`);
  await call('PUT', `/api/jobs/${id}/config`, { ...job0.config, columns: m.columns });
  await call('POST', `/api/jobs/${id}/scan`);
  const j = await waitFor(id, ['Awaiting approval', 'Draft']);
  check(j.status === 'Awaiting approval', `scan finished: ${j.status} ${j.message || ''}`);
  const c = j.counts; console.log('   counts', JSON.stringify(c), 'discovered', j.discovered);
  const fileItems = c.READY + c.UNMAPPED + c.AMBIGUOUS + c.INVALID + c.DUPLICATE;
  check(c.UNMAPPED === 7, 'unmapped = 7');
  check(c.MISSING === 3, 'missing = 3');
  check(c.AMBIGUOUS === 2, 'ambiguous = 2');
  check(c.INVALID === 5, 'invalid = 5 (blank emp, blank type, no date, 0-byte, blank filename)');
  check(c.DUPLICATE === 2, 'duplicate = 2 (duplicate record + duplicate physical file)');
  check(j.discovered === c.READY + c.UNMAPPED + c.AMBIGUOUS + c.DUPLICATE - 1 + c.INVALID - 1, 'every discovered file accounted for');
  const amb = await call('GET', `/api/jobs/${id}/items?status=AMBIGUOUS`);
  const first = amb.items[0];
  check(first.candidates.length === 2, `ambiguous item has 2 candidates (${first.fileName})`);
  const res = await call('POST', `/api/jobs/${id}/items/${first.id}/resolve`, { recordId: first.candidates[0].recordId, note: 'E2E test' });
  check(/Resolved/.test(res.message), 'ambiguous resolved');
  const ex = await call('GET', `/api/jobs/${id}/items?status=READY&q=e00879_final`);
  check(ex.items[0]?.destKey === 'E00879/Medical Certificate/2021/e00879_final_certificate.pdf', `reference example path: ${ex.items[0]?.destKey}`);
  const ci = await call('GET', `/api/jobs/${id}/items?q=e00951`);
  check(ci.items[0]?.status === 'READY', 'case-insensitive match');
  const nm = await call('GET', `/api/jobs/${id}/items?q=E00952`);
  check(nm.items[0]?.status === 'READY', 'normalized match');
  const tree = await call('GET', `/api/jobs/${id}/tree`);
  check(tree.employees > 100 && tree.sample.length > 0, `tree preview: ${tree.employees} employees, ${tree.documentTypes} types, ${tree.firstYear}-${tree.lastYear}`);
  const vr = await call('GET', `/api/jobs/${id}/report?kind=validation&format=xlsx`, undefined, true);
  const vbuf = Buffer.from(await vr.arrayBuffer());
  check(vr.ok && vbuf[0] === 0x50 && vbuf[1] === 0x4b, `validation report xlsx (${vbuf.length} bytes)`);
  const ready = (await call('GET', `/api/jobs/${id}`)).counts.READY;
  await call('POST', `/api/jobs/${id}/approve`, { acknowledge: true });
  const done = await waitFor(id, ['Completed', 'Completed with exceptions', 'Paused']);
  check(done.counts.PROCESSED === ready, `processed ${done.counts.PROCESSED} of ${ready} ready (failed ${done.counts.FAILED})`);
  const pr = await call('GET', `/api/jobs/${id}/items?status=PROCESSED&pageSize=1`);
  check(pr.items[0].sourceHash && pr.items[0].sourceHash === pr.items[0].destHash, `integrity ${pr.items[0].integrityMethod} recorded and matching`);
  const rr = await call('GET', `/api/jobs/${id}/report?kind=reconciliation&format=csv`, undefined, true);
  const csv = await rr.text();
  const lines = csv.trim().split('\r\n');
  check(lines[0].startsWith('﻿Job ID') || lines[0].startsWith('Job ID'), 'reconciliation CSV header');
  check(lines.length - 1 === Object.values(done.counts).reduce((a, b) => a + b, 0), `reconciliation has one row per item (${lines.length - 1})`);
  try { await call('POST', `/api/jobs/${id}/retry-failed`); check(false, 'retry with no failures should be rejected'); }
  catch (e) { check(/no failed files/.test(e.message), 'retry rejected when nothing failed'); }
  return id;
}

(async () => {
  try {
    await call('POST', '/api/auth/login', { userName: 'admin', password });
    check(true, 'admin login');
    try { await fetch(base + '/api/jobs', { method: 'POST', headers: { cookie, 'content-type': 'application/json' }, body: '{}' }).then(r => { if (r.status !== 400) throw 0; }); check(true, 'CSRF header required'); } catch { check(false, 'CSRF header required'); }
    const demo = await call('POST', '/api/demo');
    console.log('   demo', JSON.stringify(demo));
    if (mode === 'local' || mode === 'all')
      await runJob('local folder', { type: 'Local', path: demo.localSource }, { type: 'Local', path: demo.localDestination });
    if (mode === 'cloud' || mode === 'all') {
      const cloud = demo.cloud || [];
      if (!cloud.length) check(false, 'no cloud demo connection reachable: ' + demo.cloudNote);
      const loc = (c, dest) => ({ type: c.provider, connection: c.connection, container: dest ? c.destinationContainer : c.sourceContainer, prefix: dest ? `e2e-${Date.now()}/` : c.sourcePrefix });
      for (const c of cloud) await runJob(`${c.provider} (${c.connection})`, loc(c), loc(c, true));
      const az = cloud.find(c => c.provider === 'AzureBlob'), s3 = cloud.find(c => c.provider === 'S3');
      for (const c of cloud) await runJob(`${c.connection} -> local folder`, loc(c), { type: 'Local', path: `${demo.localDestination}-from-${c.connection}` });
      if (az && s3) await runJob(`${az.connection} -> ${s3.connection}`, loc(az), loc(s3, true));
      if (az && s3) await runJob(`${s3.connection} -> ${az.connection}`, loc(s3), loc(az, true));
    }

    // Roles
    await call('POST', '/api/users', { userName: 'viewer1', displayName: 'Viewer One', role: 'Viewer', password: 'View-Only-2026!' }).catch(() => {});
    const adminCookie = cookie; cookie = '';
    await call('POST', '/api/auth/login', { userName: 'viewer1', password: 'View-Only-2026!' });
    try { await call('POST', '/api/demo'); check(false, 'viewer blocked from operating'); } catch (e) { check(/403/.test(e.message), 'viewer blocked from operating (403)'); }
    const jl = await call('GET', '/api/jobs'); check(jl.length >= 1, 'viewer can list jobs');
    cookie = adminCookie;
    const audit = await call('GET', '/api/audit');
    check(audit.items.some(a => a.event === 'Processing approved') && audit.items.some(a => a.event === 'Ambiguity resolved'), `audit log has approvals and resolutions (${audit.total} events)`);
  } catch (e) { console.error('ERROR', e.message); failures++; }
  console.log(failures ? `\n${failures} check(s) failed` : '\nAll checks passed');
  process.exit(failures ? 1 : 0);
})();
