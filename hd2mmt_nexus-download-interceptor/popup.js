const browser = window.browser || window.chrome;

let statusCheckInterval = null;

function getServerUrl() {
  const host = document.getElementById('serverHost').value || 'localhost';
  const port = document.getElementById('serverPort').value || '7456';
  return `http://${host}:${port}`;
}

async function loadSettings() {
  const result = await browser.storage.local.get(['enabled', 'serverHost', 'serverPort']);
  
  const enabledToggle = document.getElementById('enabledToggle');
  const serverHostInput = document.getElementById('serverHost');
  const serverPortInput = document.getElementById('serverPort');
  
  if (result.enabled !== false) {
    enabledToggle.classList.add('active');
  } else {
    enabledToggle.classList.remove('active');
  }
  
  if (result.serverHost) {
    serverHostInput.value = result.serverHost;
  }
  
  if (result.serverPort) {
    serverPortInput.value = result.serverPort;
  }
}

async function requestHostPermission(url) {
  try {
    const host = new URL(url).host;
    const permission = `http://${host}/*`;
    
    const hasPermission = await browser.permissions.contains({
      origins: [permission]
    });
    
    if (!hasPermission) {
      if (!browser.permissions.request) {
        console.log('permissions.request not available, skipping permission request');
        return true;
      }
      
      const granted = await browser.permissions.request({
        origins: [permission]
      });
      
      if (granted) {
        console.log(`Permission granted for ${permission}`);
        return true;
      } else {
        console.log(`Permission denied for ${permission}`);
        return false;
      }
    }
    return true;
  } catch (error) {
    console.error('Failed to request permission:', error);
    return false;
  }
}

async function saveSettings() {
  const enabledToggle = document.getElementById('enabledToggle');
  const serverHost = document.getElementById('serverHost').value;
  const serverPort = document.getElementById('serverPort').value;
  const isEnabled = enabledToggle.classList.contains('active');
  
  await browser.storage.local.set({
    enabled: isEnabled,
    serverHost: serverHost,
    serverPort: serverPort
  });
  
  browser.runtime.sendMessage({ type: 'settingsChanged' });
  
  if (!isEnabled) {
    browser.runtime.sendMessage({ type: 'stopPolling' });
  }
}

async function requestPermissionIfNeeded() {
  const isEnabled = document.getElementById('enabledToggle').classList.contains('active');
  if (isEnabled) {
    const serverHost = document.getElementById('serverHost').value;
    const serverPort = document.getElementById('serverPort').value;
    const serverUrl = `http://${serverHost}:${serverPort}`;
    await requestHostPermission(serverUrl);
  }
}

async function checkManagerStatus() {
  const statusDot = document.getElementById('statusDot');
  const statusText = document.getElementById('statusText');
  const serverUrl = getServerUrl();
  
  try {
    const response = await fetch(`${serverUrl}/api/download/health`);
    
    if (response.ok) {
      statusDot.className = 'status-dot connected';
      statusText.textContent = '管理器已连接';
    } else {
      statusDot.className = 'status-dot disconnected';
      statusText.textContent = '管理器未运行';
    }
  } catch (error) {
    statusDot.className = 'status-dot disconnected';
    statusText.textContent = '管理器未运行';
  }
}

function startStatusCheck() {
  if (statusCheckInterval) {
    clearInterval(statusCheckInterval);
  }
  statusCheckInterval = setInterval(checkManagerStatus, 5000);
}

function stopStatusCheck() {
  if (statusCheckInterval) {
    clearInterval(statusCheckInterval);
    statusCheckInterval = null;
  }
}

document.addEventListener('DOMContentLoaded', () => {
  loadSettings();
  checkManagerStatus();
  startStatusCheck();
  
  const enabledToggle = document.getElementById('enabledToggle');
  enabledToggle.addEventListener('click', async () => {
    enabledToggle.classList.toggle('active');
    await requestPermissionIfNeeded();
    await saveSettings();
  });
  
  const serverHostInput = document.getElementById('serverHost');
  const serverPortInput = document.getElementById('serverPort');
  serverHostInput.addEventListener('change', saveSettings);
  serverPortInput.addEventListener('change', saveSettings);
  
  const refreshBtn = document.getElementById('refreshBtn');
  refreshBtn.addEventListener('click', checkManagerStatus);
});

window.addEventListener('unload', () => {
  stopStatusCheck();
});
