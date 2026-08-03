export const query = new URLSearchParams(location.search);
export const eventId = query.get('event');
export const token = query.get('token');

function messageElement() {
  return document.querySelector('#message');
}

function show(target, className, text) {
  if (!target) return;
  target.className = className;
  target.textContent = text;
}

export function showError(error, target = messageElement()) {
  show(target, 'error', error?.message || '処理中にエラーが発生しました。');
}

function showNotice(text, target = messageElement()) {
  show(target, 'success', text);
}

export function clearMessage(target = messageElement()) {
  show(target, 'hidden', '');
}

export async function api(url, options = {}, authorized = false) {
  const headers = new Headers(options.headers || {});
  if (authorized) headers.set('Authorization', `Bearer ${token || ''}`);
  if (options.method && options.method !== 'GET') headers.set('X-NonPaper-Request', '1');
  if (options.body && !(options.body instanceof FormData)) headers.set('Content-Type', 'application/json');
  let response;
  try {
    response = await fetch(url, { ...options, headers, cache: 'no-store' });
  } catch {
    throw new Error('サーバーへ接続できませんでした。ネットワークを確認してください。');
  }
  if (!response.ok) {
    let body;
    try { body = await response.json(); } catch { body = {}; }
    throw new Error(body.message || '処理に失敗しました。');
  }
  return response.status === 204 ? null : response.json();
}

async function copy(text) {
  // 庁内のHTTP接続ではClipboard APIを利用できない（安全なコンテキストではない）ため、
  // 利用できない場合はテキスト選択とexecCommandで代替する。
  try {
    if (window.isSecureContext && navigator.clipboard) {
      await navigator.clipboard.writeText(text);
      return true;
    }
  } catch {
    // フォールバックを試す
  }
  const area = document.createElement('textarea');
  area.value = text;
  area.readOnly = true;
  area.style.position = 'fixed';
  area.style.top = '-1000px';
  document.body.append(area);
  area.select();
  let copied = false;
  try { copied = document.execCommand('copy'); } catch { copied = false; }
  area.remove();
  return copied;
}

export async function copyToClipboard(text) {
  if (await copy(text)) showNotice('URLをコピーしました。');
  else showError(new Error('コピーできませんでした。URLを選択して手動でコピーしてください。'));
}

export function localDate(value) { return new Date(value).toLocaleString('ja-JP'); }
