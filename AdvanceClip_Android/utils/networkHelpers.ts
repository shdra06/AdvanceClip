// Network utility helpers for AdvanceClip Android

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

/** Resolve the best reachable URL for a device by trying all candidates */
export const resolveOptimalUrl = async (
  targetDeviceOrGlobal: any,
  fetchFn = fetchWithTimeout
): Promise<string | null> => {
  if (!targetDeviceOrGlobal || targetDeviceOrGlobal === 'Global') return null;

  const candidates: string[] = [];
  if (targetDeviceOrGlobal.Url && targetDeviceOrGlobal.Url.startsWith('http')) {
    candidates.push(targetDeviceOrGlobal.Url.endsWith('/') ? targetDeviceOrGlobal.Url.slice(0, -1) : targetDeviceOrGlobal.Url);
  }
  if (targetDeviceOrGlobal.LocalIp) {
    targetDeviceOrGlobal.LocalIp.split(',').forEach((ip: string) => {
      let cleanIp = ip.trim();
      if (!cleanIp.startsWith('http')) cleanIp = `http://${cleanIp}`;
      const hostPart = cleanIp.replace(/^https?:\/\//, '');
      if (!hostPart.includes(':')) cleanIp = `${cleanIp}:8999`;
      if (!candidates.includes(cleanIp)) candidates.push(cleanIp);
    });
  }
  if (targetDeviceOrGlobal.GlobalUrl && targetDeviceOrGlobal.GlobalUrl.includes('trycloudflare.com')) {
    if (!candidates.includes(targetDeviceOrGlobal.GlobalUrl)) candidates.push(targetDeviceOrGlobal.GlobalUrl);
  }

  for (const url of candidates) {
    try {
      const res = await fetchFn(`${url}/api/health`, {
        method: 'GET',
        headers: { 'X-Advance-Client': 'MobileCompanion' },
      }, 1500);
      if (res.ok) return url;
    } catch (e) {}
  }

  return candidates.length > 0 ? candidates[0] : null;
};

/** Build absolute media URL from a clip item */
export const getMediaUrl = (item: any, activeDevices: any[], pcLocalIp: string): string => {
  if (item.Raw && item.Raw.startsWith('http')) return item.Raw;
  if (item.DownloadUrl && item.DownloadUrl.startsWith('http')) return item.DownloadUrl;
  if (item.PreviewUrl && item.PreviewUrl.startsWith('http')) return item.PreviewUrl;
  if (item.CachedUri && (item.CachedUri.startsWith('file://') || item.CachedUri.startsWith('/'))) return item.CachedUri;

  const relUrl = item.PreviewUrl || item.DownloadUrl || item.Raw || '';
  if (!relUrl) return '';

  const pcNode = activeDevices.find((d: any) => d.DeviceType === 'PC');
  if (pcNode) {
    let baseUrl = '';
    if (pcNode.resolvedUrl) baseUrl = pcNode.resolvedUrl.replace(/\/$/, '');
    else if (pcNode.Url) baseUrl = pcNode.Url.split(',')[0].trim().replace(/\/$/, '');
    else if (pcNode.LocalIp) {
      const ip = pcNode.LocalIp.split(',')[0].trim();
      baseUrl = ip.startsWith('http') ? ip.replace(/\/$/, '') : `http://${ip}`;
    }
    else if (pcNode.GlobalUrl) baseUrl = pcNode.GlobalUrl.replace(/\/$/, '');
    if (baseUrl) return `${baseUrl}${relUrl.startsWith('/') ? relUrl : '/' + relUrl}`;
  }

  if (pcLocalIp) {
    const rawIp = pcLocalIp.trim().replace(/\/$/, '');
    const base = rawIp.startsWith('http') ? rawIp : `http://${rawIp.includes(':') ? rawIp : rawIp + ':8999'}`;
    return `${base}${relUrl.startsWith('/') ? relUrl : '/' + relUrl}`;
  }

  return relUrl;
};
