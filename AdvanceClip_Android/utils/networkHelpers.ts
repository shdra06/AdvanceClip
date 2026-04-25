// Network utility helpers for FlyShelf Android
// Simplified: Trust Firebase data, try-then-fallback pattern, no redundant health checks

/** Fetch with configurable timeout */
export const fetchWithTimeout = async (url: string, options: any = {}, timeoutMs = 2500) => {
  const controller = new AbortController();
  const id = setTimeout(() => controller.abort(), timeoutMs);
  try {
    const response = await fetch(url, { ...options, signal: controller.signal });
    clearTimeout(id);
    return response;
  } catch (error) {
    clearTimeout(id);
    throw error;
  }
};

/** Extract subnet prefix from IP (first 3 octets) */
export const getSubnet = (ip: string): string => {
  if (!ip) return '';
  const clean = ip.replace(/^https?:\/\//, '').split(':')[0];
  const parts = clean.split('.');
  return parts.length >= 3 ? `${parts[0]}.${parts[1]}.${parts[2]}` : '';
};

/** Determine connection type for a device relative to this phone */
export const getConnectionType = (device: any, myLocalIp: string): 'Local' | 'Cloud' | 'P2P' => {
  const deviceIp = device.LocalIp || device.Url || '';
  const mySubnet = getSubnet(myLocalIp);
  const deviceSubnet = getSubnet(deviceIp);
  if (mySubnet && deviceSubnet && mySubnet === deviceSubnet) return 'Local';
  return 'Cloud';
};

/** Color map for connection types */
export const connectionColors: Record<string, string> = {
  Local: '#10B981',
  Cloud: '#F59E0B',
  P2P: '#06B6D4',
};

/**
 * Normalize a raw IP/URL into a clean http:// URL with port.
 * "192.168.1.5"       → "http://192.168.1.5:8999"
 * "192.168.1.5:8999"  → "http://192.168.1.5:8999"
 * "http://192.168.1.5" → "http://192.168.1.5:8999"
 * "https://x.trycloudflare.com/" → "https://x.trycloudflare.com"
 */
const normalizeUrl = (raw: string): string => {
  let url = raw.trim();
  if (!url) return '';
  // Cloudflare URLs are already complete
  if (url.includes('trycloudflare.com')) return url.replace(/\/$/, '');
  // Add http:// if missing
  if (!url.startsWith('http')) url = `http://${url}`;
  // Add port if missing
  const hostPart = url.replace(/^https?:\/\//, '');
  if (!hostPart.includes(':')) url = `${url}:8999`;
  return url.replace(/\/$/, '');
};

/**
 * Get ordered list of URLs to try for a device.
 * Priority: LAN (fastest) → Cloudflare (reliable) → raw Url field
 * No health checks here — caller will try-then-fallback.
 */
export const getDeviceUrls = (device: any): string[] => {
  if (!device) return [];
  const urls: string[] = [];
  const seen = new Set<string>();

  const add = (raw: string | undefined) => {
    if (!raw || raw === 'offline') return;
    const normalized = normalizeUrl(raw);
    if (normalized && !seen.has(normalized)) {
      seen.add(normalized);
      urls.push(normalized);
    }
  };

  // LAN first (lowest latency)
  add(device.LocalIp);
  // Then the Url field (often same as LocalIp, but may differ)
  add(device.Url);
  // Then Cloudflare (works from anywhere)
  add(device.GlobalUrl);

  return urls;
};

/**
 * Resolve the best reachable URL for a device.
 * Smart approach: trust Firebase data, only health-check if multiple candidates.
 * If only one URL available, use it directly (no wasted round-trip).
 */
export const resolveOptimalUrl = async (
  device: any,
  fetchFn = fetchWithTimeout
): Promise<string | null> => {
  if (!device || device === 'Global') return null;

  const urls = getDeviceUrls(device);
  if (urls.length === 0) return null;

  // If only one URL, trust it — don't waste time health-checking
  if (urls.length === 1) return urls[0];

  // Multiple URLs: quick health check to pick the fastest (LAN preferred)
  // Use Promise.race — first healthy response wins
  try {
    const result = await Promise.any(
      urls.map(async (url, idx) => {
        // Stagger Cloudflare by 500ms to prefer LAN
        if (url.includes('trycloudflare.com')) {
          await new Promise(r => setTimeout(r, 500));
        }
        const res = await fetchFn(`${url}/api/health`, {
          method: 'GET',
          headers: { 'X-Advance-Client': 'MobileCompanion' },
        }, 2000);
        if (res.ok) return url;
        throw new Error(`${url} returned ${res.status}`);
      })
    );
    return result;
  } catch {
    // All failed — return LAN URL as best guess (may recover)
    return urls[0];
  }
};

/** Build absolute media URL from a clip item */
export const getMediaUrl = (item: any, activeDevices: any[], pcLocalIp: string): string => {
  // PRIORITY 1: Local cached file (already downloaded — most reliable)
  if (item.CachedUri && (item.CachedUri.startsWith('file://') || item.CachedUri.startsWith('/'))) return item.CachedUri;
  // PRIORITY 2: Absolute URL (Cloudflare or Firebase)
  if (item.Raw && item.Raw.startsWith('http')) return item.Raw;
  if (item.DownloadUrl && item.DownloadUrl.startsWith('http')) return item.DownloadUrl;
  if (item.PreviewUrl && item.PreviewUrl.startsWith('http')) return item.PreviewUrl;

  // Relative URL — needs a base
  const relUrl = item.PreviewUrl || item.DownloadUrl || item.Raw || '';
  if (!relUrl) return '';

  const pcNode = activeDevices.find((d: any) => d.DeviceType === 'PC');
  if (pcNode) {
    const urls = getDeviceUrls(pcNode);
    if (urls.length > 0) {
      return `${urls[0]}${relUrl.startsWith('/') ? relUrl : '/' + relUrl}`;
    }
  }

  if (pcLocalIp) {
    const base = normalizeUrl(pcLocalIp);
    return `${base}${relUrl.startsWith('/') ? relUrl : '/' + relUrl}`;
  }

  return relUrl;
};
