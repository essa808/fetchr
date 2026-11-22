const t = key => chrome.i18n.getMessage(key) || key;

document.documentElement.lang = chrome.i18n.getUILanguage();
document.documentElement.dir = t('@@bidi_dir');
document.querySelectorAll('[data-i18n]').forEach(el => { el.textContent = t(el.dataset.i18n); });

const statusEl = document.getElementById('status');
const help = document.getElementById('help');
const enabled = document.getElementById('enabled');
const excluded = document.getElementById('excluded');
const siteEl = document.getElementById('site');

async function init() {
  const s = await chrome.storage.local.get({ enabled: true, excluded: [] });
  enabled.checked = s.enabled;

  const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
  let site = '';
  try { site = new URL(tab && tab.url).hostname; } catch { }
  siteEl.textContent = site;
  excluded.checked = !!site && s.excluded.includes(site);
  excluded.disabled = !site;

  enabled.addEventListener('change', () => chrome.storage.local.set({ enabled: enabled.checked }));
  excluded.addEventListener('change', async () => {
    const cur = (await chrome.storage.local.get({ excluded: [] })).excluded.filter(h => h !== site);
    if (excluded.checked) cur.push(site);
    chrome.storage.local.set({ excluded: cur });
  });

  let r;
  try { r = await chrome.runtime.sendNativeMessage('com.fetchr.host', { type: 'ping' }); } catch (e) { r = null; }
  if (!r) {
    statusEl.textContent = t('statusMissing');
    statusEl.className = 'status missing';
    help.textContent = t('helpMissing');
    help.hidden = false;
  } else if (r.running === false) {
    statusEl.textContent = t('statusNotRunning');
    statusEl.className = 'status off';
    help.textContent = t('helpNotRunning');
    help.hidden = false;
  } else {
    statusEl.textContent = t('statusConnected');
    statusEl.className = 'status ok';
  }
}

init();
