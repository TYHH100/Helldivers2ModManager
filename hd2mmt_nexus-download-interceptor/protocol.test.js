import { describe, expect, it, vi } from 'vitest';
import {
  API_BASE_URL,
  createAuthenticatedHeaders,
  isAllowedNexusUrl,
  isHelldivers2DownloadUrl,
  parseFilename,
  unpairManager
} from './protocol.js';

describe('secure protocol v2', () => {
  it('uses only the IPv4 loopback v2 endpoint', () => {
    expect(API_BASE_URL).toBe('http://127.0.0.1:7456/api/v2');
  });

  it('adds bearer, timestamp and non-reusable request id headers', () => {
    vi.stubGlobal('crypto', { randomUUID: () => '4fb359a1-43e4-44d8-9348-f6db589b2460' });
    expect(createAuthenticatedHeaders('token', 1234)).toEqual({
      'Content-Type': 'application/json',
      Authorization: 'Bearer token',
      'X-Request-Id': '4fb359a1-43e4-44d8-9348-f6db589b2460',
      'X-Timestamp': '1234'
    });
  });

  it('notifies the paired desktop service before removing local pairing state', async () => {
    vi.stubGlobal('crypto', { randomUUID: () => '4fb359a1-43e4-44d8-9348-f6db589b2460' });
    const fetchImplementation = vi.fn().mockResolvedValue({ ok: true, status: 200 });

    await unpairManager('token', fetchImplementation);

    expect(fetchImplementation).toHaveBeenCalledWith(`${API_BASE_URL}/unpair`, {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        Authorization: 'Bearer token',
        'X-Request-Id': '4fb359a1-43e4-44d8-9348-f6db589b2460',
        'X-Timestamp': expect.any(String)
      },
      body: '{}'
    });
  });

  it.each([
    ['https://files.nexus-cdn.com/6119/mod.zip', true],
    ['https://www.nexusmods.com/helldivers2/mods/1', true],
    ['https://evilnexusmods.com/6119/mod.zip', false],
    ['https://nexusmods.com.evil.test/6119/mod.zip', false],
    ['http://files.nexus-cdn.com/6119/mod.zip', false],
    ['https://user:secret@nexusmods.com/file.zip', false]
  ])('validates the exact Nexus host boundary for %s', (url, allowed) => {
    expect(isAllowedNexusUrl(url)).toBe(allowed);
  });

  it('intercepts only the Helldivers 2 CDN game id', () => {
    expect(isHelldivers2DownloadUrl('https://files.nexus-cdn.com/6119/mod.zip')).toBe(true);
    expect(isHelldivers2DownloadUrl('https://files.nexus-cdn.com/9999/mod.zip')).toBe(false);
  });

  it('decodes a filename without accepting query text as part of it', () => {
    expect(parseFilename('https://files.nexus-cdn.com/6119/My%20Mod.zip?x=1')).toBe('My Mod.zip');
  });
});
