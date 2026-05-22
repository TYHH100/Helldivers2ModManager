const browser = window.browser || window.chrome;

const NEXUS_CDN_HOST = 'files.nexus-cdn.com';
let LOCAL_SERVER_URL = 'http://localhost:7456/api/download';
let HEALTH_URL = 'http://localhost:7456/api/download/health';

const SUPPORTED_GAME_IDS = ['6119'];

async function loadConnectionSettings() {
  const result = await browser.storage.local.get(['serverHost', 'serverPort']);
  const host = result.serverHost || 'localhost';
  const port = result.serverPort || '7456';
  
  const baseUrl = `http://${host}:${port}`;
  LOCAL_SERVER_URL = `${baseUrl}/api/download`;
  HEALTH_URL = `${baseUrl}/api/download/health`;
}

function isHelldivers2Download(url) {
  const match = url.match(/files\.nexus-cdn\.com\/(\d+)\//);
  if (match) {
    const gameId = match[1];
    return SUPPORTED_GAME_IDS.includes(gameId);
  }
  return false;
}

function parseFilename(url) {
  try {
    const urlObj = new URL(url);
    const pathname = urlObj.pathname;
    const filename = pathname.split('/').pop();
    const decoded = decodeURIComponent(filename);
    return decoded.split('?')[0];
  } catch (e) {
    console.error('Failed to parse filename:', e);
    return 'downloaded_file';
  }
}

async function sendToManager(url, filename) {
  try {
    const response = await fetch(LOCAL_SERVER_URL, {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
      },
      body: JSON.stringify({
        url,
        filename,
        timestamp: Date.now()
      })
    });

    if (response.ok) {
      const result = await response.json();

      if (result.ignored) {
        console.log('Download ignored by manager:', result.reason);
        return { ignored: true, reason: result.reason };
      }

      console.log('Download request sent to manager successfully');
      return { success: true };
    } else {
      console.log('Manager returned error:', response.status);
      return { success: false, error: `HTTP ${response.status}` };
    }
  } catch (error) {
    console.error('Failed to connect to manager:', error);
    return { success: false, error: error.message };
  }
}

async function handleDownload(details) {
  const url = details.url;

  console.log('Intercepted download:', url);

  const isNexusDownload = url.includes(NEXUS_CDN_HOST);
  if (!isNexusDownload) {
    console.log('Not a Nexus download, allowing');
    return { cancel: false };
  }

  if (!isHelldivers2Download(url)) {
    console.log('Not a Helldivers 2 download, allowing');
    return { cancel: false };
  }

  const result = await browser.storage.local.get('enabled');
  const isEnabled = result.enabled !== false;

  console.log('Extension enabled:', isEnabled);

  if (!isEnabled) {
    console.log('Extension disabled, allowing download');
    return { cancel: false };
  }

  const filename = parseFilename(url);
  console.log('Parsed filename:', filename);

  const sendResult = await sendToManager(url, filename);

  if (sendResult.ignored) {
    console.log('Download ignored:', sendResult.reason);
    return { cancel: false };
  }

  if (sendResult.success) {
    console.log('Download forwarded to manager');
    showNotification('下载已转发', `正在下载: ${filename}`, null);
  } else {
    console.log('Failed to forward to manager:', sendResult.error);
    showNotification('转发失败', `${filename} - ${sendResult.error || '管理器不可用'}`, null);
  }

  return { cancel: true };
}

function showNotification(title, message, taskId) {
  try {
    if (browser.notifications) {
      const notificationId = taskId ? `download-${taskId}` : `download-${Date.now()}`;
      browser.notifications.create(notificationId, {
        type: 'basic',
        iconUrl: browser.runtime.getURL('icons/icon48.png'),
        title: title,
        message: message
      });

      setTimeout(() => {
        browser.notifications.clear(notificationId);
      }, 5000);
    }
  } catch (error) {
    console.log('Notification API not available:', error);
  }
}

browser.webRequest.onBeforeRequest.addListener(
  handleDownload,
  { urls: ["*://files.nexus-cdn.com/*"] },
  ["blocking"]
);

browser.webRequest.onCompleted.addListener(
  (details) => {
    console.log('Request completed:', details.requestId);
  },
  { urls: ["*://files.nexus-cdn.com/*"] }
);

browser.webRequest.onErrorOccurred.addListener(
  (details) => {
    console.log('Request error:', details.requestId, details.error);
  },
  { urls: ["*://files.nexus-cdn.com/*"] }
);

browser.runtime.onInstalled.addListener((details) => {
  console.log('Extension installed:', details.reason);
  browser.storage.local.set({
    enabled: true,
    serverHost: 'localhost',
    serverPort: '7456'
  });
  loadConnectionSettings();
});

browser.runtime.onMessage.addListener((message, sender, sendResponse) => {
  if (message.type === 'checkManager') {
    fetch(HEALTH_URL)
      .then(response => {
        sendResponse({ available: response.ok });
      })
      .catch(() => sendResponse({ available: false }));
    return true;
  }
  
  if (message.type === 'settingsChanged') {
    loadConnectionSettings();
    sendResponse({ success: true });
    return true;
  }
});

loadConnectionSettings();
