// Sessions, settings and sign-in protections.
// Usage: node tests/e2e-security.mjs [baseUrl] [adminPassword]
const base = process.argv[2] || 'http://localhost:5080';
const password = process.argv[3] || process.env.DOCORG_ADMIN_PASSWORD;
let failures = 0;
function client() {
  let cookie = '';
  const call = async (method, path, body) => {
    const headers = { 'X-DocOrg': '1', cookie }; let payload;
    if (body !== undefined) { headers['content-type'] = 'application/json'; payload = JSON.stringify(body); }
    const res = await fetch(base + path, { method, headers, body: payload });
    const set = res.headers.get('set-cookie'); if (set) cookie = set.split(';')[0];
    const text = await res.text(); let json; try { json = text ? JSON.parse(text) : null; } catch { json = text; }
    if (!res.ok) { const e = new Error(`${method} ${path} -> ${res.status}`); e.status = res.status; e.body = json; throw e; }
    return json;
  };
  call.cookie = () => cookie; call.setCookie = c => { cookie = c; };
  return call;
}
const check = (cond, msg) => { console.log(`${cond ? 'PASS' : 'FAIL'}  ${msg}`); if (!cond) failures++; };
async function fails(p) { try { await p; return null; } catch (e) { return e; } }
(async () => {
  const admin = client();
  let before;
  try {
    await admin('POST', '/api/auth/login', { userName: 'admin', password });
    await admin('POST', '/api/users', { userName: 'sec.user', displayName: 'Sec', role: 'Operator', password: 'Security-Test-2026!' }).catch(() => {});
    await admin('PUT', '/api/users/sec.user', { password: 'Security-Test-2026!', active: true });
    // logout ends the session on the server
    const a = client(); await a('POST', '/api/auth/login', { userName: 'sec.user', password: 'Security-Test-2026!' });
    const saved = a.cookie();
    await a('POST', '/api/auth/logout');
    const replay = client(); replay.setCookie(saved);
    check((await fails(replay('GET', '/api/jobs')))?.status === 401, 'a cookie copied before logout no longer works after logout');
    // password change ends the other sessions, keeps this one
    const b1 = client(), b2 = client();
    await b1('POST', '/api/auth/login', { userName: 'sec.user', password: 'Security-Test-2026!' });
    await b2('POST', '/api/auth/login', { userName: 'sec.user', password: 'Security-Test-2026!' });
    await b1('POST', '/api/account/password', { currentPassword: 'Security-Test-2026!', newPassword: 'Security-Test-2027!' });
    check(!(await fails(b1('GET', '/api/jobs'))), 'the browser that changed the password stays signed in');
    check((await fails(b2('GET', '/api/jobs')))?.status === 401, 'other sessions of that user are signed out by a password change');
    const b3 = client(); await b3('POST', '/api/auth/login', { userName: 'sec.user', password: 'Security-Test-2027!' });
    await admin('PUT', '/api/users/sec.user', { password: 'Security-Test-2028!' });
    check((await fails(b3('GET', '/api/jobs')))?.status === 401, 'an administrator\'s password reset signs the user out');
    // deactivation and role change still take effect immediately
    const c = client(); await c('POST', '/api/auth/login', { userName: 'sec.user', password: 'Security-Test-2028!' });
    await admin('PUT', '/api/users/sec.user', { active: false });
    check((await fails(c('GET', '/api/jobs')))?.status === 401, 'a disabled user is signed out on the next request');
    await admin('PUT', '/api/users/sec.user', { active: true });
    // partial settings update keeps the other settings
    before = (await admin('GET', '/api/settings')).settings;
    await admin('PUT', '/api/settings', { ...before, requireSecondApprover: true, allowOverwrite: true });
    await admin('PUT', '/api/settings', { maxParallelCopies: 4 });
    const after = (await admin('GET', '/api/settings')).settings;
    check(after.maxParallelCopies === 4 && after.requireSecondApprover === true && after.allowOverwrite === true, 'saving one setting leaves the others unchanged');
    const badType = await fails(admin('PUT', '/api/settings', { maxParallelCopies: 'many' }));
    check(badType?.status === 400, 'a setting with a wrong type is refused');
    // failed sign-ins: locked after 5 attempts with a clear message, and the right password is then refused for 10 minutes
    const lockUser = `sec.lock${Date.now() % 100000}`;
    await admin('POST', '/api/users', { userName: lockUser, displayName: 'Lock test', role: 'Viewer', password: 'Security-Test-2028!' });
    const d = client(); let last;
    for (let i = 0; i < 5; i++) last = await fails(d('POST', '/api/auth/login', { userName: lockUser, password: 'wrong-' + i }));
    check(last?.status === 401 && /blocked for 10 minutes/.test(JSON.stringify(last.body)), 'five wrong passwords block sign-in from that computer');
    const locked = await fails(d('POST', '/api/auth/login', { userName: lockUser, password: 'Security-Test-2028!' }));
    check(locked?.status === 401, 'while blocked, even the right password is refused from that computer');
    // CSRF header and anonymous access
    const res = await fetch(base + '/api/jobs', { method: 'POST', headers: { 'content-type': 'application/json' }, body: '{}' });
    check(res.status === 400 || res.status === 401 || res.status === 403, `request without the X-DocOrg header is refused (${res.status})`);
    check((await fetch(base + '/api/jobs')).status === 401, 'anonymous access is refused');
  } catch (e) { console.error('ERROR', e.stack || e.message); failures++; }
  finally { if (before) await admin('PUT', '/api/settings', before).catch(() => {}); }
  console.log(failures ? `\n${failures} check(s) failed` : '\nAll checks passed');
  process.exit(failures ? 1 : 0);
})();
