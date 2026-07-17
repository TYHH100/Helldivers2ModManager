import { API_BASE_URL, createAuthenticatedHeaders, unpairManager } from './protocol.js';

const extensionApi = globalThis.browser ?? globalThis.chrome;
const enabledToggle = document.getElementById('enabledToggle');
const pairingCode = document.getElementById('pairingCode');
const pairButton = document.getElementById('pairButton');
const unpairButton = document.getElementById('unpairButton');
const refreshButton = document.getElementById('refreshBtn');
const statusDot = document.getElementById('statusDot');
const statusText = document.getElementById('statusText');

function setStatus(kind, text) {
  statusDot.className = `status-dot ${kind}`;
  statusText.textContent = text;
}

async function loadState() {
  const state = await extensionApi.storage.local.get(['enabled', 'pairingToken']);
  enabledToggle.checked = state.enabled === true && Boolean(state.pairingToken);
  enabledToggle.disabled = !state.pairingToken;
  pairButton.hidden = Boolean(state.pairingToken);
  pairingCode.hidden = Boolean(state.pairingToken);
  unpairButton.hidden = !state.pairingToken;
  await checkManagerStatus();
}

async function pair() {
  const code = pairingCode.value.trim();
  if (!/^\d{8}$/.test(code)) {
    setStatus('disconnected', 'Enter the 8-digit code shown by the app.');
    return;
  }
  try {
    const response = await fetch(`${API_BASE_URL}/pair`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ code })
    });
    const body = await response.json();
    if (!response.ok || !body.token) {
      setStatus('disconnected', body.error ?? `Pairing failed (${response.status})`);
      return;
    }
    await extensionApi.storage.local.set({ pairingToken: body.token, enabled: false });
    pairingCode.value = '';
    await loadState();
  } catch {
    setStatus('disconnected', 'Cannot connect to the mod manager.');
  }
}

async function unpair() {
	const { pairingToken } = await extensionApi.storage.local.get(['pairingToken']);
	if (!pairingToken) {
		await loadState();
		return;
	}

	try {
		const response = await unpairManager(pairingToken);
		if (!response.ok) {
			setStatus('disconnected', `Unpairing failed (${response.status})`);
			return;
		}
		await extensionApi.storage.local.remove(['pairingToken']);
		await extensionApi.storage.local.set({ enabled: false });
		await loadState();
	} catch {
		setStatus('disconnected', 'Manager unavailable; pairing was not removed.');
	}
}

async function checkManagerStatus() {
  const { pairingToken } = await extensionApi.storage.local.get(['pairingToken']);
  if (!pairingToken) {
    setStatus('', 'Not paired');
    return;
  }
  try {
    const response = await fetch(`${API_BASE_URL}/health`, {
      headers: createAuthenticatedHeaders(pairingToken)
    });
    setStatus(response.ok ? 'connected' : 'disconnected', response.ok ? 'Connected' : 'Authentication failed');
  } catch {
    setStatus('disconnected', 'Manager unavailable');
  }
}

enabledToggle.addEventListener('change', async () => {
  await extensionApi.storage.local.set({ enabled: enabledToggle.checked });
});
pairButton.addEventListener('click', pair);
unpairButton.addEventListener('click', unpair);
refreshButton.addEventListener('click', checkManagerStatus);
loadState();
