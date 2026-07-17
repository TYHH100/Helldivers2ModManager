import {
  API_BASE_URL,
  createAuthenticatedHeaders,
  isHelldivers2DownloadUrl,
  parseFilename
} from './protocol.js';

const extensionApi = globalThis.browser ?? globalThis.chrome;

async function sendToManager(url, filename) {
  const settings = await extensionApi.storage.local.get(['pairingToken']);
  if (!settings.pairingToken) {
    return { success: false, error: 'Not paired' };
  }

  try {
    const response = await fetch(`${API_BASE_URL}/downloads`, {
      method: 'POST',
      headers: createAuthenticatedHeaders(settings.pairingToken),
      body: JSON.stringify({ url, filename })
    });
    if (!response.ok) {
      return { success: false, error: `HTTP ${response.status}` };
    }
    return { success: true, ...(await response.json()) };
  } catch (error) {
    return { success: false, error: error instanceof Error ? error.message : String(error) };
  }
}

async function handleDownload(details) {
  if (!isHelldivers2DownloadUrl(details.url)) {
    return { cancel: false };
  }

  const settings = await extensionApi.storage.local.get(['enabled', 'pairingToken']);
  if (settings.enabled !== true || !settings.pairingToken) {
    return { cancel: false };
  }

  const filename = parseFilename(details.url);
  const result = await sendToManager(details.url, filename);
  if (!result.success) {
    await showNotification('Forwarding failed', `${filename} - ${result.error}`);
    return { cancel: false };
  }

  await showNotification('Download queued', `${filename} is waiting for import confirmation.`);
  return { cancel: true };
}

async function showNotification(title, message) {
  if (!extensionApi.notifications) {
    return;
  }
  const notificationId = `download-${Date.now()}`;
  await extensionApi.notifications.create(notificationId, {
    type: 'basic',
    iconUrl: extensionApi.runtime.getURL('icons/icon48.png'),
    title,
    message
  });
  setTimeout(() => extensionApi.notifications.clear(notificationId), 5000);
}

extensionApi.webRequest.onBeforeRequest.addListener(
  handleDownload,
  { urls: ['*://files.nexus-cdn.com/*'] },
  ['blocking']
);

extensionApi.runtime.onInstalled.addListener(async () => {
  const existing = await extensionApi.storage.local.get(['enabled']);
  if (typeof existing.enabled !== 'boolean') {
    await extensionApi.storage.local.set({ enabled: false });
  }
});

extensionApi.runtime.onMessage.addListener((message, _sender, sendResponse) => {
  if (message.type !== 'checkManager') {
    return false;
  }
  extensionApi.storage.local.get(['pairingToken']).then(async ({ pairingToken }) => {
    if (!pairingToken) {
      sendResponse({ available: false, error: 'Not paired' });
      return;
    }
    try {
      const response = await fetch(`${API_BASE_URL}/health`, {
        headers: createAuthenticatedHeaders(pairingToken)
      });
      sendResponse({ available: response.ok });
    } catch {
      sendResponse({ available: false });
    }
  });
  return true;
});
