import { api, eventId, localDate, clearMessage, showError } from './common.js';

const pollIntervalMs = 30000;
const defaultZoom = 100;

let currentDocuments = [];
let selectedId = null;
let pageNumber = 1;
let zoomPercent = defaultZoom;
let fitToWidth = false;

function zoomParameter() {
  return fitToWidth ? 'page-width' : String(zoomPercent);
}

function source() {
  return `/api/events/${encodeURIComponent(eventId)}/documents/${encodeURIComponent(selectedId)}/content`
    + `#page=${pageNumber}&zoom=${zoomParameter()}&toolbar=0&navpanes=0`;
}

function refresh() {
  if (selectedId) pdf.src = source();
  page.value = pageNumber;
}

function markSelection() {
  const document_ = currentDocuments.find(x => x.id === selectedId);
  selected.textContent = document_ ? document_.title : '';
  documents.querySelectorAll('.document-button').forEach(button =>
    button.classList.toggle('active', button.dataset.documentId === selectedId));
}

function select(id) {
  if (id === selectedId) return;
  selectedId = id;
  pageNumber = 1;
  markSelection();
  refresh();
}

function renderDocuments() {
  documents.replaceChildren();
  // iframeは残したまま表示だけ切り替える。要素ごと差し替えると資料追加後に表示できなくなる。
  pdf.classList.toggle('hidden', currentDocuments.length === 0);
  if (!currentDocuments.length) {
    documents.textContent = '登録されている資料はありません。';
    return;
  }
  currentDocuments.forEach(item => {
    const button = document.createElement('button');
    button.className = 'document-button';
    button.dataset.documentId = item.id;
    button.textContent = item.title;
    button.addEventListener('click', () => select(item.id));
    documents.append(button);
  });
}

function sameDocuments(list) {
  return list.length === currentDocuments.length
    && list.every((x, index) => x.id === currentDocuments[index].id && x.title === currentDocuments[index].title);
}

async function load() {
  let data;
  try {
    data = await api(`/api/events/${eventId}`);
  } catch (e) {
    // 会議の終了・削除・下書きへの差し戻しは、ここで閲覧終了として扱う。
    meeting.classList.add('hidden');
    showError(e);
    return;
  }

  title.textContent = data.title;
  description.textContent = data.description || '（説明なし）';
  dates.textContent = `${localDate(data.startsAt)} ～ ${localDate(data.endsAt)}`;

  // 定期取得のたびに一覧を作り直すと、閲覧中の資料とページが先頭へ戻ってしまう。
  // 資料の構成が変わったときだけ描画し、選択中の資料は維持する。
  const list = [...data.documents].sort((a, b) => a.order - b.order);
  if (!sameDocuments(list)) {
    currentDocuments = list;
    renderDocuments();
  }
  if (selectedId && currentDocuments.some(x => x.id === selectedId)) markSelection();
  else if (currentDocuments.length) select(currentDocuments[0].id);
  else {
    selectedId = null;
    markSelection();
  }

  clearMessage();
  meeting.classList.remove('hidden');
}

previous.addEventListener('click', () => {
  if (pageNumber <= 1) return;
  pageNumber -= 1;
  refresh();
});
next.addEventListener('click', () => {
  pageNumber += 1;
  refresh();
});
page.addEventListener('change', () => {
  pageNumber = Math.max(1, Math.floor(Number(page.value)) || 1);
  refresh();
});
zoomIn.addEventListener('click', () => {
  // 「領域に合わせる」の後でも拡大・縮小を続けられるよう、倍率は常に数値で保持する。
  fitToWidth = false;
  zoomPercent = Math.min(300, zoomPercent + 25);
  refresh();
});
zoomOut.addEventListener('click', () => {
  fitToWidth = false;
  zoomPercent = Math.max(25, zoomPercent - 25);
  refresh();
});
fit.addEventListener('click', () => {
  fitToWidth = true;
  refresh();
});
fullscreen.addEventListener('click', () => {
  const request = viewer.requestFullscreen();
  if (request?.catch) request.catch(() => showError(new Error('全画面表示を開始できませんでした。')));
});

document.addEventListener('keydown', e => {
  if ((e.ctrlKey || e.metaKey) && (e.key === 'p' || e.key === 'P')) {
    e.preventDefault();
    showError(new Error('この画面では印刷機能を提供していません。'));
    return;
  }
  // ページ番号の入力中は、左右キーをカーソル移動として扱う。
  const tag = e.target?.tagName;
  if (tag === 'INPUT' || tag === 'TEXTAREA') return;
  if (e.key === 'ArrowRight') next.click();
  if (e.key === 'ArrowLeft') previous.click();
});
document.addEventListener('contextmenu', e => e.preventDefault());

// 前回の取得が終わってから次を予約し、通信が遅い環境で要求が積み上がらないようにする。
async function poll() {
  await load();
  window.setTimeout(poll, pollIntervalMs);
}
poll();
