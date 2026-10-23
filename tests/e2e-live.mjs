// Live-environment scenarios (folder to folder): unsafe values in folder names, Unicode and case differences,
// nested folders, empty and changed files, files already at the destination (each duplicate rule), overlapping folders,
// empty source or mapping, unusual CSV encodings and line endings, two jobs at once, odd search text, very long names,
// and reports split over several sheets when the item count exceeds the per-sheet limit.
// Usage: node tests/e2e-live.mjs [baseUrl] [adminPassword]
// Run the application with DOCORG_XLSX_MAX_ROWS=5 to exercise the sheet split (otherwise that check is skipped).
import { mkdtempSync, mkdirSync, writeFileSync, readFileSync, existsSync, readdirSync, statSync, unlinkSync, utimesSync } from 'fs';
import { tmpdir } from 'os';
import { join, dirname, resolve } from 'path';
import { createHash } from 'crypto';
import { inflateRawSync } from 'zlib';

const base = process.argv[2] || 'http://localhost:5080';
const password = process.argv[3] || process.env.DOCORG_ADMIN_PASSWORD;
let failures = 0, cookie = '';
async function call(method, path, body, raw = false) {
  const headers = { 'X-DocOrg': '1', cookie }; let payload;
  if (body instanceof FormData) payload = body; else if (body !== undefined) { headers['content-type'] = 'application/json'; payload = JSON.stringify(body); }
  const res = await fetch(base + path, { method, headers, body: payload });
  const set = res.headers.get('set-cookie'); if (set) cookie = set.split(';')[0];
  if (raw) return res;
  const text = await res.text(); let json; try { json = text ? JSON.parse(text) : null; } catch { json = text; }
  if (!res.ok) { const e = new Error(`${method} ${path} -> ${res.status}: ${JSON.stringify(json)}`); e.status = res.status; e.body = json; throw e; }
  return json;
}
const check = (cond, msg) => { console.log(`${cond ? 'PASS' : 'FAIL'}  ${msg}`); if (!cond) failures++; };
const sleep = ms => new Promise(r => setTimeout(r, ms));
async function fails(p) { try { await p; return null; } catch (e) { return e; } }
const form = (name, data) => { const fd = new FormData(); fd.append('files', new Blob([data]), name); return fd; };
async function waitFor(id, statuses) {
  for (let i = 0; i < 600; i++) { const j = await call('GET', `/api/jobs/${id}`); if (statuses.includes(j.status) && !j.running) return j; await sleep(250); }
  throw new Error('timeout ' + id);
}
const items = async (id, q = 'status=ALL') => (await call('GET', `/api/jobs/${id}/items?${q}&pageSize=500`)).items;
const root = mkdtempSync(join(tmpdir(), 'docorg-live-'));
function files(dir, list) { for (const [f, content] of list) { mkdirSync(dirname(join(dir, f)), { recursive: true }); writeFileSync(join(dir, f), content ?? `%PDF ${f}\n`); } return dir; }
async function job(name, src, dst, mapName, mapData, cfg) {
  const { id } = await call('POST', '/api/jobs', { name, source: { type: 'Local', path: src }, destination: { type: 'Local', path: dst } });
  await call('POST', `/api/jobs/${id}/mappings`, form(mapName, mapData));
  if (cfg) { const j = await call('GET', `/api/jobs/${id}`); await call('PUT', `/api/jobs/${id}/config`, { ...j.config, ...cfg }); }
  await call('POST', `/api/jobs/${id}/scan`);
  return { id, j: await waitFor(id, ['Awaiting approval', 'Draft']) };
}
async function copy(id) { await call('POST', `/api/jobs/${id}/approve`, { acknowledge: true }); return waitFor(id, ['Completed', 'Completed with exceptions', 'Paused']); }
const walk = d => readdirSync(d, { withFileTypes: true }).flatMap(e => e.isDirectory() ? walk(join(d, e.name)) : [join(d, e.name)]);
const sha = p => createHash('sha256').update(readFileSync(p)).digest('hex');
/** Sheet names of an .xlsx (reads the zip central directory; no library needed). */
function xlsxSheets(buf) {
  let eocd = buf.lastIndexOf(Buffer.from([0x50, 0x4b, 0x05, 0x06]));
  let off = buf.readUInt32LE(eocd + 16), n = buf.readUInt16LE(eocd + 10);
  for (let i = 0; i < n; i++) {
    const nameLen = buf.readUInt16LE(off + 28), extra = buf.readUInt16LE(off + 30), comment = buf.readUInt16LE(off + 32), local = buf.readUInt32LE(off + 42);
    const name = buf.toString('utf8', off + 46, off + 46 + nameLen);
    if (name === 'xl/workbook.xml') {
      const lnl = buf.readUInt16LE(local + 26), lex = buf.readUInt16LE(local + 28), size = buf.readUInt32LE(off + 20), method = buf.readUInt16LE(off + 10);
      const data = buf.subarray(local + 30 + lnl + lex, local + 30 + lnl + lex + size);
      const xml = (method === 8 ? inflateRawSync(data) : data).toString('utf8');
      return [...xml.matchAll(/<sheet [^>]*name="([^"]+)"/g)].map(m => m[1]);
    }
    off += 46 + nameLen + extra + comment;
  }
  return [];
}

(async () => {
  try {
    await call('POST', '/api/auth/login', { userName: 'admin', password });

    console.log('\n== L1  Unsafe values in folder names stay inside the destination');
    {
      const src = files(join(root, 'L1'), [['l1a.pdf'], ['l1b.pdf'], ['l1c.pdf'], ['l1d.pdf'], ['l1e.pdf'], ['l1f.pdf'], ['l1g.pdf'], ['l1h.pdf']]);
      const dst = join(root, 'L1-out');
      const csv = ['EmployeeCode,Type,UploadedOn,FileName',
        '../../escape,Offer,2024-01-01,l1a.pdf',          // path traversal attempt
        'E1,PF: Nomination*?,2024-01-01,l1b.pdf',         // characters Windows does not allow
        'E1,Form 16/A,2024-01-01,l1c.pdf',                 // slash inside a value
        'E1,Pvt Ltd.,2024-01-01,l1d.pdf',                  // trailing dot (invalid on Windows)
        'CON,Offer,2024-01-01,l1e.pdf',                    // reserved device name
        'E1,COM5.tar,2024-01-01,l1f.pdf',                  // reserved name before the first dot
        '..,Offer,2024-01-01,l1g.pdf',                      // only dots: would disappear as a folder
        'E1, . ,2024-01-01,l1h.pdf'].join('\r\n');
      const csvMod = 'EmployeeCode,Type,Module,UploadedOn,FileName\r\nE7,Offer,,2024-01-01,l1a.pdf\r\nE7,Offer,-,2024-01-01,l1b.pdf\r\nE7,Offer,HR,2024-01-01,l1c.pdf';
      const { id } = await job('L1', src, dst, 'l1.csv', csv);
      const it = await items(id);
      const dk = f => it.find(i => i.fileName === f)?.destKey || '';
      check(dk('l1a.pdf').split('/').length === 4 && !dk('l1a.pdf').split('/').some(s => s === '..' || s === '.') && !dk('l1a.pdf').startsWith('/'), `traversal neutralised (one folder, no .. segment): ${dk('l1a.pdf')}`);
      check(!/[:*?]/.test(dk('l1b.pdf')), `invalid characters replaced: ${dk('l1b.pdf')}`);
      check(dk('l1c.pdf').split('/').length === 4, `slash inside a value does not add a folder: ${dk('l1c.pdf')}`);
      check(dk('l1d.pdf').split('/')[1] === 'Pvt Ltd', `trailing dot removed: ${dk('l1d.pdf')}`);
      check(dk('l1e.pdf').startsWith('_CON/'), `reserved name prefixed: ${dk('l1e.pdf')}`);
      check(dk('l1f.pdf').split('/')[1] === '_COM5.tar', `reserved name before a dot prefixed: ${dk('l1f.pdf')}`);
      check(it.find(i => i.fileName === 'l1g.pdf')?.status === 'INVALID' && /only dots/.test(it.find(i => i.fileName === 'l1g.pdf').reason), 'employee code of only dots is INVALID, not filed one level up');
      check(it.find(i => i.fileName === 'l1h.pdf')?.status === 'INVALID', 'document type of only a dot is INVALID');
      const { id: mid } = await job('L1 module', src, join(root, 'L1m-out'), 'mod.csv', csvMod, { template: '{EmployeeCode}/{DocumentType}/{ModuleCode}/{Year}/{FileName}' });
      const mi = await items(mid);
      check(['l1a.pdf', 'l1b.pdf'].every(f => mi.find(i => i.fileName === f)?.status === 'INVALID' && /ModuleCode is blank/.test(mi.find(i => i.fileName === f).reason)) && mi.find(i => i.fileName === 'l1c.pdf')?.destKey === 'E7/Offer/HR/2024/l1c.pdf',
        'a blank (or "-") value for a template token is INVALID instead of silently dropping a folder level');
      const done = await copy(id);
      const all = walk(dst);
      check(done.counts.PROCESSED === 6 && all.length === 6 && all.every(p => resolve(p).startsWith(resolve(dst) + '/')), 'all six copied, every file inside the destination folder');
      check(!existsSync(join(root, 'escape')) && !existsSync(join(dirname(root), 'escape')), 'nothing written outside the destination');
    }

    console.log('\n== L2  Unicode, case, spaces and folders');
    {
      const nfc = 'Résumé_NFC.pdf', nfd = 'Résumé_NFD.pdf';
      const src = files(join(root, 'L2'), [[nfd], ['UPPER.PDF'], ['x/dup.pdf', 'one'], ['y/dup.pdf', 'two'], ['z/only.pdf'], ['empty.pdf', ''], ['.docorg-write-test-1'], ['keep.pdf.partial-ab12cd34']]);
      const csv = ['EmployeeCode,Type,UploadedOn,FileName',
        `E1,Offer,2024-01-01,${nfd.normalize('NFC').replace('_NFD', '_NFD')}`,   // mapping in NFC, file on disk in NFD (macOS)
        'E2,Offer,2024-01-01,upper.pdf',                    // case differs
        'E3,Offer,2024-01-01,  only.pdf  ',                 // surrounding spaces
        'E4,Offer,2024-01-01,dup.pdf',                      // same name in two folders
        'E5,Offer,2024-01-01,y/dup.pdf',                    // a path that picks one of them
        'E6,Offer,2024-01-01,empty.pdf'].join('\r\n');
      const { id, j } = await job('L2', src, join(root, 'L2-out'), 'l2.csv', csv);
      const it = await items(id);
      const by = f => it.filter(i => i.sourceKey === f);
      check(by(nfd)[0]?.status === 'READY', `decomposed (NFD) file name matched to a composed (NFC) record (${by(nfd)[0]?.status})`);
      check(by('UPPER.PDF')[0]?.status === 'READY', 'case difference matched');
      check(by('z/only.pdf')[0]?.status === 'READY', 'spaces around the name ignored');
      check(by('y/dup.pdf')[0]?.status === 'READY' && by('y/dup.pdf')[0]?.employeeCode === 'E5', 'the record with a path takes the file in that folder');
      check(['READY', 'DUPLICATE', 'AMBIGUOUS'].includes(by('x/dup.pdf')[0]?.status), `the other file of the same name is reported, not lost (${by('x/dup.pdf')[0]?.status}: ${by('x/dup.pdf')[0]?.reason})`);
      check(by('empty.pdf')[0]?.status === 'INVALID' && /0-byte/.test(by('empty.pdf')[0]?.reason), 'empty file is INVALID');
      check(!it.some(i => /docorg-write-test|\.partial-/.test(i.sourceKey || '')), 'the application\'s own temporary files are ignored');
      check(j.discovered === 6, `files discovered counts only real files (${j.discovered})`);
    }

    console.log('\n== L3  Source changed or removed after the scan; retry');
    {
      const src = files(join(root, 'L3'), [['keep.pdf'], ['change.pdf', 'version 1'], ['remove.pdf']]);
      const csv = ['EmployeeCode,Type,UploadedOn,FileName', 'E1,Offer,2024-01-01,keep.pdf', 'E2,Offer,2024-01-01,change.pdf', 'E3,Offer,2024-01-01,remove.pdf'].join('\r\n');
      const { id } = await job('L3', src, join(root, 'L3-out'), 'l3.csv', csv);
      writeFileSync(join(src, 'change.pdf'), 'version 2 is longer'); utimesSync(join(src, 'change.pdf'), new Date(), new Date(Date.now() + 5000));
      unlinkSync(join(src, 'remove.pdf'));
      let d = await copy(id);
      const it = await items(id);
      check(d.counts.PROCESSED === 1 && d.counts.FAILED === 2, `changed and removed files FAILED, the rest copied (${JSON.stringify(d.counts)})`);
      check(/changed after the scan/.test(it.find(i => i.fileName === 'change.pdf').reason) && /no longer exists/.test(it.find(i => i.fileName === 'remove.pdf').reason), 'reasons say changed / no longer exists');
      writeFileSync(join(src, 'remove.pdf'), 'back again');
      const r = await call('POST', `/api/jobs/${id}/retry-failed`);
      d = await waitFor(id, ['Completed', 'Completed with exceptions']);
      check(d.counts.PROCESSED === 3 && d.counts.FAILED === 0, `retry copies the current versions (${r.retried} queued; ${JSON.stringify(d.counts)})`);
      const out = walk(join(root, 'L3-out')).find(p => p.endsWith('change.pdf'));
      check(readFileSync(out, 'utf8') === 'version 2 is longer', 'the retried copy is the current version');
    }

    console.log('\n== L4  File already at the destination: each duplicate rule');
    {
      const src = files(join(root, 'L4'), [['same.pdf', 'same content'], ['diff.pdf', 'new content']]);
      const csv = ['EmployeeCode,Type,UploadedOn,FileName', 'E1,Offer,2024-01-01,same.pdf', 'E1,Offer,2024-01-01,diff.pdf'].join('\r\n');
      const dst = join(root, 'L4-out');
      files(join(dst, 'E1', 'Offer', '2024'), [['same.pdf', 'same content'], ['diff.pdf', 'old content']]);
      const results = {};
      for (const rule of ['Skip', 'CompareHash', 'Rename']) {
        const { id } = await job(`L4 ${rule}`, src, dst, 'l4.csv', csv, { duplicateRule: rule });
        await copy(id);
        results[rule] = Object.fromEntries((await items(id)).map(i => [i.fileName, `${i.status}${i.destKey?.includes('_dup') ? ' ' + i.destKey.split('/').pop() : ''}`]));
      }
      console.log('   ', JSON.stringify(results));
      check(results.Skip['same.pdf'] === 'DUPLICATE' && results.Skip['diff.pdf'] === 'DUPLICATE', 'Skip: both reported, nothing overwritten');
      check(results.CompareHash['same.pdf'] === 'DUPLICATE' && /^PROCESSED diff_dup\d\.pdf$/.test(results.CompareHash['diff.pdf']), 'Compare hash: identical skipped, different copied with a suffix');
      check(/^PROCESSED same_dup\d\.pdf$/.test(results.Rename['same.pdf']) && /^PROCESSED diff_dup\d\.pdf$/.test(results.Rename['diff.pdf']), 'Rename: both copied with a suffix');
      check(readFileSync(join(dst, 'E1', 'Offer', '2024', 'diff.pdf'), 'utf8') === 'old content', 'the existing destination file was never overwritten');
      const ow = await fails((async () => { const { id } = await call('POST', '/api/jobs', { name: 'L4 overwrite', source: { type: 'Local', path: src }, destination: { type: 'Local', path: dst } });
        await call('POST', `/api/jobs/${id}/mappings`, form('l4.csv', csv)); const j = await call('GET', `/api/jobs/${id}`); await call('PUT', `/api/jobs/${id}/config`, { ...j.config, duplicateRule: 'Overwrite' }); })());
      check(ow?.status === 400 && /administrator/.test(JSON.stringify(ow.body)), 'Overwrite is refused unless an administrator allows it');
    }

    console.log('\n== L5  Overlapping or empty folders, empty mapping');
    {
      const src = files(join(root, 'L5src'), [['a.pdf']]);
      const mk = dst => call('POST', '/api/jobs', { name: 'L5', source: { type: 'Local', path: src }, destination: { type: 'Local', path: dst } });
      check((await fails(mk(join(src, 'out'))))?.status === 400, 'destination inside the source is refused');
      check((await fails(mk(src + '/')))?.status === 400, 'destination equal to the source is refused');
      check(!(await fails(mk(join(root, 'L5src2')))), 'a sibling folder whose name starts the same is allowed');
      check((await fails(mk(root)))?.status === 400, 'source inside the destination is refused (copies could land in the source)');
      const empty = join(root, 'L5empty'); mkdirSync(empty);
      const { id, j } = await job('L5 empty source', empty, join(root, 'L5-out'), 'm.csv', 'EmployeeCode,Type,UploadedOn,FileName\r\nE1,Offer,2024-01-01,a.pdf\r\nE2,Offer,2024-01-01,b.pdf');
      check(j.discovered === 0 && j.counts.MISSING === 2, 'empty source: every record MISSING');
      check((await fails(call('POST', `/api/jobs/${id}/approve`, { acknowledge: true })))?.status === 400, 'nothing ready: approval refused');
      const { j: j2 } = await job('L5 header only', src, join(root, 'L5-out2'), 'h.csv', 'EmployeeCode,Type,UploadedOn,FileName\r\n');
      check(j2.counts.UNMAPPED === 1 && j2.counts.READY === 0, 'mapping with headers only: every file UNMAPPED');
    }

    console.log('\n== L6  CSV encodings, line endings, odd headers');
    {
      const src = files(join(root, 'L6'), [['u16.pdf'], ['lf.pdf'], ['dup-head.pdf']]);
      const u16 = Buffer.concat([Buffer.from([0xFF, 0xFE]), Buffer.from('EmployeeCode\tType\tUploadedOn\tFileName\r\nE1\tOffer\t2024-01-01\tu16.pdf\r\n', 'utf16le')]);
      const { j: a } = await job('L6 utf16', src, join(root, 'L6-a'), 'unicode-text.csv', u16);
      check(a.counts.READY === 1, `UTF-16 tab-separated export ("Unicode Text") read (${JSON.stringify(a.counts)})`);
      const lf = 'EmployeeCode,Type,Remarks,UploadedOn,FileName\nE2,Offer,"has, comma and\nline break",2024-01-01,lf.pdf\n\n\n';
      const { j: b } = await job('L6 lf', src, join(root, 'L6-b'), 'lf.csv', lf);
      check(b.counts.READY === 1 && b.counts.INVALID === 0, `LF line endings, quoted comma and line break in another column, blank lines at the end (${JSON.stringify(b.counts)})`);
      const dupHead = 'EmployeeCode,Type,UploadedOn,FileName,FileName,\r\nE3,Offer,2024-01-01,dup-head.pdf,other.pdf,\r\n';
      const { j: c } = await job('L6 duplicate headers', src, join(root, 'L6-c'), 'dup.csv', dupHead);
      check(c.counts.READY === 1, `repeated and blank header columns: the first one is used (${JSON.stringify(c.counts)})`);
    }

    console.log('\n== L7  Two jobs at once, odd search text, very long names');
    {
      const big = []; for (let i = 0; i < 400; i++) big.push([`f${i}.pdf`, 'x'.repeat(2000 + i)]);
      const s1 = files(join(root, 'L7a'), big), s2 = files(join(root, 'L7b'), big);
      const csv = ['EmployeeCode,Type,UploadedOn,FileName', ...big.map(([f], i) => `E${i % 20},Offer,2024-01-01,${f}`)].join('\r\n');
      const [r1, r2] = await Promise.all([job('L7 one', s1, join(root, 'L7a-out'), 'a.csv', csv), job('L7 two', s2, join(root, 'L7b-out'), 'b.csv', csv)]);
      await Promise.all([call('POST', `/api/jobs/${r1.id}/approve`, { acknowledge: true }), call('POST', `/api/jobs/${r2.id}/approve`, { acknowledge: true })]);
      const [d1, d2] = await Promise.all([waitFor(r1.id, ['Completed', 'Completed with exceptions']), waitFor(r2.id, ['Completed', 'Completed with exceptions'])]);
      check(d1.counts.PROCESSED === 400 && d2.counts.PROCESSED === 400, 'two jobs copying at the same time both finish completely');
      const outs = walk(join(root, 'L7a-out'));
      check(outs.length === 400 && outs.every(p => sha(p) === sha(join(s1, p.split('/').pop()))), 'every copy identical to its source');
      for (const q of ['%', '_', "'", '"', '\\', '[x]', '😀', 'f1%']) {
        const r = await fails(call('GET', `/api/jobs/${r1.id}/items?status=ALL&q=${encodeURIComponent(q)}`));
        if (r) { check(false, `search for ${q} failed: ${r.message}`); break; }
      }
      check(true, 'search with %, _, quotes, backslash, brackets and emoji does not fail');
      check((await call('GET', `/api/jobs/${r1.id}/items?status=ALL&q=${encodeURIComponent('%')}`)).total === 0 && (await call('GET', `/api/jobs/${r1.id}/items?status=ALL&q=_`)).total === 0, '% and _ are searched for literally (not as wildcards)');
      const long = 'a'.repeat(180) + '_contract.pdf', long2 = 'a'.repeat(181) + '_contract.pdf';
      const s3 = files(join(root, 'L7c'), [[long], [long2]]);
      const { id } = await job('L7 long', s3, join(root, 'L7c-out'), 'l.csv', `EmployeeCode,Type,UploadedOn,FileName\r\nE1,Offer,2024-01-01,${long}\r\nE1,Offer,2024-01-01,${long2}`);
      const it = await items(id);
      check(it.every(i => i.destKey.split('/').pop().length <= 150), 'very long file names are shortened in the destination');
      check(it.filter(i => i.status === 'READY').length === 1 && it.some(i => i.status === 'DUPLICATE' && /collision/.test(i.reason)), 'two long names that shorten to the same name: one copied, the other reported (not overwritten)');
    }

    console.log('\n== L9  Two jobs copying the same file names into the same destination at once');
    {
      const n = 600, a = [], b = [];
      for (let i = 0; i < n; i++) { a.push([`s${i}.pdf`, 'from job A ' + i]); b.push([`s${i}.pdf`, 'from job B ' + i]); }
      const sa = files(join(root, 'L9a'), a), sb = files(join(root, 'L9b'), b), dst = join(root, 'L9-out');
      const csv = ['EmployeeCode,Type,UploadedOn,FileName', ...a.map(([f]) => `E1,Offer,2024-01-01,${f}`)].join('\r\n');
      const [ja, jb] = await Promise.all([job('L9 A', sa, dst, 'a.csv', csv), job('L9 B', sb, dst, 'b.csv', csv)]);
      await Promise.all([call('POST', `/api/jobs/${ja.id}/approve`, { acknowledge: true }), call('POST', `/api/jobs/${jb.id}/approve`, { acknowledge: true })]);
      const [da, db2] = await Promise.all([waitFor(ja.id, ['Completed', 'Completed with exceptions']), waitFor(jb.id, ['Completed', 'Completed with exceptions'])]);
      const all2 = async id => [...(await items(id, 'status=ALL&page=1')), ...(await items(id, 'status=ALL&page=2'))];
      const ia = await all2(ja.id), ib = await all2(jb.id);
      const both = ia.filter(x => x.status === 'PROCESSED' && ib.find(y => y.fileName === x.fileName)?.status === 'PROCESSED').length;
      const wrong = [...ia.map(x => [x, 'A']), ...ib.map(x => [x, 'B'])].filter(([x, who]) => x.status === 'PROCESSED' && !readFileSync(join(dst, ...x.destKey.split('/')), 'utf8').startsWith('from job ' + who)).length;
      check(both === 0 && wrong === 0, `each file is copied by exactly one job and holds that job's content (both: ${both}, wrong content: ${wrong})`);
      const pa = da.counts.PROCESSED, pb = db2.counts.PROCESSED;
      check(ia.length === n && ib.length === n, 'every item of both jobs checked');
      console.log(`   A copied ${pa}, B copied ${pb}; statuses A ${JSON.stringify(ia.reduce((m, x) => (m[x.status] = (m[x.status] || 0) + 1, m), {}))} B ${JSON.stringify(ib.reduce((m, x) => (m[x.status] = (m[x.status] || 0) + 1, m), {}))}`);
      check(pa + pb === n, `${n} files copied in total, the rest reported as already at the destination (${pa} + ${pb})`);
    }

    console.log('\n== L10  A slow upload does not hold up other people');
    {
      const src = files(join(root, 'L10'), [['a.pdf'], ['u.pdf']]);
      const { id } = await job('L10', src, join(root, 'L10-out'), 'm.csv', 'EmployeeCode,Type,UploadedOn,FileName\r\nE1,Offer,2024-01-01,a.pdf\r\nE2,Offer,2024-01-01,gone.pdf');
      const boundary = '----docorgslow';
      const head = Buffer.from(`--${boundary}\r\nContent-Disposition: form-data; name="files"; filename="slow.csv"\r\nContent-Type: text/csv\r\n\r\nEmployeeCode,Type,UploadedOn,FileName\r\n`);
      const tail = Buffer.from(`E3,Offer,2024-01-01,u.pdf\r\n\r\n--${boundary}--\r\n`);
      const body = new ReadableStream({ async start(c) { c.enqueue(head); for (let i = 0; i < 6; i++) { await sleep(500); c.enqueue(Buffer.from(`E${10 + i},Offer,2024-01-01,x${i}.pdf\r\n`)); } c.enqueue(tail); c.close(); } });
      const slow = fetch(`${base}/api/jobs/${id}/mappings`, { method: 'POST', headers: { 'X-DocOrg': '1', cookie, 'content-type': `multipart/form-data; boundary=${boundary}` }, body, duplex: 'half' });
      await sleep(300);
      const t0 = Date.now();
      await call('POST', `/api/jobs/${id}/items/exclude`, { allInView: true, status: 'MISSING', note: 'while someone uploads slowly' });
      const waited = Date.now() - t0;
      const r = await slow;
      check(waited < 1500, `another change to the job went through while a 3-second upload was still arriving (waited ${waited} ms)`);
      check(r.ok, `the slow upload itself succeeded (${r.status})`);
    }

    console.log('\n== L8  Reports');
    {
      const src = files(join(root, 'L8'), [['=cmd.pdf'], ['+x.pdf'], ['n1.pdf'], ['n2.pdf'], ['n3.pdf'], ['n4.pdf'], ['n5.pdf'], ['n6.pdf']]);
      const csv = ['EmployeeCode,Type,UploadedOn,FileName', '=HYPERLINK("x"),Offer,2024-01-01,=cmd.pdf', '@SUM(1),Offer,2024-01-01,+x.pdf',
        ...[1, 2, 3, 4, 5, 6].map(i => `E${i},Type\u000b${i},2024-01-01,n${i}.pdf`)].join('\r\n');   // vertical tab: not allowed in XML
      const { id } = await job('L8', src, join(root, 'L8-out'), 'l8.csv', csv);
      const rep = await (await call('GET', `/api/jobs/${id}/report?kind=validation&format=csv`, undefined, true)).text();
      check(rep.includes("'=HYPERLINK") && rep.includes("'@SUM") && rep.includes("'=cmd.pdf") && !/,=HYPERLINK/.test(rep), 'CSV report neutralises formulas (=, +, @)');
      const res = await call('GET', `/api/jobs/${id}/report?kind=validation&format=xlsx`, undefined, true);
      const buf = Buffer.from(await res.arrayBuffer());
      check(res.ok && buf.length > 500, 'Excel report with control characters in values downloads');
      const sheets = xlsxSheets(buf);
      if (sheets.length > 2) check(sheets.join('|') === 'Summary|Validation|Validation 2', `items beyond the per-sheet limit continue on another sheet (${sheets.join(', ')})`);
      else console.log(`   (sheet split not exercised: run the app with DOCORG_XLSX_MAX_ROWS=5; sheets: ${sheets.join(', ')})`);
    }
  } catch (e) { console.error('ERROR', e.stack || e.message); failures++; }
  console.log(failures ? `\n${failures} check(s) failed` : '\nAll checks passed');
  process.exit(failures ? 1 : 0);
})();
