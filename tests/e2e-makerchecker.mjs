// End-to-end test of maker-checker (four-eyes): submit / withdraw / reject / approve, who may act,
// locking while submitted, and change requests for exclusions after approval.
// Turns the setting on for the test and back off at the end.
// Usage: node tests/e2e-makerchecker.mjs [baseUrl] [adminPassword]
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
const check = (cond, msg) => { console.log(`${cond ? 'PASS' : 'FAIL'}  ${msg}`); if (!cond) failures++; };
const sleep = ms => new Promise(r => setTimeout(r, ms));
async function fails(p) { try { await p; return null; } catch (e) { return e; } }
const form = files => { const fd = new FormData(); for (const [n, t] of files) fd.append('files', new Blob([t]), n); return fd; };

const admin = client(), op1 = client(), op2 = client(), viewer = client();
const root = mkdtempSync(join(tmpdir(), 'docorg-mc-'));
const src = join(root, 'source'); mkdirSync(src, { recursive: true });
for (const f of ['a.pdf', 'b.pdf', 'c.pdf', 'u1.pdf', 'u2.pdf', 'amb.pdf']) writeFileSync(join(src, f), `%PDF ${f}\n`);
const csv = ['ID,EmployeeCode,Type,UploadedOn,FileName', 'R1,E1,Offer,2024-01-01,a.pdf', 'R2,E2,Offer,2024-01-01,b.pdf', 'R3,E3,Offer,2024-01-01,c.pdf',
  'R4,E4,Offer,2024-01-01,gone.pdf', 'R5,E5,Offer,2024-01-01,NULL', 'R6,E6,Offer,2024-01-01,amb.pdf', 'R7,E7,PAN,2024-01-01,amb.pdf'].join('\r\n');
async function waitFor(c, id, statuses) {
  for (let i = 0; i < 240; i++) { const j = await c('GET', `/api/jobs/${id}`); if (statuses.includes(j.status) && !j.running) return j; await sleep(300); }
  throw new Error('timeout');
}
let settingsBefore;

(async () => {
  try {
    await admin('POST', '/api/auth/login', { userName: 'admin', password });
    for (const [u, role] of [['mc.maker', 'Operator'], ['mc.checker', 'Operator'], ['mc.viewer', 'Viewer']])
      await admin('POST', '/api/users', { userName: u, displayName: u, role, password: 'Maker-Checker-2026!' }).catch(() => {});
    await op1('POST', '/api/auth/login', { userName: 'mc.maker', password: 'Maker-Checker-2026!' });
    await op2('POST', '/api/auth/login', { userName: 'mc.checker', password: 'Maker-Checker-2026!' });
    await viewer('POST', '/api/auth/login', { userName: 'mc.viewer', password: 'Maker-Checker-2026!' });
    settingsBefore = (await admin('GET', '/api/settings')).settings;
    const notAdmin = await fails(op1('PUT', '/api/settings', { ...settingsBefore, requireSecondApprover: false }));
    check(notAdmin?.status === 403, 'only an administrator can change the maker-checker setting');
    await admin('PUT', '/api/settings', { ...settingsBefore, requireSecondApprover: true });
    const audit0 = await admin('GET', '/api/audit?q=RequireSecondApprover&pageSize=10');
    check(audit0.items.some(a => /RequireSecondApprover: False → True/.test(a.details)), 'turning maker-checker on is in the audit log');

    // maker prepares the job
    const { id } = await op1('POST', '/api/jobs', { name: 'E2E maker-checker', source: { type: 'Local', path: src }, destination: { type: 'Local', path: join(root, 'out') } });
    const e0 = await fails(op1('POST', `/api/jobs/${id}/submit`, {}));
    check(e0?.status === 400, 'a job cannot be submitted before it is scanned');
    await op1('POST', `/api/jobs/${id}/mappings`, form([['log.csv', csv]]));
    await op1('POST', `/api/jobs/${id}/scan`);
    let j = await waitFor(op1, id, ['Awaiting approval']);
    check(j.makerChecker === true && JSON.stringify(j.makers) === '["mc.maker"]', `maker recorded (${JSON.stringify(j.makers)})`);
    const direct = await fails(op1('POST', `/api/jobs/${id}/approve`, { acknowledge: true }));
    check(direct?.status === 400 && /submit/i.test(JSON.stringify(direct.body)), 'with maker-checker on, a job must be submitted before approval');
    const viewerSubmit = await fails(viewer('POST', `/api/jobs/${id}/submit`, {}));
    check(viewerSubmit?.status === 403, 'a viewer cannot submit');

    await op1('POST', `/api/jobs/${id}/submit`, { note: 'Please check the blank file name' });
    j = await op1('GET', `/api/jobs/${id}`);
    check(j.submittedBy === 'mc.maker' && j.submitNote === 'Please check the blank file name', 'submitted, with the note');
    const list = await op2('GET', '/api/jobs');
    check(list.find(x => x.id === id)?.submittedBy === 'mc.maker', 'the jobs list shows who submitted it');
    const twice = await fails(op1('POST', `/api/jobs/${id}/submit`, {}));
    check(twice?.status === 400, 'cannot submit twice');

    // locked while submitted
    const locks = await Promise.all([
      fails(op1('PUT', `/api/jobs/${id}/config`, j.config)),
      fails(op2('POST', `/api/jobs/${id}/mappings`, form([['more.csv', csv.replace('R1,', 'X1,')]]))),
      fails(op1('POST', `/api/jobs/${id}/scan`)),
      fails(op2('POST', `/api/jobs/${id}/items/exclude`, { allInView: true, status: 'INVALID', note: 'try while locked' })),
      fails(op1('POST', `/api/jobs/${id}/items/${(await op1('GET', `/api/jobs/${id}/items?status=AMBIGUOUS`)).items[0].id}/resolve`, { recordId: 'R6' })),
    ]);
    check(locks.every(e => e?.status === 400 && /submitted for approval/.test(JSON.stringify(e.body))), `rules, mapping, scan, exclude and resolve are locked while submitted (${locks.map(e => e?.status).join(',')})`);

    // who can act
    const selfApprove = await fails(op1('POST', `/api/jobs/${id}/approve`, { acknowledge: true }));
    check(selfApprove?.status === 403, 'the maker cannot approve their own job');
    const selfReject = await fails(op1('POST', `/api/jobs/${id}/reject`, { note: 'my own' }));
    check(selfReject?.status === 403, 'the maker cannot reject their own job (they withdraw instead)');
    const viewerApprove = await fails(viewer('POST', `/api/jobs/${id}/approve`, { acknowledge: true }));
    check(viewerApprove?.status === 403, 'a viewer cannot approve');
    const otherWithdraw = await fails(op2('POST', `/api/jobs/${id}/withdraw`));
    check(otherWithdraw?.status === 403, 'another operator cannot withdraw someone else\'s submission');
    const noReason = await fails(op2('POST', `/api/jobs/${id}/reject`, { note: '' }));
    check(noReason?.status === 400, 'rejecting needs a reason');

    // checker rejects; maker fixes; the checker then works on the job and so can no longer approve it
    await op2('POST', `/api/jobs/${id}/reject`, { note: 'Exclude the NULL row first' });
    j = await op1('GET', `/api/jobs/${id}`);
    check(!j.submittedBy && /Rejected by mc.checker: Exclude the NULL row first/.test(j.message), 'rejected: back to the maker with the reason');
    await op2('POST', `/api/jobs/${id}/items/exclude`, { allInView: true, status: 'INVALID', note: 'NULL row not needed' });
    j = await op1('GET', `/api/jobs/${id}`);
    check(j.makers.includes('mc.checker'), 'excluding makes the checker one of the makers');
    await op1('POST', `/api/jobs/${id}/submit`, {});
    const ex = await fails(op2('POST', `/api/jobs/${id}/approve`, { acknowledge: true }));
    check(ex?.status === 403 && /mc.checker/.test(JSON.stringify(ex.body)), 'someone who changed the job cannot approve it');
    // withdraw by the submitter, then submit again
    await op1('POST', `/api/jobs/${id}/withdraw`);
    check(!(await op1('GET', `/api/jobs/${id}`)).submittedBy, 'the submitter can withdraw');
    await op1('POST', `/api/jobs/${id}/submit`, {});
    const noAck = await fails(admin('POST', `/api/jobs/${id}/approve`, { acknowledge: false }));
    check(noAck?.status === 400, 'approval still needs the confirmation');
    const noVersion = await fails(admin('POST', `/api/jobs/${id}/approve`, { acknowledge: true }));
    check(noVersion?.status === 400 && /version/.test(JSON.stringify(noVersion.body)), 'with maker-checker on, approval must say which version was reviewed');
    await admin('POST', `/api/jobs/${id}/approve`, { acknowledge: true, version: (await admin('GET', `/api/jobs/${id}`)).version });
    j = await waitFor(admin, id, ['Completed', 'Completed with exceptions']);
    check(j.approvedBy === 'admin' && j.counts.PROCESSED === 3, `a person who did not work on the job approved it; copied ${j.counts.PROCESSED}`);
    const audit = await admin('GET', `/api/audit?q=${encodeURIComponent(id)}&pageSize=100`);
    const ev = audit.items.map(a => a.event);
    check(['Submitted for approval', 'Approval rejected', 'Submission withdrawn', 'Processing approved'].every(e => ev.includes(e)), 'submit, reject, withdraw and approve are in the audit log');
    check(audit.items.some(a => a.event === 'Processing approved' && /checker admin; submitted by mc.maker; prepared by mc.maker, mc.checker/.test(a.details)), 'the approval entry names checker, submitter and makers');

    // after approval: exclusions become change requests
    const before = j.counts;
    const req = await op1('POST', `/api/jobs/${id}/items/exclude`, { allInView: true, status: 'EXCEPTIONS', note: 'Closing the job' });
    check(req.pending === true && req.count > 0, `after approval, excluding creates a request (${req.count} items)`);
    j = await op1('GET', `/api/jobs/${id}`);
    check(JSON.stringify(j.counts) === JSON.stringify(before) && j.pendingRequests === 1, 'nothing changes until it is approved');
    const ownReq = await fails(op1('POST', `/api/jobs/${id}/requests/${req.requestId}/approve`));
    check(ownReq?.status === 403, 'the requester cannot approve their own request');
    const viewerReq = await fails(viewer('POST', `/api/jobs/${id}/requests/${req.requestId}/approve`));
    check(viewerReq?.status === 403, 'a viewer cannot approve a request');
    const ok = await op2('POST', `/api/jobs/${id}/requests/${req.requestId}/approve`);
    j = await op1('GET', `/api/jobs/${id}`);
    check(ok.count === req.count && j.status === 'Completed' && j.pendingRequests === 0, `approved by a second person: ${ok.message} Job ${j.status}`);
    const it = (await op1('GET', `/api/jobs/${id}/items?status=EXCLUDED&q=gone.pdf`)).items[0];
    check(it?.excludedBy === 'mc.maker' && /Closing the job \(approved by mc.checker\)/.test(it.exclusionNote), 'excluded item records the requester and the approver');
    const again = await fails(op2('POST', `/api/jobs/${id}/requests/${req.requestId}/approve`));
    check(again?.status === 400, 'a decided request cannot be approved again');

    // restore request, rejected; and one withdrawn by its requester
    const rr = await op2('POST', `/api/jobs/${id}/items/restore`, { allInView: true, q: 'gone.pdf', note: 'Need to follow up' });
    check(rr.pending && rr.count === 1, 'restoring after approval also creates a request');
    const rjNoReason = await fails(op1('POST', `/api/jobs/${id}/requests/${rr.requestId}/reject`, { note: '' }));
    check(rjNoReason?.status === 400, 'rejecting someone else\'s request needs a reason');
    await op1('POST', `/api/jobs/${id}/requests/${rr.requestId}/reject`, { note: 'Client confirmed it is lost' });
    const w = await op1('POST', `/api/jobs/${id}/items/restore`, { allInView: true, q: 'gone.pdf' });
    await op1('POST', `/api/jobs/${id}/requests/${w.requestId}/reject`, { note: '' });
    const reqs = await viewer('GET', `/api/jobs/${id}/requests`);
    check(reqs.find(x => x.id === rr.requestId)?.status === 'Rejected' && reqs.find(x => x.id === w.requestId)?.status === 'Withdrawn' && reqs.find(x => x.id === req.requestId)?.status === 'Approved',
      `request history: ${reqs.map(x => `${x.id}:${x.status}`).join(', ')}`);
    check((await op1('GET', `/api/jobs/${id}/items?status=EXCLUDED&q=gone.pdf`)).total === 1, 'rejected and withdrawn requests change nothing');
    const nothing = await fails(op1('POST', `/api/jobs/${id}/items/exclude`, { allInView: true, status: 'EXCEPTIONS', note: 'nothing left' }));
    check(nothing?.status === 400, 'a request with no matching items is refused');

    // ---- races and stale pages (checker findings, iteration 1)
    {
      const { id: rid } = await op1('POST', '/api/jobs', { name: 'E2E mc races', source: { type: 'Local', path: src }, destination: { type: 'Local', path: join(root, 'out-race') } });
      await op1('POST', `/api/jobs/${rid}/mappings`, form([['log.csv', csv]]));
      let bad = 0;
      for (let k = 0; k < 8; k++) {
        await op1('POST', `/api/jobs/${rid}/scan`); await waitFor(op1, rid, ['Awaiting approval']);
        const extra = csv + `\r\nX${k},E9,Offer,2024-01-01,u${1 + (k % 2)}.pdf`;
        await Promise.allSettled([op1('POST', `/api/jobs/${rid}/submit`, {}), op1('POST', `/api/jobs/${rid}/mapping`, form([[`v${k}.csv`, extra]]))]);
        const jj = await op1('GET', `/api/jobs/${rid}`);
        if (jj.submittedBy && jj.status !== 'Awaiting approval') bad++;
        if (jj.submittedBy) await op1('POST', `/api/jobs/${rid}/withdraw`);
      }
      check(bad === 0, 'submit racing a mapping upload never leaves a changed job marked as submitted');
      await op1('POST', `/api/jobs/${rid}/scan`); await waitFor(op1, rid, ['Awaiting approval']);
      await op1('POST', `/api/jobs/${rid}/submit`, {});
      const seen = (await op2('GET', `/api/jobs/${rid}`)).version;
      await op1('POST', `/api/jobs/${rid}/withdraw`);
      await op1('POST', `/api/jobs/${rid}/mapping`, form([['changed.csv', csv + '\r\nZ1,E9,Offer,2024-01-01,u1.pdf']]));
      await op1('POST', `/api/jobs/${rid}/scan`); await waitFor(op1, rid, ['Awaiting approval']);
      await op1('POST', `/api/jobs/${rid}/submit`, {});
      const stale = await fails(admin('POST', `/api/jobs/${rid}/approve`, { acknowledge: true, version: seen }));
      check(stale?.status === 409, 'approval from a page opened before the job changed is refused (409)');
      const fresh = (await admin('GET', `/api/jobs/${rid}`)).version;
      const both = await Promise.allSettled([admin('POST', `/api/jobs/${rid}/approve`, { acknowledge: true, version: fresh }), admin('POST', `/api/jobs/${rid}/approve`, { acknowledge: true, version: fresh })]);
      check(both.filter(x => x.status === 'fulfilled').length === 1, 'two approvals at the same moment: exactly one succeeds');
      await waitFor(admin, rid, ['Completed', 'Completed with exceptions']);
      const au = await admin('GET', `/api/audit?q=${encodeURIComponent(rid)}&pageSize=200`);
      check(au.items.filter(a => a.event === 'Processing approved').length === 1, 'only one approval is recorded');
      // change requests: approve and reject at the same moment, duplicate requests
      let doubles = 0;
      for (let k = 0; k < 6; k++) {
        const rq = await op1('POST', `/api/jobs/${rid}/items/exclude`, { allInView: true, status: 'EXCEPTIONS', note: 'race ' + k });
        const dup = await fails(op1('POST', `/api/jobs/${rid}/items/exclude`, { allInView: true, status: 'EXCEPTIONS', note: 'again' }));
        if (k === 0) check(dup?.status === 400 && /already in pending request/.test(JSON.stringify(dup.body)), 'a second request for items already in a pending request is refused');
        const r2 = await Promise.allSettled([op2('POST', `/api/jobs/${rid}/requests/${rq.requestId}/approve`), op2('POST', `/api/jobs/${rid}/requests/${rq.requestId}/reject`, { note: 'no thanks' })]);
        if (r2.filter(x => x.status === 'fulfilled').length !== 1) doubles++;
        const st = (await op1('GET', `/api/jobs/${rid}/requests`)).find(x => x.id === rq.requestId).status;
        if (st === 'Approved') await op2('POST', `/api/jobs/${rid}/items/restore`, { allInView: true }).then(x => op1('POST', `/api/jobs/${rid}/requests/${x.requestId}/approve`));
      }
      check(doubles === 0, 'approve and reject of the same request at the same moment: exactly one succeeds');
    }

    // with the setting off, a job is approved directly again (single-person installations)
    await admin('PUT', '/api/settings', { ...settingsBefore, requireSecondApprover: false });
    const { id: id2 } = await op1('POST', '/api/jobs', { name: 'E2E no maker-checker', source: { type: 'Local', path: src }, destination: { type: 'Local', path: join(root, 'out2') } });
    await op1('POST', `/api/jobs/${id2}/mappings`, form([['log.csv', csv]]));
    await op1('POST', `/api/jobs/${id2}/scan`); await waitFor(op1, id2, ['Awaiting approval']);
    const sub = await fails(op1('POST', `/api/jobs/${id2}/submit`, {}));
    check(sub?.status === 400, 'with maker-checker off there is nothing to submit');
    await op1('POST', `/api/jobs/${id2}/approve`, { acknowledge: true });
    j = await waitFor(op1, id2, ['Completed', 'Completed with exceptions']);
    check(j.approvedBy === 'mc.maker', 'with maker-checker off the maker approves directly');
    const direct2 = await op1('POST', `/api/jobs/${id2}/items/exclude`, { allInView: true, status: 'INVALID', note: 'direct' });
    check(!direct2.pending && direct2.count === 1, 'with maker-checker off, exclusions after approval apply directly');
  } catch (e) { console.error('ERROR', e.stack || e.message); failures++; }
  finally { if (settingsBefore) await admin('PUT', '/api/settings', { ...settingsBefore, requireSecondApprover: false }).catch(() => {}); }
  console.log(failures ? `\n${failures} check(s) failed` : '\nAll checks passed');
  process.exit(failures ? 1 : 0);
})();
