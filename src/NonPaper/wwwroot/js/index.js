import { api, copy, showError } from './common.js';
document.querySelector('#create').addEventListener('submit', async event => {
  event.preventDefault();
  try {
    const data = await api('/api/events', { method: 'POST', body: JSON.stringify({ title: title.value, description: description.value, startsAt: new Date(starts.value).toISOString(), endsAt: new Date(ends.value).toISOString() }) });
    const manage = new URL('/manage.html', location.origin); manage.searchParams.set('event', data.event.id); manage.searchParams.set('token', data.managementToken);
    const meeting = new URL('/meeting.html', location.origin); meeting.searchParams.set('event', data.event.id);
    manageUrl.textContent = manage.href; meetingUrl.textContent = meeting.href;
    uploadLink.href = `/upload.html?${manage.searchParams}`; result.classList.remove('hidden');
  } catch (e) { showError(e); }
});
document.querySelectorAll('[data-copy]').forEach(button => button.addEventListener('click', () => copy(document.querySelector(`#${button.dataset.copy}`).textContent)));
