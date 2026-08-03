import { api, copyToClipboard, eventId, token, localDate, clearMessage, showError } from './common.js';

let current;
let busy = false;
const labels = { draft: '下書き', published: '公開中', closed: '終了', deleting: '削除中' };
const descriptions = {
  draft: 'この会議は下書きです。資料を編集できますが、参加者は閲覧できません。',
  published: 'この会議は公開中です。資料を編集するには下書きへ戻してください。',
  closed: 'この会議は終了しています。状態の変更や資料編集はできません。',
  deleting: 'この会議は削除処理中です。もう一度削除を実行してください。',
};
const transitions = { draft: ['published', 'closed'], published: ['draft', 'closed'], closed: [] };
const statusButtons = document.querySelectorAll('[data-status]');

function updateActions() {
  statusDescription.textContent = descriptions[current.status] || '';
  upload.classList.toggle('hidden', current.status !== 'draft');
  statusButtons.forEach(button =>
    button.classList.toggle('hidden', !transitions[current.status]?.includes(button.dataset.status)));
}

// 通信中の再クリックで同じ操作が二重に実行されないようにする。
function setBusy(value) {
  busy = value;
  statusButtons.forEach(button => { button.disabled = value; });
  document.querySelector('#delete').disabled = value;
}

async function load() {
  try {
    current = await api(`/api/events/${eventId}/manage`, {}, true);
    title.textContent = current.title;
    description.textContent = current.description || '（説明なし）';
    dates.textContent = `${localDate(current.startsAt)} ～ ${localDate(current.endsAt)}`;
    status.textContent = labels[current.status] ?? current.status;
    count.textContent = `${current.documents.length}件`;
    deleteTitle.textContent = current.title;
    upload.href = `/upload.html?event=${encodeURIComponent(eventId ?? '')}&token=${encodeURIComponent(token ?? '')}`;
    updateActions();
    details.classList.remove('hidden');
    danger.classList.remove('hidden');
  } catch (e) {
    showError(e);
  }
}

statusButtons.forEach(button => button.addEventListener('click', async () => {
  if (busy) return;
  const next = button.dataset.status;
  if (next === 'closed' && !confirm('会議を終了すると、参加者は資料を閲覧できなくなり、再公開できません。\n会議を終了しますか？')) return;
  if (next === 'draft' && !confirm('下書きに戻すと、参加者は資料を閲覧できなくなります。\n下書きに戻しますか？')) return;
  setBusy(true);
  try {
    await api(`/api/events/${eventId}/status/${next}`, { method: 'POST' }, true);
    clearMessage();
    await load();
  } catch (e) {
    showError(e);
    await load();
  } finally {
    setBusy(false);
  }
}));

copyMeeting.addEventListener('click', () => copyToClipboard(`${location.origin}/meeting.html?event=${encodeURIComponent(eventId ?? '')}`));

document.querySelector('#delete').addEventListener('click', async () => {
  if (busy) return;
  if (!confirm(`イベント「${current.title}」を削除します。元に戻せません。よろしいですか？`)) return;
  setBusy(true);
  try {
    await api(`/api/events/${eventId}`, { method: 'DELETE' }, true);
    details.classList.add('hidden');
    danger.classList.add('hidden');
    showError(new Error('このイベントは削除されました。'));
  } catch (e) {
    showError(e);
  } finally {
    setBusy(false);
  }
});

load();
