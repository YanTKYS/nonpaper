export const query = new URLSearchParams(location.search);
export const eventId = query.get('event');
export const token = query.get('token');
export function showError(error, target = document.querySelector('#message')) {
  target.className = 'error';
  target.textContent = error?.message || '処理中にエラーが発生しました。';
  target.classList.remove('hidden');
}
export async function api(url, options = {}, authorized = false) {
  const headers = new Headers(options.headers || {});
  if (authorized) headers.set('Authorization', `Bearer ${token || ''}`);
  if (options.method && options.method !== 'GET') headers.set('X-NonPaper-Request', '1');
  if (options.body && !(options.body instanceof FormData)) headers.set('Content-Type', 'application/json');
  const response = await fetch(url, { ...options, headers, cache: 'no-store' });
  if (!response.ok) {
    let body; try { body = await response.json(); } catch { body = {}; }
    throw new Error(body.message || '処理に失敗しました。');
  }
  return response.status === 204 ? null : response.json();
}
export async function copy(text) {
  await navigator.clipboard.writeText(text);
}
export function localDate(value) { return new Date(value).toLocaleString('ja-JP'); }
