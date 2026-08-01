import { api, copy, eventId, token, localDate, showError } from './common.js';
let current;
const labels = { draft: '下書き', published: '公開中', closed: '終了', deleting: '削除中' };
async function load() {
  try { current = await api(`/api/events/${eventId}/manage`, {}, true); title.textContent=current.title; description.textContent=current.description||'（説明なし）'; dates.textContent=`${localDate(current.startsAt)} ～ ${localDate(current.endsAt)}`; status.textContent=labels[current.status]; count.textContent=`${current.documents.length}件`; deleteTitle.textContent=current.title; upload.href=`/upload.html?event=${encodeURIComponent(eventId)}&token=${encodeURIComponent(token)}`; details.classList.remove('hidden'); danger.classList.remove('hidden'); } catch(e){showError(e);}
}
document.querySelectorAll('[data-status]').forEach(b=>b.addEventListener('click',async()=>{try{await api(`/api/events/${eventId}/status/${b.dataset.status}`,{method:'POST'},true);await load();}catch(e){showError(e);}}));
copyMeeting.addEventListener('click',()=>copy(`${location.origin}/meeting.html?event=${encodeURIComponent(eventId)}`));
document.querySelector('#delete').addEventListener('click',async()=>{if(!confirm(`イベント「${current.title}」を削除します。元に戻せません。よろしいですか？`))return;try{await api(`/api/events/${eventId}`,{method:'DELETE'},true);details.classList.add('hidden');danger.classList.add('hidden');showError(new Error('このイベントは削除されました。'));}catch(e){showError(e);}}); load();
