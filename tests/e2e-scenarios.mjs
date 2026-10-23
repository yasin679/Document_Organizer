// Broad end-to-end scenarios for mapping files (v2.1), based on what real client exports contain.
//  A  File name cells: separators, spacing, paths, names that contain a comma, placeholders, repeats, rescans
//  B  Several mapping files with different columns: other names, other order, title rows, xlsx + csv,
//     semicolon CSV in Windows-1252, a file with no recognisable headers, a file with only a Year column,
//     record IDs that collide between files, ambiguity across files and its resolution
//  C  Upload rules: same file twice, a bad file in a batch, roles, locked jobs, scan without mapping
// Usage: node tests/e2e-scenarios.mjs [baseUrl] [adminPassword]
// Set DOCORG_DATA_DIR to the application's data folder to also check that rejected uploads leave no files behind.
import { mkdtempSync, mkdirSync, writeFileSync, readFileSync, readdirSync, existsSync } from 'fs';
import { tmpdir } from 'os';
import { join, dirname } from 'path';
import { fileURLToPath } from 'url';

const base = process.argv[2] || 'http://localhost:5080';
const password = process.argv[3] || process.env.DOCORG_ADMIN_PASSWORD;
const dataDir = process.env.DOCORG_DATA_DIR;
const here = dirname(fileURLToPath(import.meta.url));
let failures = 0;

function client() {
  let cookie = '';
  async function call(method, path, body, raw = false) {
    const headers = { 'X-DocOrg': '1', cookie };
    let payload;
    if (body instanceof FormData) payload = body;
    else if (body !== undefined) { headers['content-type'] = 'application/json'; payload = JSON.stringify(body); }
    const res = await fetch(base + path, { method, headers, body: payload });
    const set = res.headers.get('set-cookie'); if (set) cookie = set.split(';')[0];
    if (raw) return res;
    const text = await res.text(); let json; try { json = text ? JSON.parse(text) : null; } catch { json = text; }
    if (!res.ok) { const e = new Error(`${method} ${path} -> ${res.status}: ${JSON.stringify(json)}`); e.status = res.status; e.body = json; throw e; }
    return json;
  }
  return call;
}
const call = client();
const check = (cond, msg) => { console.log(`${cond ? 'PASS' : 'FAIL'}  ${msg}`); if (!cond) failures++; };
const sleep = ms => new Promise(r => setTimeout(r, ms));
async function waitFor(id, statuses) {
  for (let i = 0; i < 240; i++) { const j = await call('GET', `/api/jobs/${id}`); if (statuses.includes(j.status) && !j.running) return j; await sleep(400); }
  throw new Error('timeout');
}
const form = files => { const fd = new FormData(); for (const [name, data] of files) fd.append('files', new Blob([data]), name); return fd; };
async function fails(p) { try { await p; return null; } catch (e) { return e; } }
async function scan(id) { await call('POST', `/api/jobs/${id}/scan`); return waitFor(id, ['Awaiting approval', 'Draft']); }
async function items(id) { return (await call('GET', `/api/jobs/${id}/items?status=ALL&pageSize=500`)).items; }
const cp1252 = s => Buffer.from([...s].map(ch => ({ 'é': 0xE9, 'É': 0xC9, 'ü': 0xFC, '’': 0x92 })[ch] ?? ch.charCodeAt(0)));
const root = mkdtempSync(join(tmpdir(), 'docorg-scen-'));
function source(name, files) {
  const dir = join(root, name);
  for (const f of files) { mkdirSync(dirname(join(dir, f)), { recursive: true }); writeFileSync(join(dir, f), `%PDF-1.4 ${f}\n`); }
  return dir;
}
const count = (c, s) => c[s] || 0;
let cellsCsv, countsA, srcA;

async function scenarioA() {
  console.log('\n== A  File name cells');
  const files = ['a.pdf', 'b.pdf', 'c.pdf', 'd.pdf', 'e.pdf', 'f.pdf', 'g.pdf', 'h.pdf', 'i.pdf', 'j.pdf', 'k.pdf', 'l.pdf',
    'report, final.pdf', 'm.pdf', 'n.pdf', 'p.pdf', 'q.pdf', 'r.pdf', 'a,b.pdf'].map(f => 'docs/' + f);
  const src = srcA = source('A', files);
  const csv = cellsCsv = [
    'EmployeeCode,Type,UploadedOn,FileName',
    'E1,Offer,2024-01-01,"a.pdf , b.pdf"',            // 2  spaces around the comma
    'E1,Offer,2024-01-01,",c.pdf,"',                  // 3  leading and trailing comma
    'E1,Offer,2024-01-01,"d.pdf,,e.pdf"',             // 4  double comma
    'E1,Offer,2024-01-01,"f.pdf;g.pdf"',              // 5  semicolon
    'E1,Offer,2024-01-01,"h.pdf\ni.pdf"',             // 6  line break inside the cell
    'E1,Offer,2024-01-01,"j.pdf,J.PDF"',              // 7  same file twice in one cell, other case
    'E1,Offer,2024-01-01,"docs/k.pdf,docs/l.pdf"',    // 8  folder paths in the cell
    'E1,Offer,2024-01-01,"report, final.pdf,m.pdf"',  // 9  a real name with a comma next to another name
    'E1,Offer,2024-01-01,"NULL,NULL"',                // 10 only placeholders
    'E1,Offer,2024-01-01,"n.pdf,NULL"',               // 11 a name and a placeholder
    'N/A,Offer,2024-01-01,p.pdf',                     // 12 placeholder employee code
    'E2,Offer,NULL,q.pdf',                            // 13 placeholder date
    'E3,PAN,2024-01-01,gone.pdf',                     // 14 missing
    'E3,PAN,2024-01-02,gone.pdf',                     // 15 repeat of a missing record
    'E3,PAN,2024-01-03,gone.pdf',                     // 16 repeat of a missing record
    'E4,PAN,2024-01-01,r.pdf',                        // 17 found
    'E4,PAN,2024-01-02,r.pdf',                        // 18 repeat of a found record
    'E4,PAN,2024-01-03,r.pdf',                        // 19 repeat of a found record
    'E5,PAN,2024-01-01,"a,b.pdf"',                    // 20 a real name with a comma
  ].join('\r\n');
  const { id } = await call('POST', '/api/jobs', { name: 'Scenario A', source: { type: 'Local', path: src }, destination: { type: 'Local', path: join(root, 'A-out') } });
  await call('POST', `/api/jobs/${id}/mappings`, form([['cells.csv', csv]]));
  const j = await scan(id); const c = countsA = j.counts;
  console.log('   counts', JSON.stringify(c));
  const all = await items(id); const st = r => all.filter(i => i.recordId === r).map(i => i.status).join('+');
  const by = r => all.find(i => i.recordId === r);
  check(st('ROW-2/1') === 'READY' && st('ROW-2/2') === 'READY', 'spaces around the comma');
  check(st('ROW-3/1') === 'READY' && !by('ROW-3/2') && !by('ROW-3'), 'leading and trailing comma give one entry');
  check(st('ROW-4/1') === 'READY' && st('ROW-4/2') === 'READY' && !by('ROW-4/3'), 'double comma ignored');
  check(st('ROW-5/1') === 'READY' && st('ROW-5/2') === 'READY', 'semicolon separator');
  check(st('ROW-6/1') === 'READY' && st('ROW-6/2') === 'READY', 'line break separator');
  check(st('ROW-7/1') === 'READY' && !by('ROW-7/2'), 'same file twice in one cell gives one entry');
  check(st('ROW-8/1') === 'READY' && st('ROW-8/2') === 'READY', 'folder paths in a multi-file cell');
  check(by('ROW-9/1')?.fileName === 'report, final.pdf' && st('ROW-9/1') === 'READY' && st('ROW-9/2') === 'READY', `comma name kept whole next to another name (${by('ROW-9/1')?.fileName})`);
  check(st('ROW-10') === 'INVALID' && /Blank FileName/.test(by('ROW-10')?.reason), 'cell of placeholders only is blank (INVALID)');
  check(st('ROW-11/1') === 'READY' && !by('ROW-11/2'), 'placeholder next to a name is dropped');
  check(st('ROW-12') === 'INVALID', 'placeholder employee code is INVALID');
  check(st('ROW-13') === 'INVALID' && /Year/.test(by('ROW-13')?.reason), 'placeholder date gives INVALID year');
  check(['ROW-14', 'ROW-15', 'ROW-16'].every(r => st(r) === 'MISSING'), 'missing record and its two repeats are all MISSING');
  check(st('ROW-17') === 'READY' && st('ROW-18') === 'DUPLICATE' && st('ROW-19') === 'DUPLICATE', 'found record repeated twice gives two DUPLICATE');
  check(st('ROW-20') === 'READY', 'real file name with a comma alone in the cell');
  check(count(c, 'READY') === 17 && count(c, 'MISSING') === 3 && count(c, 'DUPLICATE') === 2 && count(c, 'INVALID') === 3 && count(c, 'UNMAPPED') === 0,
    'A totals: 17 ready, 3 missing, 2 duplicate, 3 invalid, 0 unmapped');
  const total = Object.values(c).reduce((a, b) => a + b, 0);
  const csvRep = await (await call('GET', `/api/jobs/${id}/report?kind=validation&format=csv`, undefined, true)).text();
  check(csvRep.trim().split('\r\n').length - 1 === total, `validation report has one row per item (${total})`);

  // rescan with splitting off: the old behaviour, cells stay whole
  let job = await call('GET', `/api/jobs/${id}`);
  await call('PUT', `/api/jobs/${id}/config`, { ...job.config, splitMultipleFileNames: false });
  const j2 = await scan(id); const all2 = await items(id);
  const by2 = r => all2.find(i => i.recordId === r);
  check(by2('ROW-2')?.status === 'MISSING' && !by2('ROW-2/1'), 'split off: multi-file cell is one MISSING record');
  check(by2('ROW-20')?.status === 'READY', 'split off: comma name still found');
  check(count(j2.counts, 'UNMAPPED') > 0, `split off: the listed files become UNMAPPED (${count(j2.counts, 'UNMAPPED')})`);
  await call('PUT', `/api/jobs/${id}/config`, { ...job.config, splitMultipleFileNames: true });
  const j3 = await scan(id);
  check(JSON.stringify(j3.counts) === JSON.stringify(c), 'split on again: same totals as the first scan');
  return id;
}

async function scenarioB() {
  console.log('\n== B  Several mapping files with different columns');
  const src = source('B', ['b1.pdf', 'b2.pdf', 'x2a.pdf', 'x2b.pdf', 'x2c.pdf', 'shared.pdf', 'Résumé.pdf', 'c4.pdf', 'c4b.pdf', 'amb.pdf']);
  // 1: standard column names, UTF-8 with BOM, no ID column (record IDs ROW-n)
  const m1 = '﻿' + ['EmployeeCode,Type,Name,UploadedOn,FileName', 'E10,Offer,Offer letter,2021-03-04,b1.pdf', 'E11,Offer,,15/08/2020,amb.pdf'].join('\r\n');
  // 2: xlsx with title rows, other names, other order, extra columns, real dates, a multi-line cell
  const m2 = readFileSync(join(here, 'fixtures', 'mixed-columns.xlsx'));
  // 3: semicolon CSV saved by Excel on Windows (Windows-1252), only a Year column, no Uploaded on column
  const m3 = cp1252(['EmpCode;DocType;Year;PhysicalFileName', 'E30;PAN;2018;Résumé.pdf', 'E31;PAN;FY 2019-20;b2.pdf'].join('\r\n'));
  // 4: column names the application cannot recognise; also no ID column, so its IDs collide with file 1
  const m4 = ['Staff,Kind,When,Attachment', 'E41,Payslip,2024-02-02,amb.pdf', 'E40,Payslip,2024-02-02,c4.pdf', 'E42,Payslip,2024-02-02,c4b.pdf'].join('\r\n');

  const { id } = await call('POST', '/api/jobs', { name: 'Scenario B', source: { type: 'Local', path: src }, destination: { type: 'Local', path: join(root, 'B-out') } });
  const up = await call('POST', `/api/jobs/${id}/mappings`, form([['m1.csv', m1], ['mixed-columns.xlsx', m2], ['m3.csv', m3], ['m4.csv', m4]]));
  check(up.files.length === 4 && up.rows === 2 + 4 + 2 + 3, `four files with different columns uploaded (${up.rows} rows)`);
  const [f1, f2, f3, f4] = up.files.map(f => f.id);
  const fc = up.fileColumns;
  check(fc[f1].employeeCode === 'EmployeeCode' && fc[f1].fileName === 'FileName' && fc[f1].uploadedOn === 'UploadedOn' && !fc[f1].year, 'file 1 auto-mapped');
  check(up.files[1].info.headerRow === 3, `xlsx header found below the title rows (row index ${up.files[1].info.headerRow})`);
  check(fc[f2].employeeCode === 'Employee ID' && fc[f2].documentType === 'Category' && fc[f2].fileName === 'File Path' && fc[f2].uploadedOn === 'Upload Date' && fc[f2].year === 'Year' && fc[f2].description === 'Remarks',
    'xlsx auto-mapped despite other names, order and extra columns');
  check(fc[f3].employeeCode === 'EmpCode' && fc[f3].fileName === 'PhysicalFileName' && fc[f3].year === 'Year' && !fc[f3].uploadedOn, 'semicolon CSV auto-mapped');
  check(!fc[f4].employeeCode && !fc[f4].fileName, 'unrecognised headers are left unmapped');

  let job = await call('GET', `/api/jobs/${id}`);
  await call('PUT', `/api/jobs/${id}/config`, { ...job.config, template: '{MappingFile}/{Year}/{EmployeeCode}/{FileName}' })
    .then(() => check(false, 'saving rules with file 4 unmapped should be refused'))
    .catch(e => check(e.status === 400 && /m4\.csv/.test(JSON.stringify(e.body)), `rules refused and the message names the file: ${JSON.stringify(e.body?.error).slice(0, 140)}`));
  const e1 = await fails(call('POST', `/api/jobs/${id}/scan`));
  check(e1?.status === 400 && /m4\.csv/.test(JSON.stringify(e1.body)), 'scan refused while file 4 is unmapped, naming the file');

  // map file 4 by hand, keep the others
  job = await call('GET', `/api/jobs/${id}`);
  const fileColumns = { ...job.config.fileColumns, [f4]: { employeeCode: 'Staff', documentType: 'Kind', uploadedOn: 'When', fileName: 'Attachment' } };
  await call('PUT', `/api/jobs/${id}/config`, { ...job.config, fileColumns, template: '{MappingFile}/{Year}/{EmployeeCode}/{FileName}' });
  job = await call('GET', `/api/jobs/${id}`);
  check(job.config.fileColumns[f4].fileName === 'Attachment' && job.config.fileColumns[f2].fileName === 'File Path', 'hand mapping of file 4 saved, other files unchanged');

  // a client that sends the configuration without fileColumns (like a v2.0 script) must not wipe the per-file mapping
  const { fileColumns: _drop, ...oldStyle } = job.config;
  await call('PUT', `/api/jobs/${id}/config`, oldStyle);
  job = await call('GET', `/api/jobs/${id}`);
  check(job.config.fileColumns[f4]?.fileName === 'Attachment' && job.config.fileColumns[f3]?.fileName === 'PhysicalFileName', 'saving rules without fileColumns keeps every file\'s mapping');
  let j = await scan(id); console.log('   counts', JSON.stringify(j.counts));
  check(j.status === 'Awaiting approval', `scan with four different files: ${j.status} ${j.message || ''}`);
  let all = await items(id);
  const it = (file, rid) => all.find(i => i.mappingFile === file && i.recordId === rid);
  check(it('m1.csv', 'ROW-2')?.destKey === 'm1/2021/E10/b1.pdf', `file 1 (UploadedOn): ${it('m1.csv', 'ROW-2')?.destKey}`);
  check(it('mixed-columns.xlsx', 'ROW-5')?.destKey === 'mixed-columns/2022/E20/x2a.pdf', `xlsx date cell gives the year: ${it('mixed-columns.xlsx', 'ROW-5')?.destKey}`);
  check(it('mixed-columns.xlsx', 'ROW-6/1')?.status === 'READY' && it('mixed-columns.xlsx', 'ROW-6/2')?.status === 'READY', 'xlsx multi-line cell split');
  check(it('mixed-columns.xlsx', 'ROW-8')?.status === 'INVALID', 'xlsx NULL file name is INVALID');
  check(it('m3.csv', 'ROW-2')?.status === 'READY' && it('m3.csv', 'ROW-2')?.destKey === 'm3/2018/E30/Résumé.pdf', `Windows-1252 accented name found, year from the Year column: ${it('m3.csv', 'ROW-2')?.destKey}`);
  check(it('m3.csv', 'ROW-3')?.year === '2019', 'year from "FY 2019-20"');
  check(it('m4.csv', 'ROW-3')?.status === 'READY' && it('m4.csv', 'ROW-3')?.destKey === 'm4/2024/E40/c4.pdf', 'hand-mapped file 4 works');
  const amb = all.find(i => i.status === 'AMBIGUOUS');
  check(amb?.fileName === 'amb.pdf' && amb.candidates.length === 2 && new Set(amb.candidates.map(x => x.mappingFile)).size === 2, 'same file in two mapping files is AMBIGUOUS with both files listed');

  // resolve to file 4's ROW-2 while file 1's ROW-2 (same ID) is already matched to b1.pdf
  const pick = amb.candidates.findIndex(x => x.mappingFile === 'm4.csv');
  const res = await call('POST', `/api/jobs/${id}/items/${amb.id}/resolve`, { recordId: amb.candidates[pick].recordId, mappingFile: 'm4.csv', note: 'scenario' });
  all = await items(id);
  const r = all.find(i => i.id === amb.id);
  check(r.status === 'READY' && r.mappingFile === 'm4.csv' && r.destKey === 'm4/2024/E41/amb.pdf', `resolved to the right file despite colliding IDs: ${r.status} ${r.destKey} ${res.message}`);

  j = await call('GET', `/api/jobs/${id}`);
  check(count(j.counts, 'READY') === 10 && count(j.counts, 'INVALID') === 1 && count(j.counts, 'AMBIGUOUS') === 0 && count(j.counts, 'UNMAPPED') === 0,
    `B totals: ${JSON.stringify(j.counts)}`);

  // year rule YearColumn: file 1 has no Year column and falls back to UploadedOn; file 2 has one, so it is used
  const saved = j.config;
  await call('PUT', `/api/jobs/${id}/config`, { ...saved, yearRule: 'YearColumn' });
  await scan(id); all = await items(id);
  check(it('m1.csv', 'ROW-2')?.year === '2021', 'YearColumn rule: file without a Year column uses UploadedOn');
  check(it('mixed-columns.xlsx', 'ROW-5')?.year === '2019', 'YearColumn rule: file with a Year column uses it');
  check(it('mixed-columns.xlsx', 'ROW-7')?.status === 'INVALID', 'YearColumn rule: blank Year cell is INVALID');
  await call('PUT', `/api/jobs/${id}/config`, saved);

  // remove a file in the middle: the others keep their own column mapping
  await call('DELETE', `/api/jobs/${id}/mappings/${f2}`);
  job = await call('GET', `/api/jobs/${id}`);
  check(job.mappings.length === 3 && job.config.fileColumns[f4]?.fileName === 'Attachment' && !job.config.fileColumns[f2], 'removing one file keeps the others and their mapping');
  j = await scan(id);
  check(j.status === 'Awaiting approval' && count(j.counts, 'UNMAPPED') === 4, `after removing the xlsx its 4 files are UNMAPPED (${JSON.stringify(j.counts)})`);

  // approve and copy
  all = await items(id);
  const amb2 = all.find(i => i.status === 'AMBIGUOUS');
  if (amb2) await call('POST', `/api/jobs/${id}/items/${amb2.id}/resolve`, { recordId: amb2.candidates.find(x => x.mappingFile === 'm4.csv').recordId, mappingFile: 'm4.csv' });
  await call('POST', `/api/jobs/${id}/approve`, { acknowledge: true });
  const done = await waitFor(id, ['Completed', 'Completed with exceptions', 'Paused']);
  check(count(done.counts, 'FAILED') === 0 && existsSync(join(root, 'B-out', 'm3', '2018', 'E30', 'Résumé.pdf')), `copied (${count(done.counts, 'PROCESSED')} processed), accented name written`);
  const rep = await (await call('GET', `/api/jobs/${id}/report?kind=reconciliation&format=csv`, undefined, true)).text();
  check(/m3\.csv/.test(rep) && /m4\.csv/.test(rep) && /m1\.csv/.test(rep), 'reconciliation report names each mapping file');
  // a completed job is locked
  const locked = await fails(call('POST', `/api/jobs/${id}/mappings`, form([['late.csv', 'EmployeeCode,Type,UploadedOn,FileName\r\nE1,X,2024-01-01,z.pdf']])));
  check(locked?.status === 400, 'mapping files cannot be added after approval');
  const locked2 = await fails(call('DELETE', `/api/jobs/${id}/mappings/${f1}`));
  check(locked2?.status === 400, 'mapping files cannot be removed after approval');
}

function jobFiles(id) {
  if (!dataDir) return null;
  const found = [];
  const walk = d => { for (const e of readdirSync(d, { withFileTypes: true })) { const p = join(d, e.name); if (e.isDirectory()) walk(p); else if (e.name.startsWith('mapping-') && p.includes(id)) found.push(e.name); } };
  walk(dataDir); return found;
}

async function scenarioC() {
  console.log('\n== C  Upload rules');
  const src = source('C', ['c.pdf']);
  const good = 'EmployeeCode,Type,UploadedOn,FileName\r\nE1,Offer,2024-01-01,c.pdf';
  const { id } = await call('POST', '/api/jobs', { name: 'Scenario C', source: { type: 'Local', path: src }, destination: { type: 'Local', path: join(root, 'C-out') } });
  const e0 = await fails(call('POST', `/api/jobs/${id}/scan`));
  check(e0?.status === 400, 'scan refused with no mapping file');
  const e1 = await fails(call('POST', `/api/jobs/${id}/mappings`, form([['one.csv', good], ['copy.csv', good]])));
  check(e1?.status === 400 && /same content/.test(JSON.stringify(e1.body)), 'same file twice in one upload refused');
  let job = await call('GET', `/api/jobs/${id}`);
  check(job.mappings.length === 0, 'nothing kept from the refused upload');
  if (dataDir) check(jobFiles(id).length === 0, 'no files left on disk from the refused upload');
  await call('POST', `/api/jobs/${id}/mappings`, form([['one.csv', good]]));
  const e2 = await fails(call('POST', `/api/jobs/${id}/mappings`, form([['again.csv', good]])));
  check(e2?.status === 400 && /one\.csv/.test(JSON.stringify(e2.body)), 'file already added is refused, naming the earlier file');
  const e3 = await fails(call('POST', `/api/jobs/${id}/mappings`, form([['two.csv', good.replace('E1', 'E2')], ['broken.xlsx', 'this is not a workbook']])));
  check(e3?.status === 400, 'a batch with a broken workbook is refused');
  job = await call('GET', `/api/jobs/${id}`);
  check(job.mappings.length === 1, 'the good file of a refused batch is not kept');
  if (dataDir) check(jobFiles(id).length === 1, `only the one accepted file is on disk (${jobFiles(id).join(', ')})`);
  const same = await call('POST', `/api/jobs/${id}/mappings`, form([['one.csv', good.replace('E1', 'E7')]]));
  check(same.files[1]?.fileName === 'one (2).csv', `a second file with the same name is renamed (${same.files[1]?.fileName})`);
  await call('DELETE', `/api/jobs/${id}/mappings/${same.files[1].id}`);
  const e4 = await fails(call('POST', `/api/jobs/${id}/mappings`, form([['notes.pdf', '%PDF']])));
  check(e4?.status === 400, 'a file that is not CSV or Excel is refused');
  const e5 = await fails(call('POST', `/api/jobs/${id}/mappings`, form([['empty.csv', '']])));
  check(e5?.status === 400, 'an empty mapping file is refused');
  const e6 = await fails(call('DELETE', `/api/jobs/${id}/mappings/m99`));
  check(e6?.status === 400 || e6?.status === 404, 'removing an unknown mapping file is refused');

  // replacing all mapping files (POST /mapping): a refused file must not damage the current one; a good one removes the old files
  const r1 = await fails(call('POST', `/api/jobs/${id}/mapping`, form([['broken.csv', '']])));
  check(r1?.status === 400 && (await call('GET', `/api/jobs/${id}`)).mappings.length === 1, 'a refused replacement keeps the current mapping file');
  check(count((await scan(id)).counts, 'READY') === 1, 'the current mapping file still scans after a refused replacement');
  const rep = await call('POST', `/api/jobs/${id}/mapping`, form([['newer.csv', good + '\r\nE2,Offer,2024-01-01,gone.pdf']]));
  check(rep.files.length === 1 && rep.files[0].fileName === 'newer.csv', 'replacement accepted, even with content similar to the old file');
  if (dataDir) check(jobFiles(id).length === 1, `replacement leaves only the new file on disk (${jobFiles(id).join(', ')})`);
  const rep2 = await call('POST', `/api/jobs/${id}/mapping`, form([['newer.csv', good + '\r\nE2,Offer,2024-01-01,gone.pdf']]));
  check(rep2.files.length === 1, 'replacing a file with identical content is allowed');

  await call('POST', '/api/users', { userName: 'scenviewer', displayName: 'Scenario Viewer', role: 'Viewer', password: 'View-Only-2026!' }).catch(() => {});
  const viewer = client();
  await viewer('POST', '/api/auth/login', { userName: 'scenviewer', password: 'View-Only-2026!' });
  const v1 = await fails(viewer('POST', `/api/jobs/${id}/mappings`, form([['v.csv', good.replace('E1', 'E9')]])));
  const v2 = await fails(viewer('DELETE', `/api/jobs/${id}/mappings/m1`));
  check(v1?.status === 403 && v2?.status === 403, 'a viewer cannot add or remove mapping files');
  const j = await scan(id);
  check(count(j.counts, 'READY') === 1 && count(j.counts, 'MISSING') === 1, 'job still scans normally after the refused uploads');
}

async function scenarioD() {
  console.log('\n== D  Cloud storage: the same multi-file cells through Azure Blob and S3');
  const demo = await call('POST', '/api/demo');
  const az = (demo.cloud || []).find(c => c.provider === 'AzureBlob'), s3 = (demo.cloud || []).find(c => c.provider === 'S3');
  if (!az || !s3) { console.log('   skipped: no Azure and S3 demo connections reachable'); return; }
  const prefix = `scen-${Date.now()}/`;
  const azLoc = { type: 'AzureBlob', connection: az.connection, container: az.destinationContainer, prefix };
  const s3Loc = { type: 'S3', connection: s3.connection, container: s3.destinationContainer, prefix };
  // template keeps the file name only, so the copied names (with commas and spaces) are listed back as they were
  async function run(name, from, to) {
    const { id } = await call('POST', '/api/jobs', { name, source: from, destination: to });
    await call('POST', `/api/jobs/${id}/mappings`, form([['cells.csv', cellsCsv]]));
    const job = await call('GET', `/api/jobs/${id}`);
    await call('PUT', `/api/jobs/${id}/config`, { ...job.config, template: '{EmployeeCode}/{FileName}' });
    const j = await scan(id);
    const ready = count(j.counts, 'READY');
    await call('POST', `/api/jobs/${id}/approve`, { acknowledge: true });
    const done = await waitFor(id, ['Completed', 'Completed with exceptions', 'Paused']);
    console.log(`   ${name}`, JSON.stringify(done.counts));
    return { scan: j.counts, done: done.counts, ready };
  }
  const d1 = await run('Scenario D1 folder -> Azure', { type: 'Local', path: srcA }, azLoc);
  check(d1.ready === count(countsA, 'READY') && count(d1.done, 'PROCESSED') === d1.ready && count(d1.done, 'FAILED') === 0, `folder -> Azure: ${d1.ready} ready, all copied`);
  const d2 = await run('Scenario D2 Azure -> S3', azLoc, s3Loc);
  check(count(d2.scan, 'READY') === 17 && count(d2.scan, 'MISSING') === 5 && count(d2.done, 'FAILED') === 0, `Azure -> S3: multi-file cells, comma names found in Azure; p.pdf and q.pdf were never copied so they are MISSING (${JSON.stringify(d2.scan)})`);
  const d3 = await run('Scenario D3 S3 -> folder', s3Loc, { type: 'Local', path: join(root, 'D-out') });
  check(count(d3.scan, 'READY') === count(d2.scan, 'READY') && count(d3.done, 'FAILED') === 0, `S3 -> folder: same result from S3 (${JSON.stringify(d3.scan)})`);
  check(existsSync(join(root, 'D-out', 'E1', 'report, final.pdf')) && existsSync(join(root, 'D-out', 'E5', 'a,b.pdf')), 'comma names survive Azure and S3 round trip');

  // two S3 -> S3 jobs on the same connection writing the same keys at the same moment (server-side copy)
  const n = 120, A = [], B = [];
  for (let i = 0; i < n; i++) { A.push(`k${i}.pdf`); B.push(`k${i}.pdf`); }
  const la = join(root, 'D-A'), lb = join(root, 'D-B');
  for (const f of A) { mkdirSync(la, { recursive: true }); writeFileSync(join(la, f), 'job A ' + f); }
  for (const f of B) { mkdirSync(lb, { recursive: true }); writeFileSync(join(lb, f), 'job B ' + f); }
  const kcsv = ['EmployeeCode,Type,UploadedOn,FileName', ...A.map(f => `E1,Offer,2024-01-01,${f}`)].join('\r\n');
  const pre = `scen-cc-${Date.now()}/`;
  async function runQuiet(name, from, to, integrity) {
    const { id } = await call('POST', '/api/jobs', { name, source: from, destination: to });
    await call('POST', `/api/jobs/${id}/mappings`, form([['k.csv', kcsv]]));
    const job = await call('GET', `/api/jobs/${id}`);
    await call('PUT', `/api/jobs/${id}/config`, { ...job.config, template: '{FileName}', integrity });
    await scan(id); return id;
  }
  const s3loc = p => ({ type: 'S3', connection: s3.connection, container: s3.destinationContainer, prefix: pre + p });
  for (const [l, p] of [[la, 'a/'], [lb, 'b/']]) { const id = await runQuiet('D seed ' + p, { type: 'Local', path: l }, s3loc(p), 'Sha256'); await call('POST', `/api/jobs/${id}/approve`, { acknowledge: true }); await waitFor(id, ['Completed']); }
  const ida = await runQuiet('D cc A', s3loc('a/'), s3loc('out/'), 'None');
  const idb = await runQuiet('D cc B', { ...s3loc('b/'), connection: s3.connection.toLowerCase() }, s3loc('out/'), 'None');
  await Promise.all([call('POST', `/api/jobs/${ida}/approve`, { acknowledge: true }), call('POST', `/api/jobs/${idb}/approve`, { acknowledge: true })]);
  const [ca, cb] = await Promise.all([waitFor(ida, ['Completed', 'Completed with exceptions']), waitFor(idb, ['Completed', 'Completed with exceptions'])]);
  check(ca.counts.PROCESSED + cb.counts.PROCESSED === n, `S3 to S3, same keys at the same moment: each key copied by exactly one job (${ca.counts.PROCESSED} + ${cb.counts.PROCESSED} of ${n})`);
  const back = await runQuiet('D verify', s3loc('out/'), { type: 'Local', path: join(root, 'D-verify') }, 'Sha256');
  await call('POST', `/api/jobs/${back}/approve`, { acknowledge: true }); await waitFor(back, ['Completed']);
  const ia = (await items(ida)).filter(i => i.status === 'PROCESSED').map(i => i.fileName);
  const wrong = ia.filter(f => !readFileSync(join(root, 'D-verify', f), 'utf8').startsWith('job A')).length;
  check(wrong === 0, `the keys job A reports as copied hold job A's content (${wrong} wrong)`);
}

async function scenarioE() {
  console.log('\n== E  Workbook whose first sheet is a summary');
  const src = source('E', ['e1.pdf', 'e2.pdf', 'old.pdf']);
  const { id } = await call('POST', '/api/jobs', { name: 'Scenario E', source: { type: 'Local', path: src }, destination: { type: 'Local', path: join(root, 'E-out') } });
  const up = await call('POST', `/api/jobs/${id}/mappings`, form([['multi-sheet.xlsx', readFileSync(join(here, 'fixtures', 'multi-sheet.xlsx'))]]));
  check(up.rows === 2 && up.files[0].info.headers.includes('FileName'), `data sheet found after a summary sheet, hidden sheet skipped (${up.rows} rows)`);
  const j = await scan(id);
  check(count(j.counts, 'READY') === 2 && count(j.counts, 'UNMAPPED') === 1, `only the data sheet is used (${JSON.stringify(j.counts)})`);
}

async function scenarioF() {
  console.log('\n== F  Rule for a file that matches several records: flag, or keep all');
  const src = source('F', ['amb.pdf', 'x.pdf', 'a.pdf']);
  const csv = ['ID,EmployeeCode,Type,UploadedOn,FileName',
    'R1,E1,Offer,2024-01-01,amb.pdf', 'R2,E2,PAN,2023-01-01,amb.pdf', 'R3,E3,Form16,2022-01-01,amb.pdf',
    'R4,E4,Offer,2024-01-01,x.pdf', 'R5,E4,PAN,2024-01-01,x.pdf', 'R6,E6,Offer,2024-01-01,a.pdf'].join('\r\n');
  const out = join(root, 'F-out');
  const { id } = await call('POST', '/api/jobs', { name: 'Scenario F', source: { type: 'Local', path: src }, destination: { type: 'Local', path: out } });
  await call('POST', `/api/jobs/${id}/mappings`, form([['log.csv', csv]]));
  let job = await call('GET', `/api/jobs/${id}`);
  check(job.config.ambiguousRule === 'Flag', 'default rule: flag as ambiguous');
  let j = await scan(id);
  check(count(j.counts, 'AMBIGUOUS') === 2 && count(j.counts, 'READY') === 1 && j.discovered === 3, `flag: 2 ambiguous files (${JSON.stringify(j.counts)})`);
  const bad = await fails(call('PUT', `/api/jobs/${id}/config`, { ...job.config, ambiguousRule: 'Guess' }));
  check(bad?.status === 400, 'an unknown rule is refused');
  // keep all, with a template that puts two of x.pdf's records on the same path
  await call('PUT', `/api/jobs/${id}/config`, { ...job.config, ambiguousRule: 'KeepAll', template: '{EmployeeCode}/{FileName}' });
  j = await scan(id); let all = await items(id);
  const x = all.filter(i => i.fileName === 'x.pdf');
  check(x.length === 2 && x.filter(i => i.status === 'READY').length === 1 && x.some(i => i.status === 'DUPLICATE' && /Destination path collision/.test(i.reason)),
    'keep all: two records that give the same path keep one copy, the other is DUPLICATE');
  // excluding one copy of a file is remembered for that copy only
  await call('POST', `/api/jobs/${id}/items/exclude`, { ids: [x.find(i => i.status === 'DUPLICATE').id], note: 'Second copy not needed' });
  await scan(id);
  const x2 = (await items(id)).filter(i => i.fileName === 'x.pdf');
  check(x2.filter(i => i.status === 'EXCLUDED').length === 1 && x2.filter(i => i.status === 'READY').length === 1, 'exclusion of one copy is re-applied to that copy only after a new scan');
  // keep all with the default template: every record gets its copy
  await call('PUT', `/api/jobs/${id}/config`, { ...job.config, ambiguousRule: 'KeepAll' });
  j = await scan(id); all = await items(id);
  check(count(j.counts, 'READY') === 6 && count(j.counts, 'AMBIGUOUS') === 0 && j.discovered === 3, `keep all: one item per record, files still counted once (${JSON.stringify(j.counts)}, ${j.discovered} files)`);
  const amb = all.filter(i => i.fileName === 'amb.pdf');
  check(amb.length === 3 && amb.every(i => i.status === 'READY' && /kept for each/.test(i.reason)) && new Set(amb.map(i => i.destKey)).size === 3,
    `each record has its own destination: ${amb.map(i => i.destKey).join(' | ')}`);
  await call('POST', `/api/jobs/${id}/approve`, { acknowledge: true });
  const done = await waitFor(id, ['Completed', 'Completed with exceptions', 'Paused']);
  check(count(done.counts, 'PROCESSED') === 6 && count(done.counts, 'FAILED') === 0, `copied 6 (${JSON.stringify(done.counts)})`);
  check(['E1/Offer/2024', 'E2/PAN/2023', 'E3/Form16/2022'].every(p => existsSync(join(out, ...p.split('/'), 'amb.pdf'))), 'the file is at each record\'s destination');
  const rep = await (await call('GET', `/api/jobs/${id}/report?kind=reconciliation&format=csv`, undefined, true)).text();
  const rows = rep.trim().split('\r\n').filter(l => l.includes('amb.pdf') && l.includes(',PROCESSED,'));
  check(rows.length === 3 && ['R1', 'R2', 'R3'].every(r => rows.some(l => l.includes(`,${r},`))), 'the final report has one row per record for the file');
  const xl = await call('GET', `/api/jobs/${id}/report?kind=reconciliation&format=xlsx`, undefined, true);
  check(xl.ok && (await xl.arrayBuffer()).byteLength > 1000, 'the Excel report downloads');
}

(async () => {
  try {
    await call('POST', '/api/auth/login', { userName: 'admin', password });
    await scenarioA();
    await scenarioB();
    await scenarioC();
    await scenarioE();
    await scenarioF();
    if (process.env.SCEN_CLOUD !== '0') await scenarioD();
  } catch (e) { console.error('ERROR', e.stack || e.message); failures++; }
  console.log(failures ? `\n${failures} check(s) failed` : '\nAll checks passed');
  process.exit(failures ? 1 : 0);
})();
