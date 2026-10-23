// End-to-end test of bulk exclusion of exceptions (v2.1): ticked rows, everything in a view, a whole reason;
// restore; exclusions remembered across scans; completed job status; reports; audit; roles.
// Usage: node tests/e2e-exclusions.mjs [baseUrl] [adminPassword]
import { mkdtempSync, mkdirSync, writeFileSync } from 'fs';
import { tmpdir } from 'os';
import { join } from 'path';

const base = process.argv[2] || 'http://localhost:5080';
const password = process.argv[3] || process.env.DOCORG_ADMIN_PASSWORD;
let failures = 0;
function client() {
  let cookie = '';
  return async function call(method, path, body, raw = false) {
    const headers = { 'X-DocOrg': '1', cookie }; let payload;
    if (body instanceof FormData) payload = body; else if (body !== undefined) { headers['content-type'] = 'application/json'; payload = JSON.stringify(body); }
    const res = await fetch(base + path, { method, headers, body: payload });
    const set = res.headers.get('set-cookie'); if (set) cookie = set.split(';')[0];
    if (raw) return res;
    const text = await res.text(); let json; try { json = text ? JSON.parse(text) : null; } catch { json = text; }
    if (!res.ok) { const e = new Error(`${method} ${path} -> ${res.status}: ${JSON.stringify(json)}`); e.status = res.status; e.body = json; throw e; }
    return json;
  };
}
const call = client();
const check = (cond, msg) => { console.log(`${cond ? 'PASS' : 'FAIL'}  ${msg}`); if (!cond) failures++; };
const sleep = ms => new Promise(r => setTimeout(r, ms));
async function fails(p) { try { await p; return null; } catch (e) { return e; } }
async function waitFor(id, statuses) {
  for (let i = 0; i < 240; i++) { const j = await call('GET', `/api/jobs/${id}`); if (statuses.includes(j.status) && !j.running) return j; await sleep(400); }
  throw new Error('timeout');
}
const form = files => { const fd = new FormData(); for (const [n, t] of files) fd.append('files', new Blob([t]), n); return fd; };
const items = async (id, q = 'status=ALL') => (await call('GET', `/api/jobs/${id}/items?${q}&pageSize=500`)).items;

const root = mkdtempSync(join(tmpdir(), 'docorg-excl-'));
const src = join(root, 'source');
mkdirSync(src, { recursive: true });
for (const f of ['a.pdf', 'b.pdf', 'u1.pdf', 'u2.pdf', 'u3.pdf', 'amb.pdf']) writeFileSync(join(src, f), `%PDF ${f}\n`);
const csv = [
  'ID,EmployeeCode,Type,UploadedOn,FileName',
  'R1,E1,Offer,2024-01-01,a.pdf',      // READY
  'R2,E2,Offer,2024-01-01,b.pdf',      // READY
  'R3,E3,Offer,2024-01-01,m1.pdf',     // MISSING
  'R4,E4,Offer,2024-01-01,m2.pdf',     // MISSING (file added later)
  'R5,E5,Offer,2024-01-01,NULL',       // INVALID blank file name
  'R6,E6,Offer,2024-01-01,',           // INVALID blank file name
  'R7,,Offer,2024-01-01,c.pdf',        // MISSING (c.pdf not in source)
  'R8,E8,Offer,2024-01-01,amb.pdf',    // AMBIGUOUS
  'R9,E9,PAN,2024-01-01,amb.pdf',      // AMBIGUOUS
].join('\r\n');

(async () => {
  try {
    await call('POST', '/api/auth/login', { userName: 'admin', password });
    const { id } = await call('POST', '/api/jobs', { name: 'E2E exclusions', source: { type: 'Local', path: src }, destination: { type: 'Local', path: join(root, 'out') } });
    await call('POST', `/api/jobs/${id}/mappings`, form([['log.csv', csv]]));
    const e0 = await fails(call('POST', `/api/jobs/${id}/items/exclude`, { allInView: true, note: 'too early' }));
    check(e0?.status === 400, 'nothing can be excluded before a scan');
    await call('POST', `/api/jobs/${id}/scan`);
    let j = await waitFor(id, ['Awaiting approval']);
    console.log('   counts', JSON.stringify(j.counts));
    check(j.counts.UNMAPPED === 3 && j.counts.MISSING === 3 && j.counts.INVALID === 2 && j.counts.AMBIGUOUS === 1 && j.counts.EXCLUDED === 0, 'starting counts');

    const reasons = await call('GET', `/api/jobs/${id}/reasons`);
    const rg = (st, g) => reasons.find(r => r.status === st && r.group === g)?.count;
    check(rg('INVALID', 'Blank file name') === 2 && rg('UNMAPPED', 'No mapping record') === 3 && rg('MISSING', 'File not in the source') === 3 && rg('AMBIGUOUS', 'Several records match') === 1,
      `exceptions grouped by reason: ${reasons.map(r => `${r.status}/${r.group}=${r.count}`).join(', ')}`);

    // 1. ticked rows
    const all = await items(id);
    const u1 = all.find(i => i.fileName === 'u1.pdf'), ready = all.find(i => i.status === 'READY');
    const noNote = await fails(call('POST', `/api/jobs/${id}/items/exclude`, { ids: [u1.id] }));
    check(noNote?.status === 400 && /note/i.test(JSON.stringify(noNote.body)), 'a note is required');
    const onlyReady = await fails(call('POST', `/api/jobs/${id}/items/exclude`, { ids: [ready.id], note: 'should fail' }));
    check(onlyReady?.status === 400, 'ready items cannot be excluded');
    let r = await call('POST', `/api/jobs/${id}/items/exclude`, { ids: [u1.id, ready.id], note: 'Scanner test page, not a document' });
    check(r.count === 1, `ticked rows: only the exception is excluded (${r.message})`);
    let it = (await items(id)).find(i => i.id === u1.id);
    check(it.status === 'EXCLUDED' && it.excludedFrom === 'UNMAPPED' && it.excludedBy === 'admin' && it.exclusionNote === 'Scanner test page, not a document' && /No mapping record/.test(it.reason),
      'excluded item keeps its original status, reason, who and why');

    // 2. a whole reason
    r = await call('POST', `/api/jobs/${id}/items/exclude`, { allInView: true, status: 'INVALID', group: 'Blank file name', note: 'Blank file names confirmed by client' });
    check(r.count === 2, `whole reason excluded (${r.count})`);
    // 3. everything in a view with a search
    r = await call('POST', `/api/jobs/${id}/items/exclude`, { allInView: true, status: 'MISSING', q: 'm1.pdf', note: 'Lost file, accepted' });
    check(r.count === 1, 'everything in a searched view excluded');
    const bad = await fails(call('POST', `/api/jobs/${id}/items/exclude`, { allInView: true, status: 'READY', note: 'no' }));
    check(bad?.status === 400, 'a view of ready items is refused');
    const noTarget = await fails(call('POST', `/api/jobs/${id}/items/exclude`, { note: 'no target' }));
    check(noTarget?.status === 400, 'a request without rows or "all in view" is refused');

    j = await call('GET', `/api/jobs/${id}`);
    check(j.counts.EXCLUDED === 4 && j.counts.UNMAPPED === 2 && j.counts.INVALID === 0 && j.counts.MISSING === 2, `counts after excluding: ${JSON.stringify(j.counts)}`);
    const exc = await call('GET', `/api/jobs/${id}/items?status=EXCEPTIONS&pageSize=100`);
    check(exc.total === 5 && exc.items.every(i => i.status !== 'EXCLUDED'), 'the exceptions view no longer shows excluded items');
    const exReasons = await call('GET', `/api/jobs/${id}/reasons?excluded=true`);
    check(exReasons.some(x => x.status === 'INVALID' && x.group === 'Blank file name' && x.count === 2), 'excluded items grouped by original status and reason');

    // roles
    await call('POST', '/api/users', { userName: 'exclviewer', displayName: 'Excl Viewer', role: 'Viewer', password: 'View-Only-2026!' }).catch(() => {});
    const viewer = client(); await viewer('POST', '/api/auth/login', { userName: 'exclviewer', password: 'View-Only-2026!' });
    const v = await fails(viewer('POST', `/api/jobs/${id}/items/exclude`, { allInView: true, note: 'viewer' }));
    const v2 = await fails(viewer('POST', `/api/jobs/${id}/items/restore`, { allInView: true }));
    check(v?.status === 403 && v2?.status === 403, 'a viewer cannot exclude or restore');
    check((await viewer('GET', `/api/jobs/${id}/items?status=EXCLUDED`)).total === 4, 'a viewer can see excluded items');

    // remembered across scans; an item whose status changed is not excluded again
    writeFileSync(join(src, 'm2.pdf'), '%PDF m2\n');
    r = await call('POST', `/api/jobs/${id}/items/exclude`, { allInView: true, status: 'MISSING', q: 'm2.pdf', note: 'Will arrive later' });
    await call('POST', `/api/jobs/${id}/scan`);
    j = await waitFor(id, ['Awaiting approval']);
    const after = await items(id);
    check(j.counts.EXCLUDED === 4 && after.find(i => i.fileName === 'u1.pdf')?.status === 'EXCLUDED' && after.filter(i => i.reason && /Blank FileName/.test(i.reason)).every(i => i.status === 'EXCLUDED'),
      `exclusions applied again after a new scan (${JSON.stringify(j.counts)})`);
    check(after.find(i => i.recordId === 'R4')?.status === 'READY', 'an excluded MISSING record whose file has arrived is READY, not excluded');

    // restore
    r = await call('POST', `/api/jobs/${id}/items/restore`, { ids: [after.find(i => i.fileName === 'u1.pdf').id] });
    check(r.count === 1 && (await items(id)).find(i => i.fileName === 'u1.pdf').status === 'UNMAPPED', 'restored item is UNMAPPED again');
    r = await call('POST', `/api/jobs/${id}/items/restore`, { allInView: true, group: 'Blank file name' });
    check(r.count === 2, 'a whole reason restored');
    await call('POST', `/api/jobs/${id}/scan`);
    j = await waitFor(id, ['Awaiting approval']);
    check(j.counts.EXCLUDED === 1 && j.counts.INVALID === 2 && j.counts.UNMAPPED === 3, `restored items stay restored after a new scan (${JSON.stringify(j.counts)})`);

    // process, then tidy up the remaining exceptions on the completed job
    const amb = (await items(id, 'status=AMBIGUOUS'))[0];
    await call('POST', `/api/jobs/${id}/items/${amb.id}/resolve`, { recordId: amb.candidates[0].recordId });
    await call('POST', `/api/jobs/${id}/approve`, { acknowledge: true });
    j = await waitFor(id, ['Completed', 'Completed with exceptions']);
    check(j.status === 'Completed with exceptions', `after copy: ${j.status} ${JSON.stringify(j.counts)}`);
    r = await call('POST', `/api/jobs/${id}/items/exclude`, { allInView: true, status: 'EXCEPTIONS', note: 'Reviewed with client, closing job' });
    j = await call('GET', `/api/jobs/${id}`);
    check(j.status === 'Completed' && Object.entries(j.counts).every(([k, n]) => ['PROCESSED', 'EXCLUDED'].includes(k) || n === 0), `excluding every exception completes the job (${r.count} excluded, ${j.status})`);
    await call('POST', `/api/jobs/${id}/items/restore`, { ids: [(await items(id, 'status=EXCLUDED'))[0].id] });
    check((await call('GET', `/api/jobs/${id}`)).status === 'Completed with exceptions', 'restoring one makes it "with exceptions" again');

    const rep = await (await call('GET', `/api/jobs/${id}/report?kind=reconciliation&format=csv`, undefined, true)).text();
    const lines = rep.trim().split('\r\n');
    check(lines[0].endsWith('Excluded From Status,Excluded By,Excluded At (UTC),Exclusion Note'), 'reconciliation report has the exclusion columns at the end');
    check(lines.some(l => l.includes(',EXCLUDED,') && l.includes('Reviewed with client') && l.includes(',admin,')), 'excluded rows in the report carry status, who and note');
    const val = await (await call('GET', `/api/jobs/${id}/report?kind=validation&format=csv`, undefined, true)).text();
    check(val.split('\r\n')[0].includes('Exclusion Note'), 'validation report has the exclusion columns');
    const audit = await call('GET', `/api/audit?q=${encodeURIComponent(id)}&pageSize=100`);
    check(audit.items.some(a => a.event === 'Exceptions excluded' && /Blank file name/.test(a.details)) && audit.items.some(a => a.event === 'Exclusions restored'), 'audit log records exclusions and restores');
  } catch (e) { console.error('ERROR', e.stack || e.message); failures++; }
  console.log(failures ? `\n${failures} check(s) failed` : '\nAll checks passed');
  process.exit(failures ? 1 : 0);
})();
