import { api, copyToClipboard, element, clearMessage, showError } from './common.js';

const form = element('create');
const submit = form.querySelector('button');
const view = {
  title: element('title'),
  description: element('description'),
  starts: element('starts'),
  ends: element('ends'),
  result: element('result'),
  manageUrl: element('manageUrl'),
  meetingUrl: element('meetingUrl'),
  uploadLink: element('uploadLink'),
};

function isoDate(value) {
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? null : date.toISOString();
}

form.addEventListener('submit', async event => {
  event.preventDefault();
  const startsAt = isoDate(view.starts.value);
  const endsAt = isoDate(view.ends.value);
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
        title: view.title.value,
        description: view.description.value,
        startsAt,
        endsAt,
      }),
    });
    const manage = new URL('/manage.html', location.origin);
    manage.searchParams.set('event', data.event.id);
    manage.searchParams.set('token', data.managementToken);
    const meeting = new URL('/meeting.html', location.origin);
    meeting.searchParams.set('event', data.event.id);
    view.manageUrl.textContent = manage.href;
    view.meetingUrl.textContent = meeting.href;
    view.uploadLink.href = `/upload.html?${manage.searchParams}`;
    clearMessage();
    view.result.classList.remove('hidden');
  } catch (e) {
    showError(e);
  } finally {
    submit.disabled = false;
  }
});

document.querySelectorAll('[data-copy]').forEach(button =>
  button.addEventListener('click', () => copyToClipboard(element(button.dataset.copy).textContent)));
