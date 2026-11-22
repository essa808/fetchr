// Fetchr extension service worker.
// Watches downloads the browser starts; when the file type is one Fetchr handles, it pauses the
// browser's download, hands the link (with the page's cookies) to the Fetchr app through the native
// host, and cancels the browser's copy only once the app has accepted it. Anything else, or any
// failure, leaves the browser's download running untouched.

const HOST = 'com.fetchr.host';
const FALLBACK_TYPES = ['zip', 'rar', '7z', 'gz', 'tgz', 'xz', 'bz2', 'tar', 'iso', '001', 'exe', 'msi', 'msix', 'apk', 'dmg',
  'mp4', 'mkv', 'avi', 'mov', 'webm', 'm4v', 'mp3', 'm4a', 'flac', 'wav', 'pdf', 'epub'];
const CONFIG_TTL_MS = 60_000;

let config = null; // { types, minSize, at }

async function native(message) {
  try {
    return await chrome.runtime.sendNativeMessage(HOST, message);
  } catch (e) {
    return { ok: false, running: false, error: String((e && e.message) || e) };
  }
}

async function getConfig() {
  if (config && Date.now() - config.at < CONFIG_TTL_MS) return config;
  const r = await native({ type: 'get_config' });
  if (r && r.ok && Array.isArray(r.types)) config = { types: r.types, minSize: r.minSize || 0, at: Date.now() };
  return config || { types: FALLBACK_TYPES, minSize: 0, at: 0 };
}

function settings() {
  return chrome.storage.local.get({ enabled: true, excluded: [] });
}

function extOf(name) {
  const m = /\.([a-z0-9]{1,6})$/i.exec(name || '');
  return m ? m[1].toLowerCase() : '';
}

function hostOf(url) {
  try { return new URL(url).hostname; } catch { return ''; }
}

async function cookieHeader(url) {
  try {
    const cookies = await chrome.cookies.getAll({ url });
    return cookies.map(c => `${c.name}=${c.value}`).join('; ');
  } catch {
    return '';
  }
}

async function capture(fields) {
  return native({ type: 'capture', userAgent: navigator.userAgent, cookies: await cookieHeader(fields.finalUrl || fields.url), ...fields });
}

const seen = new Set();

chrome.downloads.onDeterminingFilename.addListener((item, suggest) => {
  suggest(); // keep the browser's own choice of name; we only decide whether to take the download over
  takeOver(item);
});

async function takeOver(item) {
  if (seen.has(item.id)) return;
  seen.add(item.id);

  const url = item.finalUrl || item.url;
  if (!/^https?:/i.test(url)) return;           // blob:, data:, file: stay with the browser
  if (item.byExtensionId) return;                // another extension started it on purpose
  const s = await settings();
  if (!s.enabled) return;
  if (s.excluded.includes(hostOf(item.referrer || url))) return;

  const cfg = await getConfig();
  const name = (item.filename || '').split(/[\\/]/).pop();
  if (!cfg.types.includes(extOf(name))) return;
  if (item.fileSize > 0 && item.fileSize < cfg.minSize) return;

  try { await chrome.downloads.pause(item.id); } catch { /* may already be finished */ }
  const [now] = await chrome.downloads.search({ id: item.id });
  if (!now || now.state !== 'in_progress') return; // too small: the browser already has it

  const reply = await capture({
    url: item.url,
    finalUrl: item.finalUrl,
    referrer: item.referrer,
    fileName: name,
    mime: item.mime,
    size: item.fileSize > 0 ? item.fileSize : undefined,
  });

  if (reply && reply.accepted) {
    try { await chrome.downloads.cancel(item.id); } catch { }
    try { await chrome.downloads.erase({ id: item.id }); } catch { }
  } else {
    try { await chrome.downloads.resume(item.id); } catch { }
  }
}

// Right-click a link → "Download with Fetchr", whatever its type.
chrome.runtime.onInstalled.addListener(() => {
  chrome.contextMenus.create({ id: 'fetchr-link', title: chrome.i18n.getMessage('menuDownload'), contexts: ['link'] });
});

chrome.contextMenus.onClicked.addListener(async (info, tab) => {
  if (info.menuItemId !== 'fetchr-link' || !/^https?:/i.test(info.linkUrl || '')) return;
  await capture({ url: info.linkUrl, referrer: tab && tab.url });
});
