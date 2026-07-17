export const API_BASE_URL = 'http://127.0.0.1:7456/api/v2';

export function createAuthenticatedHeaders(token, now = Date.now(), requestId = crypto.randomUUID()) {
  if (!token) {
    throw new Error('A pairing token is required.');
  }
  return {
    'Content-Type': 'application/json',
    Authorization: `Bearer ${token}`,
    'X-Request-Id': requestId,
    'X-Timestamp': String(now)
  };
}

export async function unpairManager(token, fetchImplementation = fetch) {
  return fetchImplementation(`${API_BASE_URL}/unpair`, {
    method: 'POST',
    headers: createAuthenticatedHeaders(token),
    body: '{}'
  });
}

export function isAllowedNexusUrl(value) {
  try {
    const url = new URL(value);
    const host = url.hostname.toLowerCase();
    return url.protocol === 'https:' &&
      !url.username &&
      !url.password &&
      (host === 'files.nexus-cdn.com' || host === 'nexusmods.com' || host.endsWith('.nexusmods.com'));
  } catch {
    return false;
  }
}

export function isHelldivers2DownloadUrl(value) {
  if (!isAllowedNexusUrl(value)) {
    return false;
  }
  const url = new URL(value);
  return url.hostname.toLowerCase() === 'files.nexus-cdn.com' && /^\/6119\//.test(url.pathname);
}

export function parseFilename(value) {
  try {
    const url = new URL(value);
    const encodedName = url.pathname.split('/').filter(Boolean).at(-1) ?? '';
    const decodedName = decodeURIComponent(encodedName);
    return decodedName || 'downloaded_file';
  } catch {
    return 'downloaded_file';
  }
}
