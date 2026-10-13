document.getElementById('f').addEventListener('submit', async (e) => {
  e.preventDefault();
  const err = document.getElementById('e');
  err.hidden = true;
  try {
    const res = await fetch('/api/auth/login', {
      method: 'POST',
      headers: { 'content-type': 'application/json', 'X-DocOrg': '1' },
      body: JSON.stringify({ userName: document.getElementById('u').value.trim(), password: document.getElementById('p').value })
    });
    if (res.ok) { location.href = '/'; return; }
    const j = await res.json().catch(() => ({}));
    err.textContent = j.error || 'Sign-in failed.';
  } catch {
    err.textContent = 'The server could not be reached.';
  }
  err.hidden = false;
});
document.getElementById('u').focus();
