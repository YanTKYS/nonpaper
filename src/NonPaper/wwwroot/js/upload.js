import { api, element, eventId, token, clearMessage, showError } from './common.js';

let eventData;
let busy = false;
const view = {
  back: element('back'),
  send: element('send'),
  files: element('files'),
  drop: element('drop'),
  progress: element('progress'),
  documents: element('documents'),
};

view.back.href = `/manage.html?event=${encodeURIComponent(eventId ?? '')}&token=${encodeURIComponent(token ?? '')}`;

function sorted() {
  return [...(eventData?.documents ?? [])].sort((a, b) => a.order - b.order);
}

// 通信中は資料を操作できないようにし、二重登録や順序の取り違えを防ぐ。
function setBusy(value) {
  busy = value;
  view.send.disabled = value;
  view.documents.querySelectorAll('button').forEach(button => {
    button.disabled = value || button.dataset.disabled === '1';
  });
}

async function run(action) {
  if (busy) return;
  setBusy(true);
  // 処理の途中で表示したエラーを消さないよう、メッセージは開始時に消す。
  clearMessage();
  try {
    await action();
  } catch (e) {
    showError(e);
  } finally {
    setBusy(false);
  }
}

async function load() {
  try {
    eventData = await api(`/api/events/${eventId}/manage`, {}, true);
    render();
  } catch (e) {
    showError(e);
  }
}

function render() {
  view.documents.replaceChildren();
  const list = sorted();
  list.forEach((doc, index) => {
    const row = document.createElement('div');
    row.className = 'document';
    const name = document.createElement('strong');
    name.textContent = doc.title;
    row.append(name);
    [
      ['名称変更', () => rename(doc), false],
      ['↑', () => move(index, -1), index === 0],
      ['↓', () => move(index, 1), index === list.length - 1],
      ['削除', () => remove(doc), false],
    ].forEach(([label, action, disabled]) => {
      const button = document.createElement('button');
      button.textContent = label;
      if (disabled) {
        button.disabled = true;
        button.dataset.disabled = '1';
      }
      button.addEventListener('click', action);
      row.append(button);
    });
    view.documents.append(row);
  });
  if (!list.length) view.documents.textContent = '資料はまだありません。';
}

function rename(doc) {
  const value = prompt('新しい資料表示名', doc.title);
  if (value === null) return;
  if (!value.trim()) {
    showError(new Error('資料名を1～200文字で入力してください。'));
    return;
  }
  return run(async () => {
    await api(`/api/events/${eventId}/documents/${doc.id}`, { method: 'PUT', body: JSON.stringify({ title: value }) }, true);
    await load();
  });
}

function move(index, delta) {
  const list = sorted();
  const target = index + delta;
  if (target < 0 || target >= list.length) return;
  [list[index], list[target]] = [list[target], list[index]];
  return run(async () => {
    await api(`/api/events/${eventId}/documents/order`, { method: 'PUT', body: JSON.stringify({ documentIds: list.map(x => x.id) }) }, true);
    await load();
  });
}

function remove(doc) {
  if (!confirm(`資料「${doc.title}」を削除しますか？`)) return;
  return run(async () => {
    await api(`/api/events/${eventId}/documents/${doc.id}`, { method: 'DELETE' }, true);
    await load();
  });
}

function upload(list) {
  if (!list.length || busy) return;
  const form = new FormData();
  [...list].forEach(x => form.append('files', x));
  view.progress.textContent = 'アップロード処理中です…';
  return run(async () => {
    try {
      await api(`/api/events/${eventId}/documents`, { method: 'POST', body: form }, true);
      view.files.value = '';
      view.progress.textContent = 'アップロードが完了しました。';
      await load();
    } catch (e) {
      view.progress.textContent = '';
      throw e;
    }
  });
}

view.send.addEventListener('click', () => upload(view.files.files));
view.drop.addEventListener('dragover', e => e.preventDefault());
view.drop.addEventListener('drop', e => {
  e.preventDefault();
  upload(e.dataTransfer.files);
});

load();
