// End-to-end test of mapping-file handling (v2.1):
//  - a File name cell listing several files becomes one entry per file
//  - a real file name that contains a comma is not split
//  - NULL / N/A placeholders are treated as blank (INVALID, not MISSING)
//  - a repeated record whose file is missing is MISSING, not DUPLICATE
//  - several mapping files with different column names in one job
// Usage: node tests/e2e-mapping.mjs [baseUrl] [adminPassword]
// The application must run on this machine (the test writes a local source folder).
import { mkdtempSync, mkdirSync, writeFileSync, readFileSync } from 'fs';
import { tmpdir } from 'os';
import { join } from 'path';

const base = process.argv[2] || 'http://localhost:5080';
const password = process.argv[3] || process.env.DOCORG_ADMIN_PASSWORD;
let cookie = '', failures = 0;

async function call(method, path, body, raw = false) {
  const headers = { 'X-DocOrg': '1', cookie };
  let payload;
  if (body instanceof FormData) payload = body;
  else if (body !== undefined) { headers['content-type'] = 'application/json'; payload = JSON.stringify(body); }
  const res = await fetch(base + path, { method, headers, body: payload });
  const set = res.headers.get('set-cookie'); if (set) cookie = set.split(';')[0];
  if (raw) return res;
  const text = await res.text(); let json; try { json = text ? JSON.parse(text) : null; } catch { json = text; }
  if (!res.ok) throw new Error(`${method} ${path} -> ${res.status}: ${JSON.stringify(json)}`);
  return json;
}
const check = (cond, msg) => { console.log(`${cond ? 'PASS' : 'FAIL'}  ${msg}`); if (!cond) failures++; };
const sleep = ms => new Promise(r => setTimeout(r, ms));
async function waitFor(id, statuses) {
  for (let i = 0; i < 240; i++) { const j = await call('GET', `/api/jobs/${id}`); if (statuses.includes(j.status) && !j.running) return j; await sleep(500); }
  throw new Error('timeout');
}
const form = files => { const fd = new FormData(); for (const [name, text] of files) fd.append('files', new Blob([text]), name); return fd; };

// ---------- test data ----------
const root = mkdtempSync(join(tmpdir(), 'docorg-mapping-'));
const src = join(root, 'source'), dst = join(root, 'organized');
mkdirSync(join(src, 'docs'), { recursive: true });
for (const f of ['a1.pdf', 'a2.pdf', 'a3.jpg', 'report, final.pdf', 'dup.pdf', 'x1.pdf', 'b1.pdf', 'z.pdf'])
  writeFileSync(join(src, 'docs', f), `%PDF-1.4 sample ${f}\n`);

const map1 = [
  'EmployeeCode,Type,Name,UploadedOn,FileName',
  'E1,Offer,Offer,2024-01-01,"a1.pdf,a2.pdf, a3.jpg"',     // ROW-2 -> three entries
  'E2,Payslip,Payslip,2024-02-01,"report, final.pdf"',     // ROW-3 real file with a comma
  'E3,PAN,PAN,2024-03-01,NULL',                            // ROW-4 placeholder -> INVALID
  'E4,Confirmation,Conf,2024-04-01,missing1.pdf',          // ROW-5 MISSING
  'E4,Confirmation,Conf,2024-04-02,missing1.pdf',          // ROW-6 repeat of a missing record -> MISSING
  'E5,Photo,Photo,2024-05-01,dup.pdf',                     // ROW-7 READY
  'E5,Photo,Photo,2024-05-02,dup.pdf',                     // ROW-8 repeat of a found record -> DUPLICATE
  'E6,Bank,Bank,2024-06-01,"x1.pdf; missing2.pdf"',        // ROW-9 -> x1 READY, missing2 MISSING
].join('\r\n');
const map2 = [                                              // different column names, own IDs
  'ID,EmpCode,DocType,UploadDate,StoredFileName',
  '1,E7,Form16,2023-06-01,b1.pdf',                          // READY
  '2,E1,Offer,2024-01-01,a1.pdf',                           // repeats ROW-2/1 of map1 (found) -> DUPLICATE
].join('\r\n');

(async () => {
  try {
    await call('POST', '/api/auth/login', { userName: 'admin', password });
    const { id } = await call('POST', '/api/jobs', { name: 'E2E mapping files', source: { type: 'Local', path: src }, destination: { type: 'Local', path: dst } });

    // two files in one upload
    const up = await call('POST', `/api/jobs/${id}/mappings`, form([['map1.csv', map1], ['map2.csv', map2]]));
    check(up.files.length === 2 && up.rows === 10, `two mapping files uploaded together (${up.files.length} files, ${up.rows} rows)`);
    const m2 = up.files[1].id;
    check(up.fileColumns[m2].employeeCode === 'EmpCode' && up.fileColumns[m2].fileName === 'StoredFileName' && up.fileColumns[m2].recordId === 'ID',
      'second file auto-mapped with its own column names');

    // remove and add again (one at a time)
    await call('DELETE', `/api/jobs/${id}/mappings/${m2}`);
    let job = await call('GET', `/api/jobs/${id}`);
    check(job.mappings.length === 1, 'mapping file removed');
    const again = await call('POST', `/api/jobs/${id}/mappings`, form([['map2.csv', map2]]));
    check(again.files.length === 2, 'mapping file added to an existing job');

    job = await call('GET', `/api/jobs/${id}`);
    await call('PUT', `/api/jobs/${id}/config`, { ...job.config, template: '{MappingFile}/{EmployeeCode}/{DocumentType}/{FileName}' });
    await call('POST', `/api/jobs/${id}/scan`);
    const j = await waitFor(id, ['Awaiting approval', 'Draft']);
    check(j.status === 'Awaiting approval', `scan finished: ${j.status} ${j.message || ''}`);
    const c = j.counts; console.log('   counts', JSON.stringify(c), 'discovered', j.discovered);
    check(c.READY === 7, 'READY = 7 (a1, a2, a3 from one cell; "report, final.pdf"; dup; x1; b1)');
    check(c.UNMAPPED === 1, 'UNMAPPED = 1 (z.pdf)');
    check(c.INVALID === 1, 'INVALID = 1 (FileName NULL)');
    check(c.MISSING === 3, 'MISSING = 3 (missing1 twice, missing2)');
    check(c.DUPLICATE === 2, 'DUPLICATE = 2 (repeat of dup.pdf, repeat of a1.pdf in map2)');

    const all = (await call('GET', `/api/jobs/${id}/items?status=ALL&pageSize=100`)).items;
    const by = rid => all.find(i => i.recordId === rid);
    check(['ROW-2/1', 'ROW-2/2', 'ROW-2/3'].every(r => by(r)?.status === 'READY'), 'ROW-2 split into ROW-2/1, ROW-2/2, ROW-2/3, all READY');
    check(by('ROW-3')?.status === 'READY' && by('ROW-3')?.fileName === 'report, final.pdf', 'real file name with a comma kept whole');
    check(by('ROW-4')?.status === 'INVALID' && /Blank FileName/.test(by('ROW-4').reason), 'NULL file name is INVALID (blank), not MISSING');
    check(by('ROW-6')?.status === 'MISSING' && /repeats record ROW-5/.test(by('ROW-6').reason), 'repeat of a missing record is MISSING, with the first record named');
    check(by('ROW-8')?.status === 'DUPLICATE', 'repeat of a found record is DUPLICATE');
    check(by('ROW-9/1')?.status === 'READY' && by('ROW-9/2')?.status === 'MISSING', 'semicolon split: one part found, one part missing');
    const d2 = all.find(i => i.recordId === '2' && i.mappingFile === 'map2.csv');
    check(d2?.status === 'DUPLICATE' && /ROW-2\/1 in map1\.csv/.test(d2.reason), `cross-file repeat names the other file (${d2?.reason})`);
    check(by('1')?.destKey === 'map2/E7/Form16/b1.pdf', `{MappingFile} token: ${by('1')?.destKey}`);
    check(by('ROW-7')?.mappingFile === 'map1.csv', 'items carry their mapping file name');

    await call('POST', `/api/jobs/${id}/approve`, { acknowledge: true });
    const done = await waitFor(id, ['Completed', 'Completed with exceptions', 'Paused']);
    check(done.counts.PROCESSED === 7 && done.counts.FAILED === 0, `processed ${done.counts.PROCESSED}, failed ${done.counts.FAILED}`);
    check(readFileSync(join(dst, 'map1', 'E1', 'Offer', 'a3.jpg'), 'utf8').includes('a3.jpg'), 'split entry copied to its own destination');

    const csv = await (await call('GET', `/api/jobs/${id}/report?kind=reconciliation&format=csv`, undefined, true)).text();
    const lines = csv.trim().split('\r\n');
    check(lines[0].includes('Mapping File'), 'reconciliation report has a Mapping File column');
    check(lines.length - 1 === Object.values(done.counts).reduce((a, b) => a + b, 0), `one report row per item (${lines.length - 1})`);

    // the single-file endpoint still replaces all mapping files
    const { id: id2 } = await call('POST', '/api/jobs', { name: 'E2E single mapping', source: { type: 'Local', path: src }, destination: { type: 'Local', path: dst + '-2' } });
    await call('POST', `/api/jobs/${id2}/mappings`, form([['map1.csv', map1], ['map2.csv', map2]]));
    const rep = await call('POST', `/api/jobs/${id2}/mapping`, form([['map2.csv', map2]]));
    check(rep.files.length === 1 && rep.files[0].fileName === 'map2.csv', 'POST /mapping replaces all mapping files');
  } catch (e) { console.error('ERROR', e.message); failures++; }
  console.log(failures ? `\n${failures} check(s) failed` : '\nAll checks passed');
  process.exit(failures ? 1 : 0);
})();
