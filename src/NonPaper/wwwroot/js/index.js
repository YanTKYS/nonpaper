import { api, copyToClipboard, clearMessage, showError } from './common.js';

const form = document.querySelector('#create');
const submit = form.querySelector('button');

function isoDate(value) {
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? null : date.toISOString();
}

form.addEventListener('submit', async event => {
  event.preventDefault();
  const startsAt = isoDate(starts.value);
  const endsAt = isoDate(ends.value);
  if (!startsAt || !endsAt) {
    showError(new Error('開催日時と終了日時を入力してください。'));
    return;
  }
  // 送信中の再クリックで会議が二重に作成されないようにする。
  submit.disabled = true;
  try {
    const data = await api('/api/events', {
      method: 'POST',
      body: JSON.stringify({
        title: title.value,
        description: description.value,
        startsAt,
        endsAt,
      }),
    });
    const manage = new URL('/manage.html', location.origin);
    manage.searchParams.set('event', data.event.id);
    manage.searchParams.set('token', data.managementToken);
    const meeting = new URL('/meeting.html', location.origin);
    meeting.searchParams.set('event', data.event.id);
    manageUrl.textContent = manage.href;
    meetingUrl.textContent = meeting.href;
    uploadLink.href = `/upload.html?${manage.searchParams}`;
    clearMessage();
    result.classList.remove('hidden');
  } catch (e) {
    showError(e);
  } finally {
    submit.disabled = false;
  }
});

document.querySelectorAll('[data-copy]').forEach(button =>
  button.addEventListener('click', () => copyToClipboard(document.querySelector(`#${button.dataset.copy}`).textContent)));
