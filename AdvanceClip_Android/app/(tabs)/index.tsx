import React, { useState, useEffect, useRef } from 'react';
import { View, Text, TextInput, TouchableOpacity, FlatList, ActivityIndicator, KeyboardAvoidingView, Platform, Alert, AppState, AppStateStatus, Modal, ToastAndroid, NativeModules, ScrollView } from 'react-native';
import { SafeAreaView } from 'react-native-safe-area-context';
import { LinearGradient } from 'expo-linear-gradient';
import * as Sharing from 'expo-sharing';
import * as IntentLauncher from 'expo-intent-launcher';
import { useSettings } from '../../context/SettingsContext';
import { IconSymbol } from '@/components/ui/icon-symbol';
import { database } from '../../firebaseConfig';
import { syncLog } from '../../utils/debugLog';
import { ref, push, set, get, onValue, query, limitToLast, orderByChild, update, remove } from 'firebase/database';
import * as DocumentPicker from 'expo-document-picker';
import * as Clipboard from 'expo-clipboard';
import * as FileSystem from 'expo-file-system/legacy';
import * as MediaLibrary from 'expo-media-library';
import { Image } from 'expo-image';
import * as WebBrowser from 'expo-web-browser';
import * as Linking from 'expo-linking';
import * as ImagePicker from 'expo-image-picker';
import { CameraView, useCameraPermissions } from 'expo-camera';
import AsyncStorage from '@react-native-async-storage/async-storage';

// ═══ Extracted Modules ═══
import { ClipItem, DOWNLOAD_BASE, SYNC_CACHE_BASE, CONVERTED_BASE, IMAGE_CACHE_BASE, getDownloadPath, getSyncCachePath, getConvertedPath } from '../../utils/clipTypes';
import { fetchWithTimeout, getSubnet, getConnectionType, connectionColors, resolveOptimalUrl, getDeviceUrls, getMediaUrl } from '../../utils/networkHelpers';
import { styles } from '../../styles/syncStyles';
import { colors, font } from '../../styles/theme';
import AnimatedCard from '../../components/AnimatedCard';
import AnimatedPressable from '../../components/AnimatedPressable';
import CachedImage from '../../components/CachedImage';
import PdfPageEditor from '../../components/PdfPageEditor';
import { mergePdfs as localMergePdfs } from '../../utils/pdfUtils';

const { AdvanceOverlay } = NativeModules;

// ════════════════════════════════════════════════════════
// MAIN SCREEN
// ════════════════════════════════════════════════════════
export default function SyncScreen() {
  const { pcLocalIp, deviceName, setDeviceName, isGlobalSyncEnabled, setGlobalSyncEnabled, isFloatingBallEnabled, addPairedDevice, pairingKey: contextPairingKey, regeneratePairingKey } = useSettings();

  useEffect(() => {
    if (Platform.OS === 'android' && AdvanceOverlay && isFloatingBallEnabled) {
      AdvanceOverlay.startOverlay();
    }
  }, [isFloatingBallEnabled]);

  // ─── Ghost Wipe Filter State ───
  const [localWipeTimestamp, setLocalWipeTimestamp] = useState<number>(0);
  const [localDeletedIds, setLocalDeletedIds] = useState<Set<string>>(new Set());

  // ─── Core State ───
  const [clips, setClips] = useState<ClipItem[]>([]);
  const lastSyncedContentRef = useRef<string>('');
  const lastSyncedImageTsRef = useRef<number>(0);
  const sentContentFingerprintsRef = useRef<Set<string>>(new Set());
  const recentSyncFingerprintsRef = useRef<Map<string, number>>(new Map());
  // Track items already pushed to native overlay DB
  const pushedToOverlayRef = useRef<Set<string>>(new Set());

  // ─── Scoped Clipboard (only paired devices see each other) ───
  const pairingKeyRef = useRef<string>('');
  useEffect(() => {
    AsyncStorage.getItem('pairingKey').then(k => { if (k) pairingKeyRef.current = k; });
  }, []);
  // Keep ref in sync when context key changes (e.g. after pairing or regeneration)
  useEffect(() => {
    if (contextPairingKey) pairingKeyRef.current = contextPairingKey;
  }, [contextPairingKey]);
  /** Returns the Firebase path scoped to the pairing key, e.g. `clipboard/abc123` */
  const clipboardPath = () => `clipboard/${pairingKeyRef.current}`;

  // ─── PC URL (auto-discovered from Firebase, no manual config needed) ───
  const cachedPcUrlRef = useRef<string | null>(null);
  const cachedPcUrlTimestampRef = useRef<number>(0);

  const getCachedPcUrl = async (): Promise<string> => {
    // Return cached URL if fresh (15s TTL)
    const now = Date.now();
    if (cachedPcUrlRef.current && (now - cachedPcUrlTimestampRef.current) < 15_000) {
      return cachedPcUrlRef.current;
    }

    // Priority 2: Stored pairing URLs from QR scan / code entry
    try {
      const storedLocal = await AsyncStorage.getItem('pairedLocalUrl');
      const storedGlobal = await AsyncStorage.getItem('pairedGlobalUrl');
      for (const url of [storedLocal, storedGlobal].filter(Boolean)) {
        try {
          const res = await fetchWithTimeout(`${url}/api/health`,
            { headers: { 'X-Advance-Client': 'MobileCompanion' } }, 2000);
          if (res.ok) {
            cachedPcUrlRef.current = url;
            cachedPcUrlTimestampRef.current = now;
            return url!;
          }
        } catch {}
      }
    } catch {}

    // Priority 3: Firebase auto-discovered devices (use REF to avoid stale closure)
    const pc = activeDevicesRef.current.find((d: any) => d.DeviceType === 'PC');
    if (pc) {
      const urls = getDeviceUrls(pc);
      const resolved = urls.length === 1 ? urls[0] : await resolveOptimalUrl(pc);
      if (resolved) {
        cachedPcUrlRef.current = resolved;
        cachedPcUrlTimestampRef.current = now;
        return resolved;
      }
    }

    // Priority 4: Direct Firebase query for PC nodes (handles cold start / stale state)
    const pk = pairingKeyRef.current;
    if (pk) {
      try {
        const nodesSnap = await get(ref(database, `nodes/${pk}`));
        if (nodesSnap.exists()) {
          const nodes = nodesSnap.val();
          for (const key of Object.keys(nodes)) {
            const node = nodes[key];
            if (node.DeviceType === 'PC') {
              const urls = getDeviceUrls(node);
              for (const url of urls) {
                try {
                  const res = await fetchWithTimeout(`${url}/api/health`, { headers: { 'X-Advance-Client': 'MobileCompanion' } }, 2500);
                  if (res.ok) {
                    cachedPcUrlRef.current = url;
                    cachedPcUrlTimestampRef.current = now;
                    return url;
                  }
                } catch {}
              }
            }
          }
        }
      } catch {}
    }

    // Priority 5: manual IP from Settings (legacy fallback)
    const raw = pcLocalIp?.trim();
    if (raw) {
      const fallback = raw.startsWith('http') ? raw.replace(/\/$/, '') : `http://${raw.includes(':') ? raw : raw + ':8999'}`;
      return fallback;
    }
    return '';
  };

  // ─── Overlay Sync ───
  const lastNativeSyncRef = useRef<number>(0);
  useEffect(() => {
    if (Platform.OS === 'android' && AdvanceOverlay && isFloatingBallEnabled) {
      const now = Date.now();
      if (now - lastNativeSyncRef.current < 500) return;
      lastNativeSyncRef.current = now;

      const filtered = clips.filter(c => (c.IsPinned || (c.Timestamp || 0) >= localWipeTimestamp) && (!c.id || !localDeletedIds.has(c.id)) && (c.Raw || c.Title));
      const seen = new Set<string>();
      const deduped = filtered.filter(c => {
        const key = (c.Raw || c.Title || '').substring(0, 200);
        if (seen.has(key)) return false;
        seen.add(key);
        return true;
      });
      if (deduped.length > 0) {
        // Only push items NOT already in native DB
        const mapped = deduped.slice(0, 20).filter(c => {
          const overlayFp = `overlay::${(c.Raw || c.Title || '').substring(0, 100)}`;
          if (pushedToOverlayRef.current.has(overlayFp)) return false;
          pushedToOverlayRef.current.add(overlayFp);
          return true;
        }).map(c => {
          let rawData = c.Raw;
          if (c.Type === 'Pdf' || c.Type === 'Document' || c.Type === 'Archive') {
            const safeName = c.Title.replace(/[^a-zA-Z0-9.-]/g, '_');
            rawData = DOWNLOAD_BASE + safeName;
          }
          return {
            Title: c.Title, Raw: rawData || '', Type: c.Type || 'Text',
            SourceDeviceName: c.SourceDeviceName || 'Cloud', Timestamp: c.Timestamp,
            DownloadUrl: c.Raw?.startsWith?.('http') ? c.Raw : '',
          };
        });
        if (mapped.length > 0) {
          try { AdvanceOverlay.syncNativeDB(JSON.stringify(mapped)); } catch(e) {}
        }
      }
      // Cap overlay tracker to prevent unbounded growth
      if (pushedToOverlayRef.current.size > 500) pushedToOverlayRef.current.clear();
    }
  }, [clips, isFloatingBallEnabled, localWipeTimestamp, localDeletedIds]);

  // ─── Bidirectional Overlay Sync ───
  useEffect(() => {
    if (Platform.OS !== 'android' || !AdvanceOverlay || !isFloatingBallEnabled || !deviceName) return;
    // Immediately configure overlay with PC URL for seamless sync
    (async () => {
      try {
        const targetUrl = await getCachedPcUrl();
        if (targetUrl) AdvanceOverlay.setPcUrl(targetUrl);
        if (deviceName) AdvanceOverlay.setDeviceName(deviceName);
      } catch {}
    })();
    const pollInterval = setInterval(async () => {
      try {
        const copiedText = await AdvanceOverlay.getLastCopiedFromOverlay();
        if (copiedText && copiedText.trim().length > 0) {
          // Fingerprint to prevent echo back from Firebase
          sentContentFingerprintsRef.current.add(copiedText.substring(0, 200));
          const newItem: ClipItem = {
            Title: copiedText.substring(0, 80), Type: 'Text', Raw: copiedText,
            Time: new Date().toLocaleString(), SourceDeviceName: deviceName,
            SourceDeviceType: 'Mobile', Timestamp: Date.now(),
          };
          setClips(prev => [newItem, ...prev]);
          if (isGlobalSyncEnabled) {
            try { if (pairingKeyRef.current) { const clipRef = push(ref(database, clipboardPath())); await set(clipRef, newItem); } } catch(e) {}
          }
        }
      } catch(e) {}
    }, 1500);
    return () => clearInterval(pollInterval);
  }, [isFloatingBallEnabled, deviceName, isGlobalSyncEnabled]);

  // ─── Device Discovery ───
  const [activeDevices, setActiveDevices] = useState<any[]>([]);
  const activeDevicesRef = useRef<any[]>([]);
  // Keep ref in sync with state so interval callbacks never use stale data
  useEffect(() => { activeDevicesRef.current = activeDevices; }, [activeDevices]);

  // ─── Screenshot Detection ───
  // SINGLE source of truth: handled by handleForegroundMediaCheck + pollAndSyncScreenshot in the main useEffect below.
  // This avoids duplicate detectors that cause infinite loops.
  const lastScreenshotTsRef = useRef<number>(Date.now());
  // Local screenshots are stored in a ref so Firebase listener can merge them into the feed
  const localScreenshotsRef = useRef<ClipItem[]>([]);

  // ─── UI State ───
  const [isRefreshing, setIsRefreshing] = useState(false);
  const [inputText, setInputText] = useState('');
  const [isSending, setIsSending] = useState(false);
  const [lastCopiedText, setLastCopiedText] = useState('');
  const [setupName, setSetupName] = useState('');
  const [isTargetModalVisible, setIsTargetModalVisible] = useState(false);
  const [pendingUploadPayload, setPendingUploadPayload] = useState<any>(null);
  const [downloadedItems, setDownloadedItems] = useState<Set<string>>(new Set());
  const [downloadProgress, setDownloadProgress] = useState<{[key: string]: number}>({});
  const [incomingTransferProgress, setIncomingTransferProgress] = useState<{[key: string]: number}>({});
  const [isCameraOptionsVisible, setIsCameraOptionsVisible] = useState(false);
  const [isQRScannerActive, setIsQRScannerActive] = useState(false);
  const [cameraPermission, requestCameraPermission] = useCameraPermissions();
  const [expandedImage, setExpandedImage] = useState<string | null>(null);
  const [lastScannedImageId, setLastScannedImageId] = useState<string | null>(null);
  const [latestIngestedId, setLatestIngestedId] = useState<string | null>(null);
  const [activeOptionsId, setActiveOptionsId] = useState<string | null>(null);
  const [isMultiSelectMode, setIsMultiSelectMode] = useState(false);
  const [selectedItemIds, setSelectedItemIds] = useState<Set<string>>(new Set());
  const [isMergeModalVisible, setIsMergeModalVisible] = useState(false);
  const [mergeQueue, setMergeQueue] = useState<ClipItem[]>([]);
  const [isForceSyncModalVisible, setIsForceSyncModalVisible] = useState(false);
  const [forceSyncDevices, setForceSyncDevices] = useState<any[]>([]);
  const [isConnectModalVisible, setIsConnectModalVisible] = useState(false);
  const [pairingCodeInput, setPairingCodeInput] = useState('');
  const [myPairingCode, setMyPairingCode] = useState<string | null>(null);
  const [isPairing, setIsPairing] = useState(false);
  const [pairedPcName, setPairedPcName] = useState<string | null>(null);
  // ── PDF Page Editor ──
  const [pageEditorVisible, setPageEditorVisible] = useState(false);
  const [pageEditorUri, setPageEditorUri] = useState('');
  const [pageEditorTitle, setPageEditorTitle] = useState('');

  // ─── Persistence ───
  useEffect(() => {
    AsyncStorage.getItem('localWipeTimestamp').then(val => {
      if (val) { setLocalWipeTimestamp(parseInt(val)); }
      else { setLocalWipeTimestamp(0); AsyncStorage.setItem('localWipeTimestamp', '0'); }
    });
    AsyncStorage.getItem('localDeletedIds').then(val => {
      if (val) { try { const arr = JSON.parse(val); setLocalDeletedIds(new Set(arr.slice(-500))); } catch(e) {} }
    });
  }, []);

  // ─── Peer Relay ───
  useEffect(() => {
    if (!deviceName) return;
    const peerRef = query(ref(database, `peer_transfers/${deviceName}`));
    const unsubscribePeer = onValue(peerRef, async (snapshot) => {
      if (snapshot.exists() && Platform.OS !== 'web') {
        const data = snapshot.val();
        const updates: any = {};
        for (const key of Object.keys(data)) {
          const batch = data[key];
          if (batch.urls && Array.isArray(batch.urls)) {
            ToastAndroid.show(`Incoming batch transfer from ${batch.sender}...`, ToastAndroid.LONG);
            try {
              const perm = await MediaLibrary.requestPermissionsAsync();
              if (perm.status === 'granted') {
                await Promise.all(batch.urls.map(async (url: string, idx: number) => {
                  const localUri = `${SYNC_CACHE_BASE}relayed_${Date.now()}_${idx}.jpg`;
                  const dl = await FileSystem.downloadAsync(url, localUri, { headers: { 'X-Advance-Client': 'MobileCompanion' } });
                  const asset = await MediaLibrary.createAssetAsync(dl.uri);
                  await MediaLibrary.createAlbumAsync("FlyShelf Extractions", asset, false);
                }));
                ToastAndroid.show("Extraction successful: Saved to Native Gallery ✅", ToastAndroid.LONG);
              }
            } catch (e) { ToastAndroid.show("Failed to relay items to Gallery.", ToastAndroid.SHORT); }
            updates[key] = null;
          }
        }
        if (Object.keys(updates).length > 0) await update(ref(database, `peer_transfers/${deviceName}`), updates);
      }
    });
    return () => unsubscribePeer();
  }, [deviceName]);

  // Helper: wrap getMediaUrl with current state
  const getMediaUrlForItem = (item: any) => getMediaUrl(item, activeDevices, pcLocalIp);

  // ─── Fetch Local Clips ───
  const fetchLocalClips = async () => {
    setIsRefreshing(true);
    const targetUrl = await getCachedPcUrl();
    try {
      const response = await fetchWithTimeout(`${targetUrl}/api/sync`, { headers: { 'X-Advance-Client': 'MobileCompanion' } }, 2500);
      if (response.ok) {
        const data: any[] = await response.json();
        const enriched = data.map(item => {
          if (item.Type === 'Image' || item.Type === 'ImageLink' || item.Type === 'QRCode') {
            return { ...item,
              PreviewUrl: item.PreviewUrl?.startsWith('/') ? `${targetUrl}${item.PreviewUrl}` : item.PreviewUrl,
              DownloadUrl: item.DownloadUrl?.startsWith('/') ? `${targetUrl}${item.DownloadUrl}` : item.DownloadUrl,
              Raw: item.Raw?.startsWith('/') ? `${targetUrl}${item.Raw}` : item.Raw,
            };
          }
          return item;
        });
        setClips(enriched);
      }
    } catch (error) { cachedPcUrlRef.current = null; }
    setIsRefreshing(false);
  };

  // ─── Firebase Listeners ───
  useEffect(() => {
    // Only listen to Firebase when global sync is enabled
    if (!isGlobalSyncEnabled) {
      setClips([]);
      return;
    }
    const pk = pairingKeyRef.current;
    if (!pk) { setClips([]); return; }
    const clipsRef = query(ref(database, `clipboard/${pk}`), orderByChild('Timestamp'), limitToLast(5));
    const unsubscribeFeed = onValue(clipsRef, (snapshot) => {
      if (snapshot.exists()) {
        const data = snapshot.val();
        const allParsed: ClipItem[] = Object.keys(data).map(k => ({ id: k, ...data[k] } as ClipItem)).reverse();
        // Filter out items sent by THIS device to prevent echo loops
        const myName = deviceName || '';
        const parsed = allParsed.filter(c => {
          // Check 1: If this device sent it (by name match), filter it out
          if (c.SourceDeviceType === 'Mobile' && myName && c.SourceDeviceName === myName) {
            syncLog('FIREBASE', `Filtered own item: ${(c.Title || '').substring(0, 40)}`);
            return false;
          }
          // Check 2: If the raw content matches something we recently sent, it's an echo
          const rawFp = (c.Raw || '').substring(0, 200);
          if (rawFp && sentContentFingerprintsRef.current.has(rawFp)) {
            syncLog('FIREBASE', `Filtered echo (fingerprint match): ${(c.Title || '').substring(0, 40)}`);
            return false;
          }
          return true;
        });
        syncLog('FIREBASE', `Feed: ${allParsed.length} total, ${parsed.length} after self-filter`);
        const now = Date.now();
        recentSyncFingerprintsRef.current.forEach((ts, fp) => { if (now - ts > 30_000) recentSyncFingerprintsRef.current.delete(fp); });

        // Push text/url items to floating ball overlay
        if (Platform.OS === 'android' && AdvanceOverlay) {
          parsed.slice(0, 5).forEach((c: any) => {
            const fp = `${c.Type}::${(c.Raw || '').substring(0, 150)}`;
            if (recentSyncFingerprintsRef.current.has(fp)) return;
            if ((c.Type === 'Text' || c.Type === 'Url' || c.Type === 'Pdf' || c.Type === 'Document') && c.Raw) {
              let rawData = c.Raw;
              if (c.Type === 'Pdf' || c.Type === 'Document') { rawData = DOWNLOAD_BASE + c.Title.replace(/[^a-zA-Z0-9.-]/g, '_'); }
              AdvanceOverlay.pushClipToNativeDB(rawData, c.SourceDeviceName || 'Cloud');
              recentSyncFingerprintsRef.current.set(fp, Date.now());
            }
          });
        }

        // ─── Process ALL image/file items from Firebase ───
        // Download images locally so they render reliably, then set clips ONCE with enriched data
        if (Platform.OS === 'android') {
          const imageItems = parsed.filter(c =>
            (c.Type === 'Image' || c.Type === 'ImageLink') && c.Raw?.startsWith('http')
          );
          const fileItems = parsed.filter(c =>
            c.Raw?.startsWith('http') && ['Pdf', 'Document', 'File', 'Video', 'Audio', 'Archive', 'Presentation'].includes(c.Type || '')
          );

          if (imageItems.length > 0 || fileItems.length > 0) {
            // Set clips immediately with Firebase data (images will show loading spinners via CachedImage)
            setClips(parsed);

            // Background: download all images and update clips with local URIs
            (async () => {
              const updatedItems: Record<string, { Raw: string; CachedUri: string }> = {};

              // Download all images in parallel — prefer LAN over Cloudflare
              await Promise.all(imageItems.map(async (imgItem) => {
                const fp = `dl::${(imgItem.Raw || '').substring(0, 100)}`;
                if (recentSyncFingerprintsRef.current.has(fp)) return;
                // DON'T record fingerprint yet — only after successful download
                try {
                  await FileSystem.makeDirectoryAsync(SYNC_CACHE_BASE, { intermediates: true }).catch(() => {});
                  const localUri = `${SYNC_CACHE_BASE}fb_img_${imgItem.id || Date.now()}.png`;
                  // Check if already cached
                  const existing = await FileSystem.getInfoAsync(localUri);
                  if (existing.exists && (existing as any).size > 100) {
                    updatedItems[imgItem.id!] = { Raw: localUri, CachedUri: localUri };
                    return;
                  }

                  // Build download URLs: LAN first, then Cloudflare original
                  const urls: string[] = [];
                  const rawUrl = imgItem.Raw || '';

                  // Try to build a LAN URL from the cached PC URL
                  if (rawUrl.includes('?path=')) {
                    const pathPart = rawUrl.substring(rawUrl.indexOf('?path='));
                    // Get LAN URL from cached PC URL or active devices
                    try {
                      const cachedUrl = await getCachedPcUrl();
                      if (cachedUrl && !cachedUrl.includes('trycloudflare.com') && !cachedUrl.includes('localhost')) {
                        urls.push(`${cachedUrl}/download${pathPart}`);
                      }
                    } catch {}
                    // Also try any PC device's local IP from the Url field
                    const pcDev = activeDevicesRef.current.find((d: any) => d.DeviceType === 'PC');
                    if (pcDev?.Url) {
                      pcDev.Url.split(',').forEach((u: string) => {
                        const cleaned = u.trim().replace(/\/$/, '');
                        if (cleaned.startsWith('http') && !cleaned.includes('trycloudflare.com') && !urls.includes(`${cleaned}/download${pathPart}`)) {
                          urls.push(`${cleaned}/download${pathPart}`);
                        }
                      });
                    }
                  }
                  urls.push(rawUrl); // Original Cloudflare URL as fallback

                  let downloaded = false;
                  for (const dlUrl of urls) {
                    if (downloaded) break;
                    try {
                      const { uri, status } = await FileSystem.downloadAsync(dlUrl, localUri, {
                        headers: { 'X-Advance-Client': 'MobileCompanion' }
                      });
                      if (status === 200) {
                        const info = await FileSystem.getInfoAsync(uri);
                        if (info.exists && (info as any).size > 100) {
                          updatedItems[imgItem.id!] = { Raw: uri, CachedUri: uri };
                          downloaded = true;
                          recentSyncFingerprintsRef.current.set(fp, Date.now()); // Record fingerprint AFTER success
                          // Copy to clipboard (only the most recent image)
                          if (imgItem === imageItems[0]) {
                            try {
                              const b64 = await FileSystem.readAsStringAsync(uri, { encoding: FileSystem.EncodingType.Base64 });
                              await Clipboard.setImageAsync(b64);
                            } catch {}
                          }
                          // Push to floating ball overlay
                          if (AdvanceOverlay && isFloatingBallEnabled) {
                            try { AdvanceOverlay.pushClipToNativeDB(uri, imgItem.SourceDeviceName || 'PC'); } catch {}
                          }
                        }
                      }
                    } catch {}
                    // Delete failed download before retrying with next URL
                    if (!downloaded) {
                      try { await FileSystem.deleteAsync(localUri, { idempotent: true }); } catch {}
                    }
                  }
                } catch {}
              }));

              // Auto-download latest file (non-image)
              if (fileItems.length > 0) {
                const latestFile = fileItems[0];
                const fp = `dl::${(latestFile.Raw || '').substring(0, 100)}`;
                if (!recentSyncFingerprintsRef.current.has(fp)) {
                  recentSyncFingerprintsRef.current.set(fp, Date.now());
                  try {
                    const subfolder = latestFile.Type === 'Pdf' ? 'PDFs' : latestFile.Type === 'Video' ? 'Videos' : 'Documents';
                    const safeName = (latestFile.Title || `file_${Date.now()}`).replace(/[^a-zA-Z0-9._-]/g, '_');
                    const destPath = await getDownloadPath(subfolder, safeName);
                    const existing = await FileSystem.getInfoAsync(destPath);
                    if (!existing.exists) {
                      ToastAndroid.show(`⬇️ Downloading ${latestFile.Title}...`, ToastAndroid.SHORT);
                      const dl = await FileSystem.downloadAsync(latestFile.Raw!, destPath, { headers: { 'X-Advance-Client': 'MobileCompanion' } });
                      if (dl.status === 200) ToastAndroid.show(`✅ ${latestFile.Title} saved`, ToastAndroid.SHORT);
                    }
                  } catch {}
                }
              }

              // If any images were downloaded, update clips with local URIs
              if (Object.keys(updatedItems).length > 0) {
                setClips(prev => prev.map(c => {
                  if (c.id && updatedItems[c.id]) {
                    return { ...c, Raw: updatedItems[c.id].Raw, CachedUri: updatedItems[c.id].CachedUri };
                  }
                  return c;
                }));
                if (imageItems.length > 0) {
                  ToastAndroid.show(`🖼️ ${Object.keys(updatedItems).length} image(s) synced from PC`, ToastAndroid.SHORT);
                }
              }
            })();
          } else {
            // No image/file items — just set clips directly, merging local screenshots
            const merged = [...localScreenshotsRef.current.filter(ls => !parsed.some(p => p.Title === ls.Title)), ...parsed];
            setClips(merged);
          }
        } else {
          // Merge local screenshots even when no Firebase image items
          const merged = [...localScreenshotsRef.current.filter(ls => !parsed.some(p => p.Title === ls.Title)), ...parsed];
          setClips(merged);
        }
      } else {
        // No Firebase data — show only local screenshots
        setClips(localScreenshotsRef.current);
      }
    });

    // pk already declared above at line 340
    if (!pk) { setActiveDevices([]); return; }
    const nodesRef = query(ref(database, `active_devices/${pk}`));
    const unsubscribeNodes = onValue(nodesRef, async (snapshot) => {
      let rawDevices: any[] = [];
      if (snapshot.exists()) {
        const data = snapshot.val();
        const now = Date.now();
        rawDevices = Object.keys(data).map(k => ({ ...data[k], _key: k })).filter(d => d.IsOnline && d.Timestamp && (now - d.Timestamp) < 300_000);
      }
      // Probe LAN reachability for each PC device from Firebase
      for (let i = 0; i < rawDevices.length; i++) {
        const dev = rawDevices[i];
        if (dev.DeviceType === 'PC' && dev.LocalIp && !dev._lanVerified) {
          try {
            const lanIp = dev.LocalIp.trim();
            const lanUrl = lanIp.startsWith('http') ? lanIp.replace(/\/$/, '') : `http://${lanIp.includes(':') ? lanIp : lanIp + ':8999'}`;
            const res = await fetch(`${lanUrl}/api/health`, { method: 'GET', headers: { 'X-Advance-Client': 'MobileCompanion' }, signal: AbortSignal.timeout(1500) });
            if (res.ok) {
              rawDevices[i] = { ...dev, _lanVerified: true, _lanUrl: lanUrl };
            }
          } catch {}
        }
      }

      // If no PC found in Firebase at all, probe manual IP from Settings as fallback
      const hasPc = rawDevices.some(d => d.DeviceType === 'PC');
      if (!hasPc && pcLocalIp) {
        try {
          const raw = pcLocalIp.trim();
          const probeUrl = raw.startsWith('http') ? raw.replace(/\/$/, '') : `http://${raw.includes(':') ? raw : raw.split(':')[0] + ':8999'}`;
          const res = await fetch(`${probeUrl}/api/health`, { method: 'GET', headers: { 'X-Advance-Client': 'MobileCompanion' }, signal: AbortSignal.timeout(2000) });
          if (res.ok) rawDevices.push({ DeviceName: 'PC (LAN)', DeviceType: 'PC', IsOnline: true, Url: probeUrl, LocalIp: probeUrl, _key: 'local_direct', _lanVerified: true, _lanUrl: probeUrl, Timestamp: Date.now() });
        } catch {}
      } else if (hasPc && pcLocalIp) {
        // Also check if the manual IP setting matches a different PC
        const manualIp = pcLocalIp.trim();
        const manualUrl = manualIp.startsWith('http') ? manualIp.replace(/\/$/, '') : `http://${manualIp.includes(':') ? manualIp : manualIp + ':8999'}`;
        const existingLan = rawDevices.some(d => d._lanUrl === manualUrl);
        if (!existingLan) {
          try {
            const res = await fetch(`${manualUrl}/api/health`, { method: 'GET', headers: { 'X-Advance-Client': 'MobileCompanion' }, signal: AbortSignal.timeout(1500) });
            if (res.ok) {
              // Update the first PC device with this LAN URL
              const pcIdx = rawDevices.findIndex(d => d.DeviceType === 'PC');
              if (pcIdx >= 0) rawDevices[pcIdx] = { ...rawDevices[pcIdx], _lanVerified: true, _lanUrl: manualUrl, LocalIp: manualUrl };
            }
          } catch {}
        }
      }
      setActiveDevices(rawDevices);
    });

    return () => { unsubscribeFeed(); unsubscribeNodes(); };
  }, [isGlobalSyncEnabled, contextPairingKey]);

  // ─── Local PC Polling ───
  useEffect(() => {
    const pollFn = async () => {
      const targetUrl = await getCachedPcUrl();
      if (Platform.OS === 'android' && AdvanceOverlay && targetUrl) {
        try { AdvanceOverlay.setPcUrl(targetUrl); } catch(e) {}
        try { if (deviceName) AdvanceOverlay.setDeviceName(deviceName); } catch(e) {}
      }
      try {
        const timeout = targetUrl.includes('trycloudflare.com') ? 5000 : 2000;
        const response = await fetchWithTimeout(`${targetUrl}/api/sync`, { headers: { 'X-Advance-Client': 'MobileCompanion' } }, timeout);
        if (response.ok) {
          const data = await response.json();
          if (data && data.length > 0) {
            const latest = data[0];
            const contentKey = `${latest.Type}_${latest.Title}_${latest.Timestamp}`;
            if (contentKey !== lastSyncedContentRef.current) {
              lastSyncedContentRef.current = contentKey;
              const crossFp = `${latest.Type}::${(latest.Raw || '').substring(0, 150)}`;
              recentSyncFingerprintsRef.current.set(crossFp, Date.now());
              const rawFingerprint = (latest.Raw || '').substring(0, 200);
              const isOwnEcho = (latest.SourceDeviceName && deviceName && latest.SourceDeviceName === deviceName) || (latest.SourceDeviceType === 'Mobile') || sentContentFingerprintsRef.current.has(rawFingerprint);

              if (!isOwnEcho) {
                // Fingerprint incoming PC content so it doesn't get re-captured and sent back
                sentContentFingerprintsRef.current.add(rawFingerprint);
                syncLog('PC-POLL', `New from PC: ${latest.Type} - ${(latest.Title || '').substring(0, 50)}`);
                if (latest.Type === 'Text' || latest.Type === 'Code' || latest.Type === 'Url') {
                  const latestRaw = latest.Raw;
                  if (latestRaw) {
                    const currentContent = await Clipboard.getStringAsync();
                    if (currentContent !== latestRaw) {
                      if (Platform.OS === 'android' && AdvanceOverlay) {
                        try { AdvanceOverlay.setClipboardSuppressed(latestRaw); } catch(e) { await Clipboard.setStringAsync(latestRaw); }
                      } else { await Clipboard.setStringAsync(latestRaw); }
                      setLastCopiedText(latestRaw);
                      lastCopiedRef.current = latestRaw;
                      if (Platform.OS === 'android') ToastAndroid.show(`📋 ${latestRaw.substring(0, 40)}...`, ToastAndroid.SHORT);
                    }
                  }
                } else if (latest.Type === 'Image' || latest.Type === 'ImageLink' || latest.Type === 'QRCode') {
                  // Only copy image to clipboard here — the Firebase listener handles the feed entry
                  // This polling path is faster (LAN) so clipboard gets updated quickly
                  try {
                    let mediaUrl = '';
                    if (latest.DownloadUrl?.startsWith('/')) mediaUrl = `${targetUrl}${latest.DownloadUrl}`;
                    else if (latest.PreviewUrl?.startsWith('/')) mediaUrl = `${targetUrl}${latest.PreviewUrl}`;
                    else if (latest.Raw?.startsWith('http')) mediaUrl = latest.Raw;
                    if (mediaUrl) {
                      await FileSystem.makeDirectoryAsync(SYNC_CACHE_BASE, { intermediates: true }).catch(() => {});
                      const localUri = `${SYNC_CACHE_BASE}clip_sync_${Date.now()}.png`;
                      const { uri, status } = await FileSystem.downloadAsync(mediaUrl, localUri, { headers: { 'X-Advance-Client': 'MobileCompanion' } });
                      if (status === 200) {
                        const b64 = await FileSystem.readAsStringAsync(uri, { encoding: (FileSystem as any).EncodingType.Base64 });
                        await Clipboard.setImageAsync(b64);
                        syncLog('PC-POLL', `Image copied to clipboard via ${targetUrl.includes('trycloudflare') ? 'Cloud' : 'LAN'}`);
                        if (Platform.OS === 'android') ToastAndroid.show(`🖼️ Screenshot copied from PC!`, ToastAndroid.SHORT);
                      }
                    }
                  } catch (imgErr) {}
                } else if (['Pdf', 'Document', 'File', 'Video', 'Audio', 'Archive', 'Presentation'].includes(latest.Type)) {
                  try {
                    let fileUrl = '';
                    if (latest.DownloadUrl?.startsWith('/')) fileUrl = `${targetUrl}${latest.DownloadUrl}`;
                    else if (latest.Raw?.startsWith('http')) fileUrl = latest.Raw;
                    else if (latest.DownloadUrl?.startsWith('http')) fileUrl = latest.DownloadUrl;
                    else if (latest.Raw?.startsWith('/')) fileUrl = `${targetUrl}${latest.Raw}`;
                    if (fileUrl) {
                      const subfolder = latest.Type === 'Pdf' ? 'PDFs' : latest.Type === 'Video' ? 'Videos' : latest.Type === 'Audio' ? 'Audio' : 'Documents';
                      const safeName = (latest.Title || `file_${Date.now()}`).replace(/[^a-zA-Z0-9._-]/g, '_');
                      const destPath = await getDownloadPath(subfolder, safeName);
                      const existing = await FileSystem.getInfoAsync(destPath);
                      if (!existing.exists) {
                        if (Platform.OS === 'android') ToastAndroid.show(`⬇️ Downloading ${latest.Title}...`, ToastAndroid.SHORT);
                        const dl = await FileSystem.downloadAsync(fileUrl, destPath, { headers: { 'X-Advance-Client': 'MobileCompanion' } });
                        if (dl.status === 200) {
                          setClips(prev => prev.map(c => c.id === latest.id ? { ...c, CachedUri: destPath } : c));
                          setDownloadedItems(prev => { const n = new Set(prev); n.add(latest.id || latest.Title); return n; });
                          if (Platform.OS === 'android') ToastAndroid.show(`✅ ${latest.Title} saved`, ToastAndroid.SHORT);
                        }
                      } else { setDownloadedItems(prev => { const n = new Set(prev); n.add(latest.id || latest.Title); return n; }); }
                    }
                  } catch (dlErr) { if (Platform.OS === 'android') ToastAndroid.show(`📁 ${latest.Title} — tap to download`, ToastAndroid.SHORT); }
                }
                if (Platform.OS === 'android' && AdvanceOverlay && isFloatingBallEnabled) {
                  try {
                    if (latest.Type === 'Image' || latest.Type === 'ImageLink' || latest.Type === 'QRCode') {
                      const imgRaw = latest.PreviewUrl?.startsWith('/') ? `${targetUrl}${latest.PreviewUrl}` : latest.DownloadUrl?.startsWith('/') ? `${targetUrl}${latest.DownloadUrl}` : latest.Raw?.startsWith('http') ? latest.Raw : '';
                      if (imgRaw) AdvanceOverlay.pushClipToNativeDB(imgRaw, 'PC');
                    } else {
                      const rawForOverlay = latest.Raw || latest.Title || '';
                      if (rawForOverlay) AdvanceOverlay.pushClipToNativeDB(rawForOverlay, 'PC');
                    }
                  } catch(e) {}
                }
              }
            }
          }
          setClips(current => {
            const merged = [...current];
            let changed = false;
            data.forEach((localItem: any) => {
              if (!merged.find(m => m.id === localItem.id || (m.Title === localItem.Title && m.Raw === localItem.Raw))) {
                merged.push(localItem); changed = true;
              }
            });
            return changed ? merged.sort((a,b) => (b.Timestamp || 0) - (a.Timestamp || 0)) : current;
          });
        }
      } catch (e) { cachedPcUrlRef.current = null; }
    };
    // Adaptive poll interval: fast on LAN, slower on Cloud, very slow if no PC
    const getInterval = () => {
      const url = cachedPcUrlRef.current || '';
      if (!url) return 8000; // No PC found
      if (url.includes('trycloudflare')) return 5000; // Cloud
      return 2000; // LAN
    };
    // Initial poll
    pollFn();
    const interval = setInterval(pollFn, getInterval());
    return () => clearInterval(interval);
  }, [isGlobalSyncEnabled, activeDevices, pcLocalIp]);

  // ─── Device Self-Registration ───
  useEffect(() => {
    if (!deviceName) return;
    const myDeviceId = `Mobile_${deviceName.replace(/[^a-zA-Z0-9_]/g, '_')}`;
    const pk = pairingKeyRef.current;
    if (!pk) return;
    const registerSelf = async () => {
      try { await set(ref(database, `active_devices/${pk}/${myDeviceId}`), { DeviceId: myDeviceId, DeviceName: deviceName, DeviceType: 'Mobile', IsOnline: true, Timestamp: Date.now() }); } catch(e) {}
    };
    registerSelf();
    const heartbeat = setInterval(registerSelf, 30000);
    return () => { clearInterval(heartbeat); if (!isFloatingBallEnabled) set(ref(database, `active_devices/${pk}/${myDeviceId}/IsOnline`), false).catch(() => {}); };
  }, [deviceName, isFloatingBallEnabled]);

  // ─── Periodic fingerprint cleanup (every 60s) ───
  useEffect(() => {
    const cleanup = setInterval(() => {
      // Clean sentContentFingerprintsRef — cap at 200 entries
      if (sentContentFingerprintsRef.current.size > 200) {
        sentContentFingerprintsRef.current.clear();
        syncLog('CLEANUP', 'Cleared sentContentFingerprints (exceeded 200)');
      }
      // Clean recentSyncFingerprintsRef — remove entries older than 60s
      const now = Date.now();
      recentSyncFingerprintsRef.current.forEach((ts, fp) => {
        if (now - ts > 60_000) recentSyncFingerprintsRef.current.delete(fp);
      });
    }, 60_000);
    return () => clearInterval(cleanup);
  }, []);

  // ─── Clear All ───
  const clearAllClips = async () => {
    const executeWipe = async () => {
      try {
        const now = Date.now();
        setLocalWipeTimestamp(now);
        AsyncStorage.setItem('localWipeTimestamp', now.toString()).catch(() => {});
        if (isGlobalSyncEnabled) {
          const updates: any = {};
          clips.forEach(item => { if (!item.IsPinned) updates[item.id!] = null; });
          if (Object.keys(updates).length > 0 && pairingKeyRef.current) await update(ref(database, clipboardPath()), updates);
        }
        Platform.OS === 'android' ? ToastAndroid.show(`Clean slate natively.`, ToastAndroid.SHORT) : alert(`Wiped visually & globally.`);
      } catch(e) {}
    };
    if (Platform.OS === 'web') { if (window.confirm("Delete all unpinned items?")) await executeWipe(); return; }
    Alert.alert("Clear Entire Clipboard", "Delete all unpinned items from the Global Mesh?", [{ text: "Cancel", style: "cancel" }, { text: "Delete All", style: "destructive", onPress: executeWipe }]);
  };

  // ─── Clipboard & Media Foreground Checks ───
  const lastCopiedRef = React.useRef(lastCopiedText);
  useEffect(() => { lastCopiedRef.current = lastCopiedText; }, [lastCopiedText]);

  const handleForegroundClipboardCheck = async () => {
    if (Platform.OS === 'web') return;
    try {
      const hasText = await Clipboard.hasStringAsync();
      if (hasText) {
        const text = await Clipboard.getStringAsync();
        // NEVER send flyshelf:// scheme strings — these are internal markers
        if (text && text.startsWith('flyshelf://')) return;
        if (text && text !== lastCopiedRef.current) {
          lastCopiedRef.current = text; // Set BEFORE transmit to prevent re-entry
          setLastCopiedText(text);
          await transmitTextSecurely(text);
        }
      }
    } catch(e) {}
  };

  // ─── Screenshot Poller: polls native ScreenshotObserver for new screenshots ───
  const lastSyncedScreenshotRef = useRef<string>('');
  const pollAndSyncScreenshot = async () => {
    if (Platform.OS !== 'android' || !AdvanceOverlay) return;
    try {
      const result = await AdvanceOverlay.getLatestScreenshot();
      const screenshotPath = typeof result === 'string' ? result : result?.path;
      if (screenshotPath && screenshotPath !== lastSyncedScreenshotRef.current) {
        // Check if handleForegroundMediaCheck already handled this
        const fileName = screenshotPath.split('/').pop() || '';
        if (sentContentFingerprintsRef.current.has(`screenshot::${fileName}`)) {
          syncLog('SCREENSHOT', `Already sent by MediaCheck: ${fileName}`);
          lastSyncedScreenshotRef.current = screenshotPath;
          return;
        }
        // Add fingerprint IMMEDIATELY to prevent race with handleForegroundMediaCheck
        sentContentFingerprintsRef.current.add(`screenshot::${fileName}`);
        lastSyncedScreenshotRef.current = screenshotPath;
        syncLog('SCREENSHOT', `Native detected: ${fileName}`);
        const activePc = activeDevicesRef.current.find((d: any) => d.DeviceType === 'PC');
        let targetUrl = activePc ? ((activePc._lanVerified && activePc._lanUrl) ? activePc._lanUrl : (await resolveOptimalUrl(activePc))) : await getCachedPcUrl();
        if (targetUrl) {
          const uploadUri = screenshotPath.startsWith('file://') ? screenshotPath : `file://${screenshotPath}`;
          try {
            const upRes = await FileSystem.uploadAsync(
              `${targetUrl}/api/sync_file?name=${encodeURIComponent(fileName)}&type=Image&sourceDevice=${encodeURIComponent(deviceName || 'Mobile')}`,
              uploadUri,
              { httpMethod: 'POST', uploadType: 0 as any, headers: { 'X-Original-Date': Date.now().toString(), 'X-Advance-Client': 'MobileCompanion' } }
            );
            if (upRes.status === 200) {
              syncLog('SCREENSHOT', `Sent to PC via ${targetUrl.includes('trycloudflare') ? 'Cloud' : 'LAN'}: ${fileName}`);
              ToastAndroid.show(`Screenshot synced to PC ✨`, ToastAndroid.SHORT);
            }
          } catch (e: any) { syncLog('SCREENSHOT', `Upload failed: ${e?.message}`); }
        } else {
          syncLog('SCREENSHOT', `No PC URL available`);
        }
      }
    } catch {}
  };

  const lastProcessedScreenshotRef = useRef<string>('');
  const handleForegroundMediaCheck = async () => {
    try {
      let perm = await MediaLibrary.getPermissionsAsync();
      if (perm.status !== 'granted') { perm = await MediaLibrary.requestPermissionsAsync(); if (perm.status !== 'granted') return; }
      const media = await MediaLibrary.getAssetsAsync({ first: 1, mediaType: ['photo'], sortBy: [[MediaLibrary.SortBy.creationTime, false]] });
      if (media.assets.length > 0) {
        const latest = media.assets[0];
        const isRecent = (Date.now() - latest.creationTime) < 2 * 60 * 1000;
        // ONLY detect screenshots — skip random photos/downloads
        const isScreenshot = (latest.filename || '').toLowerCase().includes('screenshot');
        if (isRecent && isScreenshot && latest.id !== lastScannedImageId) {
          // Ref-based dedup: prevents triple-fire from concurrent interval/AppState/MediaLibrary triggers
          if (lastProcessedScreenshotRef.current === latest.id) return;
          lastProcessedScreenshotRef.current = latest.id;
          // Check if pollAndSyncScreenshot already handled this
          const fp = `screenshot::${latest.filename}`;
          if (sentContentFingerprintsRef.current.has(fp)) {
            syncLog('MEDIA', `Already sent by NativePoll: ${latest.filename}`);
            setLastScannedImageId(latest.id);
            return;
          }
          setLastScannedImageId(latest.id);
          // Add fingerprint IMMEDIATELY to prevent race with pollAndSyncScreenshot
          sentContentFingerprintsRef.current.add(fp);
          setIsSending(true);
          syncLog('MEDIA', `Screenshot detected: ${latest.filename}`);
          try {
            const assetInfo = await MediaLibrary.getAssetInfoAsync(latest.id);
            const assetUri = assetInfo.localUri || assetInfo.uri;
            if (assetUri) {
              // Step 1: Create local cached copy for preview
              const safeName = (assetInfo.filename || `ss_${Date.now()}.png`).replace(/[^a-zA-Z0-9.-]/g, '_');
              await FileSystem.makeDirectoryAsync(IMAGE_CACHE_BASE, { intermediates: true }).catch(() => {});
              const localCopy = `${IMAGE_CACHE_BASE}${safeName}`;
              try { await FileSystem.copyAsync({ from: assetUri, to: localCopy }); } catch { /* use asset URI directly */ }
              const previewUri = localCopy;

              // Step 2: Create local clip entry (visible immediately in feed)
              const screenshotItem: ClipItem = {
                Title: assetInfo.filename || safeName,
                Type: 'ImageLink',
                Raw: previewUri,
                CachedUri: previewUri,
                Time: new Date().toLocaleString(),
                SourceDeviceName: deviceName || 'Phone',
                SourceDeviceType: 'Mobile',
                Timestamp: Date.now(),
              };
              // Store in ref so Firebase listener can merge it
              localScreenshotsRef.current = [screenshotItem, ...localScreenshotsRef.current].slice(0, 10);
              setClips(prev => [screenshotItem, ...prev.filter(c => c.Title !== screenshotItem.Title)]);
              syncLog('MEDIA', `Local preview created: ${safeName}`);
              // Push to floating ball overlay with image type info
              if (Platform.OS === 'android' && AdvanceOverlay && isFloatingBallEnabled) {
                try { AdvanceOverlay.pushClipToNativeDB(previewUri, deviceName || 'Phone'); } catch {}
              }

              // Step 3: Copy to clipboard
              try {
                const base64 = await FileSystem.readAsStringAsync(previewUri, { encoding: FileSystem.EncodingType.Base64 });
                await Clipboard.setImageAsync(base64);
              } catch {}

              // Step 4: Upload to PC — ONLY if native AdvanceOverlay is NOT available
              // When native is available, pollAndSyncScreenshot handles the upload
              if (!AdvanceOverlay) {
                let targetUrl = await getCachedPcUrl();
                let localSuccess = false;
                if (targetUrl) {
                  try {
                    const upRes = await FileSystem.uploadAsync(`${targetUrl}/api/sync_file?name=${encodeURIComponent(assetInfo.filename || 'screenshot.jpg')}&type=ImageLink&sourceDevice=${encodeURIComponent(deviceName || 'Mobile')}`, assetUri, {
                      httpMethod: 'POST', uploadType: 0 as any,
                      headers: { 'X-Original-Date': Date.now().toString(), 'X-Advance-Client': 'MobileCompanion' }
                    });
                    localSuccess = upRes.status === 200;
                    if (localSuccess) {
                      syncLog('MEDIA', `Sent to PC via ${targetUrl.includes('trycloudflare') ? 'Cloud' : 'LAN'}: ${assetInfo.filename}`);
                    }
                  } catch(e: any) { syncLog('MEDIA', `Upload failed: ${e?.message}`); }
                }
                if (!localSuccess) {
                  syncLog('MEDIA', `Could not reach PC`);
                  if (Platform.OS === 'android') ToastAndroid.show(`⚠️ Could not reach PC to send screenshot`, ToastAndroid.SHORT);
                } else {
                  if (Platform.OS === 'android') ToastAndroid.show(`📸 Screenshot sent to PC!`, ToastAndroid.SHORT);
                }
              } else {
                syncLog('MEDIA', `Upload delegated to native SCREENSHOT handler`);
              }
            }
          } catch(e) {}
          setIsSending(false);
        }
      }
    } catch(e) {}
  };

  useEffect(() => {
    // Always run clipboard + media checks (don't skip when floating ball is on)
    handleForegroundClipboardCheck();
    handleForegroundMediaCheck();
    const subscription = AppState.addEventListener('change', (nextAppState: AppStateStatus) => {
      if (nextAppState === 'active') { handleForegroundClipboardCheck(); handleForegroundMediaCheck(); }
    });
    // Poll for new media every 3 seconds
    let screenshotPollInterval: ReturnType<typeof setInterval> | null = null;
    if (Platform.OS !== 'web') { screenshotPollInterval = setInterval(() => handleForegroundMediaCheck(), 3000); }
    // Poll native ScreenshotObserver every 2 seconds
    let nativeScreenshotPoll: ReturnType<typeof setInterval> | null = null;
    if (Platform.OS === 'android') { nativeScreenshotPoll = setInterval(() => pollAndSyncScreenshot(), 2000); }
    let mediaSub: any = null;
    if (Platform.OS !== 'web' && typeof MediaLibrary.addListener === 'function') {
      mediaSub = MediaLibrary.addListener((event) => { if (event.hasIncrementalChanges || (event as any).insertedMedia?.length > 0) handleForegroundMediaCheck(); });
    }
    return () => { subscription.remove(); if (mediaSub) mediaSub.remove(); if (screenshotPollInterval) clearInterval(screenshotPollInterval); if (nativeScreenshotPoll) clearInterval(nativeScreenshotPoll); };
  }, [deviceName, isGlobalSyncEnabled, activeDevices]);

  // ─── Auto-Copy Incoming ───
  useEffect(() => {
    if (clips.length === 0) return;
    const latest = clips[0];
    if (latest.id !== latestIngestedId) {
      setLatestIngestedId(latest.id!);
      if (latest.SourceDeviceName !== deviceName) {
        if (Platform.OS === 'web') return;
        (async () => {
          try {
            if (latest.Type === 'Text' || latest.Type === 'Url' || latest.Type === 'Code') {
              const currentClip = await Clipboard.getStringAsync();
              if (currentClip !== latest.Raw) { await Clipboard.setStringAsync(latest.Raw); setLastCopiedText(latest.Raw); lastCopiedRef.current = latest.Raw; Platform.OS === 'android' && ToastAndroid.show("Copied Natively", ToastAndroid.SHORT); }
            } else if (latest.Type === 'Image' || latest.Type === 'ImageLink') {
              const mediaUrl = getMediaUrlForItem(latest);
              if (mediaUrl) {
                const { uri } = await FileSystem.downloadAsync(mediaUrl, SYNC_CACHE_BASE + 'clip_sync_global.png', { headers: { 'X-Advance-Client': 'MobileCompanion' } });
                const b64 = await FileSystem.readAsStringAsync(uri, { encoding: (FileSystem as any).EncodingType.Base64 });
                await Clipboard.setImageAsync(b64);
                Platform.OS === 'android' && ToastAndroid.show("Image Copied Natively", ToastAndroid.SHORT);
              }
            }
          } catch (e) {}
        })();
      }
    }
  }, [clips, deviceName, latestIngestedId]);

  // ─── Auto-Download Rich Media ───
  useEffect(() => {
    if (clips.length === 0) return;
    clips.forEach(async (item) => {
      if (!item.id || downloadedItems.has(item.id)) return;
      const autoTargetTypes = ['ImageLink', 'Image', 'Pdf', 'Document', 'Archive', 'Video', 'File', 'Presentation'];
      const mediaUrl = getMediaUrlForItem(item);
      if (autoTargetTypes.includes(item.Type) && mediaUrl.startsWith('http')) {
        try {
          if (Platform.OS === 'web') return;
          const safeName = item.Title.replace(/[^a-zA-Z0-9.-]/g, '_');
          const localUri = DOWNLOAD_BASE + safeName;
          const transferId = item.id || safeName;
          const fileInfo = await FileSystem.getInfoAsync(localUri);
          if (fileInfo.exists) { setDownloadedItems(prev => new Set(prev).add(item.id!)); setIncomingTransferProgress(p => { const n = {...p}; delete n[transferId]; return n; }); return; }
          if ((item.Title || '').toLowerCase().endsWith('.apk')) return;
          try {
            const headRes = await fetch(mediaUrl, { method: 'HEAD', headers: { 'X-Advance-Client': 'MobileCompanion' } });
            const sizeStr = headRes.headers.get('content-length');
            if (sizeStr) { const sizeBytes = parseInt(sizeStr); const isLocalRoute = !mediaUrl.includes('firebasestorage.googleapis.com'); if (!isLocalRoute && sizeBytes > 100 * 1024 * 1024) return; }
          } catch(e) {}
          setIncomingTransferProgress(p => ({...p, [transferId]: 0}));
          const resumable = FileSystem.createDownloadResumable(mediaUrl, localUri, { headers: { 'X-Advance-Client': 'MobileCompanion' } }, (dp) => { const pct = dp.totalBytesExpectedToWrite > 0 ? dp.totalBytesWritten / dp.totalBytesExpectedToWrite : 0; setIncomingTransferProgress(p => ({...p, [transferId]: pct})); });
          await resumable.downloadAsync();
          setIncomingTransferProgress(p => { const n = {...p}; delete n[transferId]; return n; });
          if (item.Type === 'ImageLink' || item.Type === 'Image') { try { const perm = await MediaLibrary.requestPermissionsAsync(); if (perm.status === 'granted') await MediaLibrary.saveToLibraryAsync(localUri); } catch (err) {} }
        } catch(e) { const transferId = item.id || (item.Title || '').replace(/[^a-zA-Z0-9.-]/g, '_'); setIncomingTransferProgress(p => { const n = {...p}; delete n[transferId]; return n; }); } finally { setDownloadedItems(prev => new Set(prev).add(item.id!)); }
      } else { setDownloadedItems(prev => new Set(prev).add(item.id!)); }
    });
  }, [clips]);

  // ─── Send Text ───
  const transmitTextSecurely = async (payloadText: string) => {
    const isDuplicate = clips.some(c => c.Raw === payloadText || c.Title === payloadText);
    if (isDuplicate) return;
    setIsSending(true);
    try {
      let finalRaw = payloadText, finalType = 'Text';
      if (payloadText.startsWith('http')) finalType = 'Url';
      else if (payloadText.includes('meet.google.com') || payloadText.includes('zoom.us') || payloadText.startsWith('www.')) { finalType = 'Url'; finalRaw = `https://${payloadText}`; }
      const targetUrl = await getCachedPcUrl();
      sentContentFingerprintsRef.current.add(finalRaw.substring(0, 200));
      if (sentContentFingerprintsRef.current.size > 100) sentContentFingerprintsRef.current = new Set(Array.from(sentContentFingerprintsRef.current).slice(-50));
      let localSuccess = false;
      try {
        const pairingKey = await AsyncStorage.getItem('pairingKey');
        const hdrs: any = { 'Content-Type': 'text/plain', 'X-Advance-Client': 'MobileCompanion', 'X-Source-Device': deviceName || 'Mobile' };
        if (pairingKey) hdrs['X-Pairing-Key'] = pairingKey;
        const response = await fetchWithTimeout(`${targetUrl}/api/sync_text`, { method: 'POST', headers: hdrs, body: finalRaw }, 1500);
        localSuccess = response.ok;
      } catch(e) { cachedPcUrlRef.current = null; }
      // Always add sent text to local clips so it appears in the feed
      const sentItem: ClipItem = {
        id: `local_${Date.now()}`,
        Title: payloadText.length > 50 ? payloadText.substring(0, 50) + '...' : payloadText,
        Type: finalType,
        Raw: finalRaw,
        Time: new Date().toLocaleTimeString(),
        Timestamp: Date.now(),
        SourceDeviceName: deviceName || 'Mobile',
        SourceDeviceType: 'Mobile',
      };
      setClips(prev => {
        const exists = prev.some(c => c.Raw === finalRaw);
        return exists ? prev : [sentItem, ...prev];
      });

      if (!localSuccess && isGlobalSyncEnabled) {
        const newRef = push(ref(database, clipboardPath()));
        set(newRef, { Title: payloadText.length > 50 ? payloadText.substring(0, 50) + '...' : payloadText, Type: finalType, Raw: finalRaw, Time: new Date().toLocaleTimeString(), Timestamp: Date.now(), SourceDeviceName: deviceName || 'Unknown Mobile', SourceDeviceType: 'Mobile' }).catch(() => {});
      }
    } catch (e) {}
    setIsSending(false);
  };

  // ─── Multi-Select ───
  const toggleSelectItem = (id: string) => { setSelectedItemIds(prev => { const u = new Set(prev); if (u.has(id)) u.delete(id); else u.add(id); return u; }); };
  const exitMultiSelect = () => { setIsMultiSelectMode(false); setSelectedItemIds(new Set()); };
  const getSelectedClips = () => clips.filter(c => (c.IsPinned || (c.Timestamp || 0) >= localWipeTimestamp) && (!c.id || !localDeletedIds.has(c.id)) && (c.Raw || c.Title)).filter(c => selectedItemIds.has(c.id || ''));

  // ─── PDF Merge ───
  const openMergeModal = () => { const selected = getSelectedClips().filter(c => c.Type === 'Pdf' || (c.Title || '').toLowerCase().endsWith('.pdf')); if (selected.length < 2) { Alert.alert('Need 2+ PDFs'); return; } setMergeQueue([...selected]); setIsMergeModalVisible(true); };
  const moveMergeItem = (fromIdx: number, toIdx: number) => { if (toIdx < 0 || toIdx >= mergeQueue.length) return; setMergeQueue(prev => { const arr = [...prev]; const [moved] = arr.splice(fromIdx, 1); arr.splice(toIdx, 0, moved); return arr; }); };
  const executePdfMerge = async () => {
    try {
      setIsMergeModalVisible(false);
      if (Platform.OS === 'android') ToastAndroid.show('Merging PDFs on device...', ToastAndroid.LONG);

      // Resolve PDF URIs (local cached or remote URLs)
      const pdfUris = mergeQueue.map(item => {
        const mUrl = getMediaUrlForItem(item);
        // Prefer local cached version
        if (item.CachedUri) return item.CachedUri;
        const safeName = (item.Title || '').replace(/[^a-zA-Z0-9.-]/g, '_');
        const localPath = DOWNLOAD_BASE + safeName;
        return mUrl || localPath;
      }).filter(u => u && u.length > 0);

      if (pdfUris.length < 2) { Alert.alert('Error', 'Could not resolve PDF files.'); return; }

      const outputPath = CONVERTED_BASE + `merged_${Date.now()}.pdf`;

      try {
        // Try local merge first (on-device, no PC needed)
        await localMergePdfs(pdfUris, outputPath);
        if (Platform.OS === 'android') ToastAndroid.show('✅ PDFs merged on device!', ToastAndroid.SHORT);
        await Sharing.shareAsync(outputPath, { mimeType: 'application/pdf', UTI: 'com.adobe.pdf', dialogTitle: 'Merged PDF' });
      } catch (localErr: any) {
        // Fallback: try PC merge
        if (Platform.OS === 'android') ToastAndroid.show('Local merge failed, trying PC...', ToastAndroid.SHORT);
        let targetUrl = `http://${pcLocalIp}`;
        const activePc = activeDevices.find((d: any) => d.DeviceType === 'PC');
        if (activePc) { const opt = await resolveOptimalUrl(activePc); if (opt) targetUrl = opt; }
        const pdfUrls = mergeQueue.map(item => getMediaUrlForItem(item)).filter(u => u.startsWith('http'));
        if (pdfUrls.length < 2) { Alert.alert('Error', `Local: ${localErr.message}\nPC: No HTTP URLs available.`); return; }
        const res = await fetchWithTimeout(`${targetUrl}/api/merge_pdfs`, { method: 'POST', headers: { 'Content-Type': 'application/json', 'X-Advance-Client': 'MobileCompanion' }, body: JSON.stringify({ urls: pdfUrls, sourceDevice: deviceName || 'Mobile' }) }, 30000);
        if (res.ok) { const body = await res.json(); if (body.downloadUrl) { const mergedUrl = body.downloadUrl.startsWith('http') ? body.downloadUrl : `${targetUrl}${body.downloadUrl}`; const localUri = CONVERTED_BASE + `merged_${Date.now()}.pdf`; await FileSystem.downloadAsync(mergedUrl, localUri, { headers: { 'X-Advance-Client': 'MobileCompanion' } }); await Sharing.shareAsync(localUri, { mimeType: 'application/pdf', UTI: 'com.adobe.pdf', dialogTitle: 'Merged PDF' }); } } else Alert.alert('Merge Failed');
      }
    } catch (e) { Alert.alert('Merge Error'); }
    exitMultiSelect();
  };

  // ─── Force Sync ───
  const openForceSyncModal = async () => {
    if (selectedItemIds.size === 0) { Alert.alert('Nothing Selected'); return; }
    try { const pk = pairingKeyRef.current; if (!pk) { setForceSyncDevices([]); setIsForceSyncModalVisible(true); return; } const { get: firebaseGet } = await import('firebase/database'); const snapshot = await firebaseGet(ref(database, `active_devices/${pk}`)); if (snapshot.exists()) { const data = snapshot.val(); setForceSyncDevices(Object.keys(data).map(k => ({ key: k, ...data[k] })).filter(d => d.DeviceName !== deviceName)); } else setForceSyncDevices([]); } catch (e) { setForceSyncDevices([]); }
    setIsForceSyncModalVisible(true);
  };
  const executeForcedSync = async (targetDeviceKeys: string[]) => {
    try {
    setIsForceSyncModalVisible(false);
    const selected = getSelectedClips();
    if (selected.length === 0) { syncLog('FORCE-SYNC', 'No items selected'); return; }
    syncLog('FORCE-SYNC', `Syncing ${selected.length} items to ${targetDeviceKeys.length} devices`);
    if (Platform.OS === 'android') ToastAndroid.show(`Force syncing ${selected.length} items...`, ToastAndroid.LONG);
    try {
      for (const deviceKey of targetDeviceKeys) { for (const item of selected) { const forcedRef = push(ref(database, `forced_sync/${deviceKey}`)); await set(forcedRef, { ...item, ForcedBy: deviceName, ForcedAt: Date.now(), SourceDeviceName: item.SourceDeviceName || deviceName }); } }
      for (const item of selected) { if (!item.id && pairingKeyRef.current) { const clipRef = push(ref(database, clipboardPath())); await set(clipRef, { ...item, Timestamp: Date.now() }); } }
      for (const deviceKey of targetDeviceKeys) {
        const dev = forceSyncDevices.find(d => d.key === deviceKey);
        if (dev?.LocalIp) { try { const url = await resolveOptimalUrl(dev); if (url) { for (const item of selected) { await fetchWithTimeout(`${url}/api/sync`, { method: 'POST', headers: { 'Content-Type': 'application/json', 'X-Advance-Client': 'MobileCompanion' }, body: JSON.stringify({ title: item.Title, content: item.Raw, type: item.Type, sourceDevice: deviceName }) }, 5000).catch(() => {}); } } } catch (e) {} }
      }
      if (Platform.OS === 'android') ToastAndroid.show('Force sync complete ✅', ToastAndroid.SHORT);
    } catch (e: any) { syncLog('FORCE-SYNC', `ERROR: ${e?.message}`); Alert.alert('Sync Error', e?.message || 'Unknown error'); }
    } catch (outerErr: any) { syncLog('FORCE-SYNC', `CRASH: ${outerErr?.message}`); Alert.alert('Error', outerErr?.message || 'Unexpected error'); }
    exitMultiSelect();
  };

  // ─── File/Camera/QR Actions ───
  const sendTextToPc = async () => { if (!inputText.trim()) return; await transmitTextSecurely(inputText); setInputText(''); };
  const pickFileAndSend = async () => {
    try {
      const result = await DocumentPicker.getDocumentAsync({ type: '*/*' });
      if (result.canceled) return;
      const file = result.assets[0];
      const ext = file.name.split('.').pop()?.toLowerCase() || '';
      let assignedType = 'Document';
      if (['apk','zip','rar'].includes(ext)) assignedType = 'Archive';
      else if (ext === 'pdf') assignedType = 'Pdf';
      else if (['mp4','avi','mkv'].includes(ext)) assignedType = 'Video';
      else if (['ppt','pptx'].includes(ext)) assignedType = 'Presentation';
      else if (['jpg','jpeg','png','gif','webp'].includes(ext)) assignedType = 'Image';
      else if (['doc','docx','txt'].includes(ext)) assignedType = 'Document';
      else assignedType = 'File';
      setPendingUploadPayload({ uri: file.uri, name: file.name, size: file.size, type: assignedType });
      // Auto-send to PC via LAN/Cloudflare if available, skip Firebase
      const pc = activeDevices.find((d: any) => d.DeviceType === 'PC');
      if (pc) {
        setPendingUploadPayload({ uri: file.uri, name: file.name, size: file.size, type: assignedType });
        executeHeavyUpload(pc);
      } else {
        setPendingUploadPayload({ uri: file.uri, name: file.name, size: file.size, type: assignedType });
        setIsTargetModalVisible(true);
      }
    } catch (err) { Alert.alert('Upload Failed'); }
  };
  const launchDirectCamera = async () => {
    setIsCameraOptionsVisible(false);
    const result = await ImagePicker.launchCameraAsync({ mediaTypes: ['images'], allowsEditing: false, quality: 0.8 });
    if (!result.canceled) {
      const file = result.assets[0];
      try { const b64 = await FileSystem.readAsStringAsync(file.uri, { encoding: (FileSystem as any).EncodingType.Base64 }); await Clipboard.setImageAsync(b64); Platform.OS === 'android' ? ToastAndroid.show("Captured & Copied", ToastAndroid.SHORT) : null; } catch (e) {}
      const payload = { uri: file.uri, name: file.fileName || `camera_${Date.now()}.jpg`, size: file.fileSize, type: 'Image' };
      const pc = activeDevices.find((d: any) => d.DeviceType === 'PC');
      setPendingUploadPayload(payload);
      if (pc) { executeHeavyUpload(pc); } else { setIsTargetModalVisible(true); }
    }
  };
  const pickImageAndSend = async () => {
    const result = await ImagePicker.launchImageLibraryAsync({ mediaTypes: ['images', 'videos'], allowsEditing: false, quality: 0.8 });
    if (!result.canceled) {
      const file = result.assets[0];
      try { if (file.type === 'image') { const b64 = await FileSystem.readAsStringAsync(file.uri, { encoding: (FileSystem as any).EncodingType.Base64 }); await Clipboard.setImageAsync(b64); } } catch (e) {}
      const payload = { uri: file.uri, name: file.fileName || `media_${Date.now()}`, size: file.fileSize, type: file.type === 'video' ? 'Video' : 'Image' };
      const pc = activeDevices.find((d: any) => d.DeviceType === 'PC');
      setPendingUploadPayload(payload);
      if (pc) { executeHeavyUpload(pc); } else { setIsTargetModalVisible(true); }
    }
  };
  const launchQRScanner = async () => { setIsConnectModalVisible(false); setIsCameraOptionsVisible(false); if (!cameraPermission?.granted) { const perm = await requestCameraPermission(); if (!perm.granted) { Alert.alert("Permission Required"); return; } } setIsQRScannerActive(true); };

  // ─── Pairing System ───
  const executePairing = async (pairInfo: { key?: string; local?: string; global?: string; pin?: string; name?: string; id?: string }) => {
    const { key, local, global: globalUrl, pin, name: pcName, id: pcId } = pairInfo;
    setIsPairing(true);
    if (Platform.OS === 'android') ToastAndroid.show(`Connecting to ${pcName || 'device'}...`, ToastAndroid.SHORT);

    const urls = [local, globalUrl].filter(u => u && u.startsWith('http')) as string[];
    let paired = false, workingUrl = '';

    for (const url of urls) {
      try {
        const res = await fetchWithTimeout(`${url}/api/pair`, {
          method: 'POST',
          headers: { 'Content-Type': 'application/json', 'X-Advance-Client': 'MobileCompanion' },
          body: JSON.stringify({
            key: key || '',
            deviceId: `Mobile_${(deviceName || 'Phone').replace(/[^a-zA-Z0-9_]/g, '_')}`,
            deviceName: deviceName || 'Phone',
            deviceType: 'Mobile',
          }),
        }, 6000);
        if (res.ok) { paired = true; workingUrl = url; break; }
      } catch {}
    }

    // ═══ ALWAYS save pairing info — the key is what matters for cloud sync ═══
    // Even if we can't reach the PC right now, the shared key enables Firebase sync.
    await AsyncStorage.multiSet([
      ['pairingKey', key || ''], ['pairedPcName', pcName || ''], ['pairedPcId', pcId || ''],
      ['pairedLocalUrl', local || ''], ['pairedGlobalUrl', globalUrl || ''],
      ['pairedPin', pin || ''],
    ]);
    pairingKeyRef.current = key || '';
    if (workingUrl) {
      cachedPcUrlRef.current = workingUrl;
      cachedPcUrlTimestampRef.current = Date.now();
    }
    setPairedPcName(pcName || 'Device');
    if (!isGlobalSyncEnabled) setGlobalSyncEnabled(true);

    // Register the remote device in the paired devices list
    const deviceType = (pairInfo as any).deviceType || 'PC';
    await addPairedDevice({
      deviceId: pcId || `${pcName}_${Date.now()}`,
      deviceName: pcName || 'Unknown Device',
      deviceType: deviceType as 'PC' | 'Mobile' | 'Browser',
      pairedAt: Date.now(),
    });

    setIsPairing(false);

    if (paired) {
      if (Platform.OS === 'android') ToastAndroid.show(`✅ Paired with ${pcName}!`, ToastAndroid.LONG);
      Alert.alert('Connected! 🎉',
        `Paired with ${pcName}.\n\nAnything you copy or drop on your PC will appear here instantly — from anywhere in the world.`,
        [{ text: 'Got it!' }]
      );
    } else {
      // Pairing key is saved — sync will work once the PC is reachable
      if (Platform.OS === 'android') ToastAndroid.show(`✅ Paired with ${pcName} (deferred)`, ToastAndroid.LONG);
      Alert.alert('Paired! 🔑',
        `Paired with ${pcName}.\n\nThe PC isn't reachable right now, but your pairing key is saved.\nClipboard sync will start automatically once FlyShelf is running.`,
        [{ text: 'OK' }]
      );
    }
  };

  const connectByCode = async (code: string) => {
    if (!code || code.trim().length !== 6) { Alert.alert('Invalid Code', 'Please enter a 6-character pairing code.'); return; }
    setIsPairing(true);
    if (Platform.OS === 'android') ToastAndroid.show('Looking up code...', ToastAndroid.SHORT);
    try {
      const res = await fetch(`https://advance-sync-default-rtdb.firebaseio.com/pairing_codes/${code.toUpperCase().trim()}.json`);
      const data = await res.json();
      if (!data) { setIsPairing(false); Alert.alert('Code Not Found', 'No device found with this code.\nMake sure the code is correct and the other device is online.'); return; }

      // Check TTL (5 min)
      if (data.timestamp && (Date.now() - data.timestamp) > 5 * 60 * 1000) {
        setIsPairing(false); Alert.alert('Code Expired', 'This code has expired. Generate a new one on the other device.'); return;
      }

      await executePairing({
        key: data.pairingKey, local: data.localUrl, global: data.globalUrl,
        pin: data.pin, name: data.deviceName, id: data.deviceId,
      });
      setIsConnectModalVisible(false);
      setPairingCodeInput('');
    } catch { setIsPairing(false); Alert.alert('Error', 'Could not connect. Check your internet.'); }
  };

  const generateMyPairingCode = async () => {
    const chars = 'ABCDEFGHJKLMNPQRSTUVWXYZ23456789';
    let code = '';
    for (let i = 0; i < 6; i++) code += chars[Math.floor(Math.random() * chars.length)];
    try {
      const myDeviceId = `Mobile_${(deviceName || 'Phone').replace(/[^a-zA-Z0-9_]/g, '_')}`;

      // Ensure we have a pairing key — generate one if this is a fresh install
      let currentKey = pairingKeyRef.current;
      if (!currentKey) {
        currentKey = await regeneratePairingKey();
        pairingKeyRef.current = currentKey;
      }

      const payload = {
        deviceId: myDeviceId,
        deviceName: deviceName || 'Phone',
        deviceType: 'Mobile',
        pairingKey: currentKey, // Use the ACTUAL pairing key, not deviceId
        localUrl: '',
        globalUrl: '',
        pin: '',
        timestamp: Date.now(),
      };
      await fetch(`https://advance-sync-default-rtdb.firebaseio.com/pairing_codes/${code}.json`, {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(payload),
      });
      setMyPairingCode(code);
      if (Platform.OS === 'android') ToastAndroid.show(`Code: ${code} (5 min) — Waiting for device...`, ToastAndroid.SHORT);

      // ── Poll for incoming connections ──
      // When the PC enters our code, it appears in active_devices with our pairing key.
      // We poll every 3s to detect this and auto-register the paired device.
      const pollForConnection = setInterval(async () => {
        try {
          const pk = pairingKeyRef.current;
          const devicesRes = await fetch(`https://advance-sync-default-rtdb.firebaseio.com/active_devices/${pk}.json`);
          const devices = await devicesRes.json();
          if (!devices) return;

          for (const key of Object.keys(devices)) {
            const dev = devices[key];
            // Look for a recently-active PC that's online
            if (dev.DeviceType === 'PC' && dev.IsOnline && dev.Timestamp && (Date.now() - dev.Timestamp) < 120000) {
              // Check if this PC is NOT already in our paired list
              const alreadyPaired = (await AsyncStorage.getItem('@pairedDevices') || '[]');
              const pairedList = JSON.parse(alreadyPaired);
              const exists = pairedList.some((d: any) => d.deviceId === key);
              if (!exists) {
                await addPairedDevice({
                  deviceId: key,
                  deviceName: dev.DeviceName || 'PC',
                  deviceType: 'PC',
                  pairedAt: Date.now(),
                });
                // Save their connection URLs for fast LAN sync
                if (dev.LocalIp) await AsyncStorage.setItem('pairedLocalUrl', dev.LocalIp.startsWith('http') ? dev.LocalIp : `http://${dev.LocalIp}`);
                if (dev.GlobalUrl) await AsyncStorage.setItem('pairedGlobalUrl', dev.GlobalUrl);
                if (dev.Url) {
                  cachedPcUrlRef.current = dev.Url;
                  cachedPcUrlTimestampRef.current = Date.now();
                }
                setPairedPcName(dev.DeviceName || 'PC');
                if (!isGlobalSyncEnabled) setGlobalSyncEnabled(true);
                if (Platform.OS === 'android') ToastAndroid.show(`✅ ${dev.DeviceName || 'PC'} connected!`, ToastAndroid.LONG);
                clearInterval(pollForConnection);
                setMyPairingCode(null);
                // Clean up the pairing code from Firebase
                try { await fetch(`https://advance-sync-default-rtdb.firebaseio.com/pairing_codes/${code}.json`, { method: 'DELETE' }); } catch {}
                break;
              }
            }
          }
        } catch {}
      }, 3000);

      // Auto-expire after 5 min
      setTimeout(async () => {
        clearInterval(pollForConnection);
        try { await fetch(`https://advance-sync-default-rtdb.firebaseio.com/pairing_codes/${code}.json`, { method: 'DELETE' }); } catch {}
        if (myPairingCode === code) setMyPairingCode(null);
      }, 5 * 60 * 1000);
    } catch { Alert.alert('Error', 'Could not generate code.'); }
  };

  const handleBarcodeScanned = async ({ data }: { data: string }) => {
    setIsQRScannerActive(false);

    // Try to parse as FlyShelf QR payload
    let qr: any = null;
    try { qr = JSON.parse(data); } catch {}

    if (qr && qr.app === 'ClipFlow') {
      // FlyShelf QR — do proper pairing
      await executePairing({ key: qr.key, local: qr.local, global: qr.global, pin: qr.pin, name: qr.name, id: qr.id });
      return;
    }

    // Not a FlyShelf QR — legacy behavior (copy text / open URL)
    await Clipboard.setStringAsync(data);
    if (Platform.OS === 'android') ToastAndroid.show('Copied QR content', ToastAndroid.SHORT);
    if (data.toLowerCase().startsWith('http://') || data.toLowerCase().startsWith('https://')) Linking.openURL(data).catch(() => {});
    setInputText(data);
  };

  // Load paired PC name on startup
  useEffect(() => {
    AsyncStorage.getItem('pairedPcName').then(name => { if (name) setPairedPcName(name); });
  }, []);

  // ─── Heavy Upload ───
  const CHUNK_SIZE = 50 * 1024 * 1024; // 50MB (under Cloudflare 100MB limit)

  const executeHeavyUpload = async (targetDeviceOrGlobal: any) => {
    try {
    if (!pendingUploadPayload) { syncLog('UPLOAD', 'No payload — skipping'); return; }
    setIsTargetModalVisible(false);
    setIsSending(true);
    const { uri: physicalPath, name, size, type } = pendingUploadPayload;
    syncLog('UPLOAD', `Starting: ${name} (${type}) size=${size || '?'}`);
    try {
      const safeName = `sync_${Date.now()}_` + name.replace(/[^a-zA-Z0-9.-]/g, '_');
      const hydratedPath = `${SYNC_CACHE_BASE}${safeName}`;
      await FileSystem.copyAsync({ from: physicalPath, to: hydratedPath });

      if (targetDeviceOrGlobal === 'Global') {
        // Send to PC via LAN/Cloudflare (no Firebase Storage)
        const pc = activeDevices.find((d: any) => d.DeviceType === 'PC');
        if (!pc) { Alert.alert('No PC Found', 'No paired PC is online. Connect a PC first.'); setIsSending(false); setPendingUploadPayload(null); return; }
        const resolved = await resolveOptimalUrl(pc);
        if (!resolved) { Alert.alert('PC Unreachable', 'Could not reach your PC. Make sure FlyShelf is running.'); setIsSending(false); setPendingUploadPayload(null); return; }
        const uploadUrl = `${resolved}/api/sync_file?name=${encodeURIComponent(name)}&type=${encodeURIComponent(type)}&sourceDevice=${encodeURIComponent(deviceName || 'Mobile')}`;
        await FileSystem.uploadAsync(uploadUrl, hydratedPath, { httpMethod: 'POST', uploadType: 0 as any, headers: { 'X-Original-Date': Date.now().toString(), 'X-Advance-Client': 'MobileCompanion' } });
      } else {
        // Direct device transfer (LAN or Cloudflare)
        const resolved = await resolveOptimalUrl(targetDeviceOrGlobal);
        if (!resolved) { Alert.alert('Device Unreachable', 'Could not connect to this device. Make sure it is online.'); setIsSending(false); setPendingUploadPayload(null); return; }

        const isCloudflare = resolved.includes('trycloudflare.com');
        const fileSize = size || 0;

        if (isCloudflare && fileSize > CHUNK_SIZE) {
          // ── Chunked upload for large files over Cloudflare ──
          if (Platform.OS === 'android') ToastAndroid.show(`📦 Chunked upload: ${Math.ceil(fileSize / CHUNK_SIZE)} chunks`, ToastAndroid.SHORT);
          const sessionId = `${Date.now()}_${Math.random().toString(36).substring(2, 10)}`;
          const totalChunks = Math.ceil(fileSize / CHUNK_SIZE);

          for (let i = 0; i < totalChunks; i++) {
            const offset = i * CHUNK_SIZE;
            const length = Math.min(CHUNK_SIZE, fileSize - offset);

            // Read chunk as base64, write to temp file
            const chunkB64 = await FileSystem.readAsStringAsync(hydratedPath, {
              encoding: FileSystem.EncodingType.Base64,
              position: offset,
              length: length,
            });
            const chunkTempUri = `${FileSystem.cacheDirectory}chunk_${sessionId}_${i}`;
            await FileSystem.writeAsStringAsync(chunkTempUri, chunkB64, { encoding: FileSystem.EncodingType.Base64 });

            // Upload chunk with retries
            let attempt = 0;
            let done = false;
            while (attempt < 3 && !done) {
              attempt++;
              try {
                const res = await FileSystem.uploadAsync(`${resolved}/api/upload_chunk`, chunkTempUri, {
                  httpMethod: 'POST',
                  uploadType: FileSystem.FileSystemUploadType.BINARY_CONTENT,
                  headers: {
                    'X-Advance-Client': 'MobileCompanion',
                    'X-Upload-Session': sessionId,
                    'X-Chunk-Index': i.toString(),
                  }
                });
                if (res.status === 200) done = true;
                else throw new Error(`Chunk ${i + 1}/${totalChunks} failed: HTTP ${res.status}`);
              } catch (e) {
                if (attempt === 3) throw e;
                await new Promise(r => setTimeout(r, 1000));
              }
            }
            try { await FileSystem.deleteAsync(chunkTempUri, { idempotent: true }); } catch {}
            if (Platform.OS === 'android') ToastAndroid.show(`📤 Chunk ${i + 1}/${totalChunks} sent`, ToastAndroid.SHORT);
          }

          // Finalize — tell PC to merge all chunks
          const finRes = await fetch(`${resolved}/api/upload_finalize`, {
            method: 'POST',
            headers: {
              'X-Advance-Client': 'MobileCompanion',
              'X-Upload-Session': sessionId,
              'X-File-Name': encodeURIComponent(name),
              'X-Original-Date': Date.now().toString(),
              'X-Total-Chunks': totalChunks.toString(),
              'X-Source-Device': encodeURIComponent(deviceName || 'Mobile'),
            }
          });
          if (!finRes.ok) throw new Error(`Finalize failed: ${finRes.status}`);
        } else {
          // ── Direct single POST (LAN or small Cloudflare files) ──
          const uploadUrl = `${resolved}/api/sync_file?name=${encodeURIComponent(name)}&type=${encodeURIComponent(type)}&sourceDevice=${encodeURIComponent(deviceName || 'Mobile')}`;
          await FileSystem.uploadAsync(uploadUrl, hydratedPath, { httpMethod: 'POST', uploadType: 0 as any, headers: { 'X-Original-Date': Date.now().toString(), 'X-Advance-Client': 'MobileCompanion' } });
        }
      }
      if (Platform.OS === 'android') ToastAndroid.show(`✅ ${name} sent!`, ToastAndroid.SHORT);
    } catch (err: any) { syncLog('UPLOAD', `FAILED: ${err?.message}`); Alert.alert('Upload Failed', err?.message || 'Unknown error'); }
    } catch (outerErr: any) { syncLog('UPLOAD', `CRASH: ${outerErr?.message}`); Alert.alert('Error', outerErr?.message || 'Unexpected error'); }
    setIsSending(false);
    setPendingUploadPayload(null);
  };

  // ─── Clip Visibility Filter ───
  const clipFilter = (c: ClipItem) => {
    const isVisible = (c.IsPinned || (c.Timestamp || 0) >= localWipeTimestamp) && (!c.id || !localDeletedIds.has(c.id)) && (c.Raw || c.Title);
    if (!isVisible) return false;
    // Show image clips even if download pending — they have a Title at minimum
    if ((c.Type === 'Image' || c.Type === 'ImageLink') && !c.Raw && !c.CachedUri && !c.Title) return false;
    if (!c.Raw && !c.Title) return false;
    return true;
  };

  // ════════════════════════════════════════════════════════
  // RENDER
  // ════════════════════════════════════════════════════════
  return (
    <LinearGradient colors={[colors.bg.base, colors.bg.baseEnd]} style={{ flex: 1 }}>
    <SafeAreaView style={[styles.container, { backgroundColor: 'transparent' }]}>
      {/* Device Name Setup Modal */}
      <Modal visible={!deviceName && deviceName === ''} animationType="fade" transparent={true}>
        <View style={styles.modalOverlay}><View style={styles.modalContent}>
          <Text style={styles.modalTitle}>Name this Device</Text>
          <Text style={styles.modalSubtitle}>Identify this device in the FlyShelf network.</Text>
          <TextInput style={styles.modalInput} value={setupName} onChangeText={setSetupName} placeholder="e.g. Galaxy S23" placeholderTextColor="#4C5361" autoFocus />
          <TouchableOpacity style={styles.modalButton} onPress={() => { if(setupName.trim()) setDeviceName(setupName.trim()); }}><Text style={styles.modalButtonText}>Get Started</Text></TouchableOpacity>
        </View></View>
      </Modal>

      {/* Target Device Selection Modal */}
      <Modal visible={isTargetModalVisible} animationType="slide" transparent={true}>
        <View style={styles.modalOverlay}><View style={styles.modalContent}>
          <Text style={styles.modalTitle}>Select Target Node</Text>
          <Text style={styles.modalSubtitle}>Where do you want to transfer this payload?</Text>
          <TouchableOpacity style={styles.targetOption} onPress={() => executeHeavyUpload('Global')}>
            <IconSymbol name="cloud.fill" size={24} color="#4A62EB" />
            <View style={{marginLeft: 12}}><Text style={{color: '#FFF', fontSize: 16, fontWeight: '600'}}>Global Cloud (Firebase)</Text><Text style={{color: '#8A8F98', fontSize: 12}}>10MB Limit. Visible to all devices.</Text></View>
          </TouchableOpacity>
          <Text style={{color: '#8A8F98', fontSize: 12, marginTop: 16, marginBottom: 8, fontWeight: '700', textTransform: 'uppercase'}}>Active Proxy Endpoints</Text>
          {activeDevices.map((device, i) => {
            const connType = getConnectionType(device, pcLocalIp);
            return (
              <TouchableOpacity key={i} style={styles.targetOption} onPress={() => executeHeavyUpload(device)}>
                <IconSymbol name={device.DeviceType === 'PC' ? 'laptopcomputer' : 'iphone'} size={24} color={connectionColors[connType]} />
                <View style={{marginLeft: 12, flex: 1}}>
                  <Text style={{color: '#FFF', fontSize: 16, fontWeight: '600'}}>{device.DeviceName}</Text>
                  <View style={{flexDirection: 'row', alignItems: 'center', gap: 6, marginTop: 2}}>
                    <View style={{backgroundColor: connectionColors[connType] + '22', borderRadius: 4, paddingHorizontal: 6, paddingVertical: 1}}><Text style={{color: connectionColors[connType], fontSize: 10, fontWeight: '700'}}>{connType}</Text></View>
                    <Text style={{color: '#8A8F98', fontSize: 12}}>{connType === 'Local' ? 'Same network · Direct transfer' : 'Remote · Via tunnel'}</Text>
                  </View>
                </View>
              </TouchableOpacity>
            );
          })}
          <TouchableOpacity style={[styles.modalButton, {backgroundColor: '#2A2F3A', marginTop: 10}]} onPress={() => { setIsTargetModalVisible(false); setPendingUploadPayload(null); }}><Text style={styles.modalButtonText}>Cancel</Text></TouchableOpacity>
        </View></View>
      </Modal>

      {/* Camera Options Modal */}
      <Modal visible={isCameraOptionsVisible} animationType="slide" transparent={true}>
        <View style={styles.modalOverlay}><View style={styles.modalContent}>
          <Text style={styles.modalTitle}>Capture Mode</Text>
          <Text style={styles.modalSubtitle}>Take a photo to transfer or scan a data code.</Text>
          <TouchableOpacity style={styles.targetOption} onPress={launchDirectCamera}>
            <IconSymbol name="camera.fill" size={24} color="#F59E0B" />
            <View style={{marginLeft: 12}}><Text style={{color: '#FFF', fontSize: 16, fontWeight: '600'}}>Take Photo</Text><Text style={{color: '#8A8F98', fontSize: 12}}>Instantly transfer a camera image.</Text></View>
          </TouchableOpacity>
          <TouchableOpacity style={styles.targetOption} onPress={launchQRScanner}>
            <IconSymbol name="qrcode" size={24} color="#8B5CF6" />
            <View style={{marginLeft: 12}}><Text style={{color: '#FFF', fontSize: 16, fontWeight: '600'}}>Scan QR Code</Text><Text style={{color: '#8A8F98', fontSize: 12}}>Pair with PC or extract data.</Text></View>
          </TouchableOpacity>
          <TouchableOpacity style={[styles.modalButton, {backgroundColor: '#2A2F3A', marginTop: 10}]} onPress={() => setIsCameraOptionsVisible(false)}><Text style={styles.modalButtonText}>Cancel</Text></TouchableOpacity>
        </View></View>
      </Modal>

      {/* Connect Device Modal */}
      <Modal visible={isConnectModalVisible} animationType="slide" transparent={true}>
        <View style={styles.modalOverlay}><View style={styles.modalContent}>
          <Text style={styles.modalTitle}>Connect Device</Text>
          <Text style={styles.modalSubtitle}>Pair once — stays connected forever</Text>

          {/* Option 1: Scan QR */}
          <TouchableOpacity style={styles.targetOption} onPress={launchQRScanner}>
            <IconSymbol name="qrcode" size={24} color="#8B5CF6" />
            <View style={{marginLeft: 12}}>
              <Text style={{color: '#FFF', fontSize: 16, fontWeight: '600'}}>Scan QR Code</Text>
              <Text style={{color: '#8A8F98', fontSize: 12}}>Point camera at QR on your PC</Text>
            </View>
          </TouchableOpacity>

          {/* Option 2: Enter Code */}
          <View style={{marginTop: 16}}>
            <Text style={{color: '#8A8F98', fontSize: 12, fontWeight: '700', textTransform: 'uppercase', marginBottom: 8}}>Or Enter Code</Text>
            <View style={{flexDirection: 'row', gap: 10}}>
              <TextInput
                style={{flex: 1, backgroundColor: '#0F1115', color: '#FFF', fontSize: 22, fontWeight: '800',
                  borderRadius: 12, paddingHorizontal: 16, paddingVertical: 12, borderWidth: 1,
                  borderColor: '#2A2F3A', textAlign: 'center', letterSpacing: 6}}
                value={pairingCodeInput}
                onChangeText={setPairingCodeInput}
                placeholder="A7K9M2"
                placeholderTextColor="#4C5361"
                maxLength={6}
                autoCapitalize="characters"
              />
              <TouchableOpacity
                style={{backgroundColor: isPairing ? '#4C5361' : '#4A62EB', borderRadius: 12, paddingHorizontal: 20, justifyContent: 'center'}}
                onPress={() => { if (pairingCodeInput.length === 6) connectByCode(pairingCodeInput); }}
                disabled={isPairing}
              >
                {isPairing ? <ActivityIndicator size="small" color="#FFF" /> : <Text style={{color: '#FFF', fontWeight: '700', fontSize: 14}}>Connect</Text>}
              </TouchableOpacity>
            </View>
          </View>

          {/* This phone's code */}
          <View style={{marginTop: 20, padding: 14, backgroundColor: '#0F1115', borderRadius: 12, borderWidth: 1, borderColor: '#10B98133'}}>
            <Text style={{color: '#10B981', fontSize: 12, fontWeight: '700', marginBottom: 6}}>Your Phone's Code</Text>
            {myPairingCode ? (
              <Text style={{color: '#FFF', fontSize: 28, fontWeight: '900', letterSpacing: 8, textAlign: 'center'}}>{myPairingCode}</Text>
            ) : (
              <TouchableOpacity
                style={{backgroundColor: '#10B98122', borderRadius: 10, paddingVertical: 10, alignItems: 'center'}}
                onPress={generateMyPairingCode}
              >
                <Text style={{color: '#10B981', fontWeight: '700', fontSize: 14}}>🔑 Generate Code</Text>
              </TouchableOpacity>
            )}
            <Text style={{color: '#8A8F98', fontSize: 11, marginTop: 6, textAlign: 'center'}}>Enter this code on your PC to connect</Text>
          </View>

          {/* Connected status */}
          {pairedPcName && (
            <View style={{marginTop: 12, padding: 10, backgroundColor: '#10B98111', borderRadius: 10, flexDirection: 'row', alignItems: 'center', gap: 8}}>
              <View style={{width: 8, height: 8, borderRadius: 4, backgroundColor: '#10B981'}} />
              <Text style={{color: '#10B981', fontSize: 13, fontWeight: '600'}}>Paired with {pairedPcName}</Text>
            </View>
          )}

          <TouchableOpacity style={[styles.modalButton, {backgroundColor: '#2A2F3A', marginTop: 16}]}
            onPress={() => { setIsConnectModalVisible(false); setPairingCodeInput(''); }}>
            <Text style={styles.modalButtonText}>Close</Text>
          </TouchableOpacity>
        </View></View>
      </Modal>

      {/* QR Scanner */}
      {isQRScannerActive && (
        <Modal visible={isQRScannerActive} animationType="fade" transparent={false}>
          <View style={{flex: 1, backgroundColor: '#000'}}>
            <CameraView style={{flex: 1}} facing="back" barcodeScannerSettings={{ barcodeTypes: ["qr"] }} onBarcodeScanned={handleBarcodeScanned} />
            <TouchableOpacity style={{position: 'absolute', bottom: 50, alignSelf: 'center', backgroundColor: '#EF4444', padding: 15, borderRadius: 30}} onPress={() => setIsQRScannerActive(false)}>
              <Text style={{color: '#fff', fontWeight: 'bold', fontSize: 16}}>Cancel Scan</Text>
            </TouchableOpacity>
          </View>
        </Modal>
      )}

      <KeyboardAvoidingView behavior={Platform.OS === 'ios' ? 'padding' : undefined} style={{flex: 1}}>
        {/* Header */}
        <View style={[styles.header, { flexDirection: 'row', justifyContent: 'space-between', alignItems: 'center' }]}>
          <View>
            <Text style={styles.title}>FlyShelf</Text>
            <View style={styles.statusRow}>
              <View style={[styles.indicator, { backgroundColor: pairingKeyRef.current ? (pairedPcName ? '#10B981' : '#4A62EB') : '#EF4444' }]} />
              <Text style={styles.statusText}>{pairingKeyRef.current ? (pairedPcName ? `Connected to ${pairedPcName}` : 'Cloud Active') : '⚠ Not Paired'}</Text>
            </View>
          </View>
          <View style={{flexDirection: 'row', gap: 10}}>
            <TouchableOpacity onPress={() => setIsConnectModalVisible(true)} style={{padding: 10, backgroundColor: '#8B5CF622', borderRadius: 10}}>
              <IconSymbol name="link" size={20} color="#8B5CF6" />
            </TouchableOpacity>
            {Platform.OS === 'android' && (<TouchableOpacity onPress={() => AdvanceOverlay?.startOverlay()} style={{padding: 10, backgroundColor: '#4A62EB33', borderRadius: 10}}><IconSymbol name="macwindow" size={20} color="#4A62EB" /></TouchableOpacity>)}
            <TouchableOpacity onPress={clearAllClips} style={{padding: 10, backgroundColor: '#2A2F3A', borderRadius: 10}}><IconSymbol name="trash" size={20} color="#EF4444" /></TouchableOpacity>
          </View>
        </View>

        {/* Global Sync Toggle */}
        <TouchableOpacity activeOpacity={0.7} onPress={() => setGlobalSyncEnabled(!isGlobalSyncEnabled)} style={{marginHorizontal: 20, marginBottom: 15, padding: 12, backgroundColor: isGlobalSyncEnabled ? '#10B98122' : '#2A2F3A', borderRadius: 12, borderWidth: 1, borderColor: isGlobalSyncEnabled ? '#10B98155' : '#4C5361', flexDirection: 'row', alignItems: 'center'}}>
          <IconSymbol name={isGlobalSyncEnabled ? "cloud.fill" : "cloud"} size={22} color={isGlobalSyncEnabled ? "#10B981" : "#8A8F98"} />
          <View style={{marginLeft: 12, flex: 1}}>
            <Text style={{color: '#FFF', fontSize: 14, fontWeight: '700', marginBottom: 2}}>Global Cloud Transfer</Text>
            <Text style={{color: '#8A8F98', fontSize: 11}}>{isGlobalSyncEnabled ? "Enabled. Syncing payloads across entire mesh." : "Disabled. Only connecting to local PC proxies."}</Text>
          </View>
          <View style={{width: 40, height: 24, borderRadius: 12, backgroundColor: isGlobalSyncEnabled ? '#10B981' : '#4C5361', justifyContent: 'center', alignItems: isGlobalSyncEnabled ? 'flex-end' : 'flex-start', paddingHorizontal: 2}}>
            <View style={{width: 20, height: 20, borderRadius: 10, backgroundColor: '#FFF'}} />
          </View>
        </TouchableOpacity>

        {/* Clip Feed */}
        <View style={styles.feedContainer}>
          {isRefreshing && clips.length === 0 ? (
            <ActivityIndicator size="large" color="#4A62EB" style={{marginTop: 50}} />
          ) : clips.filter(clipFilter).length === 0 ? (
            <Text style={styles.emptyText}>No clips synced yet.</Text>
          ) : (
            <FlatList
              data={clips.filter(clipFilter)}
              keyExtractor={(item, index) => item.id ? item.id : index.toString()}
              showsVerticalScrollIndicator={false}
              initialNumToRender={15}
              maxToRenderPerBatch={10}
              windowSize={5}
              removeClippedSubviews={true}
              updateCellsBatchingPeriod={50}
              renderItem={({ item, index: itemIndex }) => {
                let iconName = 'doc.text', iconColor = '#8A8F98';
                const lowerTit = (item.Title || item.Raw || '').toLowerCase();
                const isApk = lowerTit.endsWith('.apk');
                const isPdf = item.Type === 'Pdf' || lowerTit.endsWith('.pdf');
                const isDoc = lowerTit.endsWith('.doc') || lowerTit.endsWith('.docx') || item.Type === 'Document';
                if (item.Type === 'ImageLink' || item.Type === 'Image') { iconName = 'photo'; iconColor = '#ec4899'; }
                else if (item.Type === 'Url') { iconName = 'globe'; iconColor = '#0EA5E9'; }
                else if (isPdf) { iconName = 'doc.richtext'; iconColor = '#EF4444'; }
                else if (isApk) { iconName = 'hammer.fill'; iconColor = '#10B981'; }
                else if (isDoc) { iconName = 'doc.text.fill'; iconColor = '#3B82F6'; }
                else if (['File', 'Archive', 'Video', 'Presentation'].includes(item.Type)) { iconName = 'folder.fill'; iconColor = '#F59E0B'; }
                else if (item.Type === 'Code') { iconName = 'curlybraces'; iconColor = '#10B981'; }
                else if (item.Type === 'QRCode') { iconName = 'qrcode'; iconColor = '#8B5CF6'; }

                const mediaUrl = getMediaUrlForItem(item);
                const transferId = item.id || (item.Title || '').replace(/[^a-zA-Z0-9.-]/g, '_');
                const incomingProgress = incomingTransferProgress[transferId];
                const isIncomingTransfer = incomingProgress !== undefined && incomingProgress < 1;
                const heavyFileTypes = ['Pdf', 'Document', 'Archive', 'Video', 'Audio', 'File', 'Presentation'];
                const isHeavyFile = heavyFileTypes.includes(item.Type) || (item.Title || '').toLowerCase().endsWith('.apk');

                return (
                  <View style={{ position: 'relative' }}>
                  <AnimatedCard
                    index={itemIndex}
                    style={[styles.clipCard, isMultiSelectMode && selectedItemIds.has(item.id || '') && { borderColor: colors.accent.primary, borderWidth: 1.5 }]}
                    onPress={() => { const itemKey = item.id || `idx_${itemIndex}`; if (isMultiSelectMode) toggleSelectItem(item.id || ''); else if (activeOptionsId === itemKey) setActiveOptionsId(null); else setActiveOptionsId(itemKey); }}
                    onLongPress={() => { if (!isMultiSelectMode) { setIsMultiSelectMode(true); setSelectedItemIds(new Set([item.id || ''])); setActiveOptionsId(null); } }}
                    skipEntrance={itemIndex > 12}
                  >
                    {isMultiSelectMode && (
                      <View style={{position: 'absolute', left: 8, top: 8, width: 24, height: 24, borderRadius: 12, backgroundColor: selectedItemIds.has(item.id || '') ? '#4A62EB' : 'rgba(255,255,255,0.1)', borderWidth: 2, borderColor: selectedItemIds.has(item.id || '') ? '#4A62EB' : '#4C5361', alignItems: 'center', justifyContent: 'center', zIndex: 10}}>
                        {selectedItemIds.has(item.id || '') && <IconSymbol name="checkmark" size={12} color="#FFF" />}
                      </View>
                    )}
                    {/* Transfer method badge */}
                    {(() => {
                      const srcType = item.SourceDeviceType || '';
                      const srcName = item.SourceDeviceName || '';
                      const rawUrl = item.Raw || '';
                      let emoji = '📋';
                      let badgeColor = '#4C5361';
                      if (srcType === 'Mobile' || srcName === 'Phone' || srcName === deviceName) {
                        emoji = '📱'; badgeColor = '#8B5CF6';
                      } else if (srcType === 'PC' || srcName.toLowerCase().includes('pc')) {
                        emoji = '💻'; badgeColor = '#3B82F6';
                      }
                      // Override with transfer method
                      if (rawUrl.includes('trycloudflare.com')) { emoji = '🌐'; badgeColor = '#F59E0B'; }
                      else if (rawUrl.includes('firebasestorage.googleapis.com') || rawUrl.includes('firebase')) { emoji = '☁️'; badgeColor = '#F59E0B'; }
                      else if (rawUrl.startsWith('http://192.') || rawUrl.startsWith('http://10.') || rawUrl.startsWith('http://172.')) { emoji = '📡'; badgeColor = '#10B981'; }
                      const displayName = srcName && srcName !== deviceName ? srcName : '';
                      return (
                        <View style={{position: 'absolute', right: 6, top: 4, flexDirection: 'row', alignItems: 'center', backgroundColor: 'rgba(15,17,21,0.85)', borderRadius: 8, paddingHorizontal: 6, paddingVertical: 2, zIndex: 5}}>
                          <Text style={{fontSize: 10}}>{emoji}</Text>
                          {displayName ? <Text style={{color: badgeColor, fontSize: 9, fontWeight: '700', marginLeft: 3}} numberOfLines={1}>{displayName.length > 10 ? displayName.slice(0, 10) + '…' : displayName}</Text> : null}
                        </View>
                      );
                    })()}
                    <View style={{ flex: 1, padding: 4, paddingLeft: isMultiSelectMode ? 32 : 4 }}>
                      {(item.Type === 'Image' || item.Type === 'ImageLink') ? (() => {
                        const imgUri = mediaUrl || item.CachedUri || item.Raw || '';
                        if (!imgUri) return <View style={{ marginBottom: 8, height: 100, borderRadius: 12, backgroundColor: '#1C202B', justifyContent: 'center', alignItems: 'center' }}><IconSymbol name="photo.fill" size={32} color="#4C5361" /><Text style={{color: '#8A8F98', fontSize: 12, marginTop: 8}}>No image URL</Text></View>;
                        return <CachedImage imgUri={imgUri} onPress={() => setExpandedImage(imgUri)} />;
                      })() : null}
                      {(item.Type !== 'Image' && item.Type !== 'ImageLink') && (
                        <Text style={styles.clipTitle}>{item.Raw || item.Title || `${item.Type || 'Clip'} from ${item.SourceDeviceName || 'Unknown'}`}</Text>
                      )}
                      {isIncomingTransfer && isHeavyFile && (
                        <View style={{position: 'absolute', bottom: 0, left: 0, right: 0, borderBottomLeftRadius: 16, borderBottomRightRadius: 16, overflow: 'hidden', zIndex: 20}}>
                          <View style={{height: 28, backgroundColor: 'rgba(15,17,21,0.92)', flexDirection: 'row', alignItems: 'center', paddingHorizontal: 12}}>
                            <ActivityIndicator size="small" color="#4A62EB" style={{marginRight: 8}} /><Text style={{color: '#8A8F98', fontSize: 11, fontWeight: '600', flex: 1}}>Receiving file...</Text>
                            <Text style={{color: '#4A62EB', fontSize: 12, fontWeight: '800'}}>{Math.round((incomingProgress || 0) * 100)}%</Text>
                          </View>
                          <View style={{height: 3, backgroundColor: 'rgba(74,98,235,0.15)'}}><View style={{height: 3, backgroundColor: '#4A62EB', width: `${Math.round((incomingProgress || 0) * 100)}%`, borderRadius: 2}} /></View>
                        </View>
                      )}
                    </View>
                  </AnimatedCard>
                    {activeOptionsId === (item.id || `idx_${itemIndex}`) && !(isIncomingTransfer && isHeavyFile) && (
                      <View style={{ position: 'absolute', right: 10, top: 10, flexDirection: 'row', backgroundColor: 'rgba(20,24,36,0.95)', borderRadius: 12, padding: 8, gap: 8, zIndex: 50 }}>
                        <TouchableOpacity onPress={async () => {
                          try {
                            if (!item.id) { ToastAndroid.show("Pinning is restricted to Global Cloud payloads.", ToastAndroid.SHORT); return; }
                            await update(ref(database, `${clipboardPath()}/${item.id}`), { IsPinned: !item.IsPinned });
                            setClips(prev => prev.map(c => c.id === item.id ? {...c, IsPinned: !c.IsPinned} : c));
                            ToastAndroid.show(item.IsPinned ? "Unpinned" : "Pinned!", ToastAndroid.SHORT);
                          } catch(e) {}
                          setActiveOptionsId(null);
                        }} style={[styles.actionBtnIcon, {backgroundColor: item.IsPinned ? '#F59E0B33' : '#2A2F3A'}]}>
                          <IconSymbol name={item.IsPinned ? "pin.fill" : "pin"} size={18} color={item.IsPinned ? "#F59E0B" : "#8A8F98"} />
                        </TouchableOpacity>
                        <TouchableOpacity onPress={async () => {
                          const contentStr = item.Raw || item.Title || '';
                          if (item.Type === 'Image' || item.Type === 'ImageLink') {
                            try { const src = item.CachedUri || mediaUrl || item.Raw; if (src) { if (src.startsWith('file://') || src.startsWith('/')) { const b64 = await FileSystem.readAsStringAsync(src.startsWith('file://') ? src : `file://${src}`, { encoding: FileSystem.EncodingType.Base64 }); await Clipboard.setImageAsync(b64); } else { const localUri = `${SYNC_CACHE_BASE}copy_${Date.now()}.png`; const dl = await FileSystem.downloadAsync(src, localUri, { headers: { 'X-Advance-Client': 'MobileCompanion' } }); const b64 = await FileSystem.readAsStringAsync(dl.uri, { encoding: FileSystem.EncodingType.Base64 }); await Clipboard.setImageAsync(b64); } if (Platform.OS === 'android') ToastAndroid.show("Image Copied", ToastAndroid.SHORT); } } catch(e) { await Clipboard.setStringAsync(contentStr); if (Platform.OS === 'android') ToastAndroid.show("URL Copied", ToastAndroid.SHORT); }
                          } else { await Clipboard.setStringAsync(contentStr); if (Platform.OS === 'android') ToastAndroid.show("Copied!", ToastAndroid.SHORT); }
                          setActiveOptionsId(null);
                        }} style={[styles.actionBtnIcon, {backgroundColor: '#4A62EB33'}]}>
                          <IconSymbol name="doc.on.doc" size={18} color="#4A62EB" />
                        </TouchableOpacity>
                        {(item.Type === 'Url' || (item.Raw && item.Raw.startsWith('http'))) && (
                          <TouchableOpacity onPress={() => { Linking.openURL(item.Raw).catch(() => {}); setActiveOptionsId(null); }} style={[styles.actionBtnIcon, {backgroundColor: '#0EA5E933'}]}>
                            <IconSymbol name="arrow.up.right" size={18} color="#0EA5E9" />
                          </TouchableOpacity>
                        )}
                        <TouchableOpacity onPress={async () => {
                          if (item.id) {
                            setLocalDeletedIds(prev => { const n = new Set(prev); n.add(item.id!); AsyncStorage.setItem('localDeletedIds', JSON.stringify([...n])).catch(() => {}); return n; });
                            if (isGlobalSyncEnabled && pairingKeyRef.current) { try { await remove(ref(database, `${clipboardPath()}/${item.id}`)); } catch(e) {} }
                          } else {
                            setClips(prev => prev.filter(c => !(c.Title === item.Title && c.Raw === item.Raw && c.Timestamp === item.Timestamp)));
                          }
                          setActiveOptionsId(null);
                          if (Platform.OS === 'android') ToastAndroid.show("Deleted", ToastAndroid.SHORT);
                        }} style={[styles.actionBtnIcon, {backgroundColor: '#EF444433'}]}>
                          <IconSymbol name="trash" size={18} color="#EF4444" />
                        </TouchableOpacity>
                      </View>
                    )}
                  </View>
                );
              }}
            />
          )}
        </View>

        {/* Multi-Select Bar */}
        {isMultiSelectMode && (
          <View style={{backgroundColor: '#1C1F26', borderTopWidth: 1, borderColor: '#2A2F3A', padding: 12, flexDirection: 'row', alignItems: 'center', gap: 8}}>
            <Text style={{color: '#8A8F98', fontSize: 13, fontWeight: '600', marginRight: 4}}>{selectedItemIds.size} selected</Text>
            {(() => { const sel = getSelectedClips(); const allPdf = sel.length >= 2 && sel.every(c => c.Type === 'Pdf' || (c.Title || '').toLowerCase().endsWith('.pdf')); if (allPdf) return (<TouchableOpacity style={{backgroundColor: '#EF4444', paddingHorizontal: 14, paddingVertical: 8, borderRadius: 10, flexDirection: 'row', alignItems: 'center', gap: 4}} onPress={openMergeModal}><IconSymbol name="doc.on.doc" size={14} color="#FFF" /><Text style={{color: '#FFF', fontSize: 12, fontWeight: '700'}}>Merge PDFs</Text></TouchableOpacity>); return null; })()}
            <TouchableOpacity style={{backgroundColor: '#10B981', paddingHorizontal: 14, paddingVertical: 8, borderRadius: 10, flexDirection: 'row', alignItems: 'center', gap: 4}} onPress={async () => {
              try { const selected = clips.filter(c => selectedItemIds.has(c.id || '')); if (selected.length === 0) return; const item = selected[0]; const mUrl = getMediaUrlForItem(item);
              if (mUrl.startsWith('http')) { const safeName = (item.Title || `file_${Date.now()}`).replace(/[^a-zA-Z0-9.-]/g, '_'); const localUri = DOWNLOAD_BASE + safeName; const fileInfo = await FileSystem.getInfoAsync(localUri); let uri = localUri; if (!fileInfo.exists) { if (Platform.OS === 'android') ToastAndroid.show('Downloading for share...', ToastAndroid.SHORT); const dl = await FileSystem.downloadAsync(mUrl, localUri, { headers: { 'X-Advance-Client': 'MobileCompanion' } }); uri = dl.uri; } await Sharing.shareAsync(uri, { dialogTitle: `Share ${safeName}` }); } else { const text = item.Raw || item.Title || ''; await Sharing.shareAsync(text, { dialogTitle: 'Share' }).catch(() => { Clipboard.setStringAsync(text); if (Platform.OS === 'android') ToastAndroid.show('Copied', ToastAndroid.SHORT); }); }
              } catch(e) { if (Platform.OS === 'android') ToastAndroid.show('Share failed', ToastAndroid.SHORT); }
            }}><IconSymbol name="square.and.arrow.up" size={14} color="#FFF" /><Text style={{color: '#FFF', fontSize: 12, fontWeight: '700'}}>Share</Text></TouchableOpacity>
            <TouchableOpacity style={{backgroundColor: '#4A62EB', paddingHorizontal: 14, paddingVertical: 8, borderRadius: 10, flexDirection: 'row', alignItems: 'center', gap: 4}} onPress={openForceSyncModal}><IconSymbol name="bolt.fill" size={14} color="#FFF" /><Text style={{color: '#FFF', fontSize: 12, fontWeight: '700'}}>Force Sync</Text></TouchableOpacity>
            <View style={{flex: 1}} />
            <TouchableOpacity style={{backgroundColor: '#2A2F3A', paddingHorizontal: 14, paddingVertical: 8, borderRadius: 10}} onPress={exitMultiSelect}><Text style={{color: '#FFF', fontSize: 12, fontWeight: '700'}}>Cancel</Text></TouchableOpacity>
          </View>
        )}

        {/* PDF Merge Modal */}
        <Modal visible={isMergeModalVisible} animationType="slide" transparent={true}>
          <View style={styles.modalOverlay}><View style={[styles.modalContent, {maxHeight: '80%'}]}>
            <Text style={styles.modalTitle}>Arrange & Merge PDFs</Text>
            <Text style={styles.modalSubtitle}>Drag items up/down to reorder before merging.</Text>
            <ScrollView style={{maxHeight: 350, marginTop: 12}}>
              {mergeQueue.map((item, idx) => (
                <View key={idx} style={{flexDirection: 'row', alignItems: 'center', backgroundColor: '#2A2F3A', borderRadius: 12, padding: 12, marginBottom: 8}}>
                  <View style={{width: 28, height: 28, borderRadius: 14, backgroundColor: '#EF4444', alignItems: 'center', justifyContent: 'center', marginRight: 10}}><Text style={{color: '#FFF', fontSize: 12, fontWeight: '800'}}>{idx + 1}</Text></View>
                  <Text style={{color: '#FFF', fontSize: 13, flex: 1, fontWeight: '500'}} numberOfLines={1}>{item.Title}</Text>
                  <View style={{flexDirection: 'row', gap: 6}}>
                    <TouchableOpacity onPress={() => moveMergeItem(idx, idx - 1)} style={{backgroundColor: '#1C1F26', width: 30, height: 30, borderRadius: 8, alignItems: 'center', justifyContent: 'center'}}><IconSymbol name="chevron.up" size={14} color="#FFF" /></TouchableOpacity>
                    <TouchableOpacity onPress={() => moveMergeItem(idx, idx + 1)} style={{backgroundColor: '#1C1F26', width: 30, height: 30, borderRadius: 8, alignItems: 'center', justifyContent: 'center'}}><IconSymbol name="chevron.down" size={14} color="#FFF" /></TouchableOpacity>
                  </View>
                </View>
              ))}
            </ScrollView>
            <TouchableOpacity style={{backgroundColor: '#EF4444', paddingVertical: 16, borderRadius: 14, alignItems: 'center', marginTop: 16}} onPress={executePdfMerge}><Text style={{color: '#FFF', fontSize: 16, fontWeight: '800'}}>Merge {mergeQueue.length} PDFs</Text></TouchableOpacity>
            <TouchableOpacity style={{backgroundColor: '#2A2F3A', paddingVertical: 14, borderRadius: 14, alignItems: 'center', marginTop: 8}} onPress={() => setIsMergeModalVisible(false)}><Text style={{color: '#FFF', fontSize: 14, fontWeight: '600'}}>Cancel</Text></TouchableOpacity>
          </View></View>
        </Modal>

        {/* PDF Page Editor */}
        <PdfPageEditor
          visible={pageEditorVisible}
          onClose={() => setPageEditorVisible(false)}
          pdfUri={pageEditorUri}
          pdfTitle={pageEditorTitle}
          outputDir={CONVERTED_BASE}
          onSaved={(newUri, title) => {
            const newItem: ClipItem = {
              Title: title, Type: 'Pdf', Raw: newUri,
              Time: new Date().toLocaleString(), SourceDeviceName: deviceName || 'Phone',
              SourceDeviceType: 'Mobile', Timestamp: Date.now(), CachedUri: newUri,
            };
            setClips(prev => [newItem, ...prev]);
          }}
        />

        {/* Force Sync Modal */}
        <Modal visible={isForceSyncModalVisible} animationType="slide" transparent={true}>
          <View style={styles.modalOverlay}><View style={[styles.modalContent, {maxHeight: '80%'}]}>
            <Text style={styles.modalTitle}>⚡ Force Sync</Text>
            <Text style={styles.modalSubtitle}>Push {selectedItemIds.size} items to selected devices.</Text>
            <TouchableOpacity style={{backgroundColor: '#4A62EB', paddingVertical: 14, borderRadius: 14, alignItems: 'center', marginTop: 12, flexDirection: 'row', justifyContent: 'center', gap: 6}} onPress={() => executeForcedSync(forceSyncDevices.map(d => d.key))}><IconSymbol name="bolt.fill" size={16} color="#FFF" /><Text style={{color: '#FFF', fontSize: 15, fontWeight: '800'}}>Force to ALL ({forceSyncDevices.length})</Text></TouchableOpacity>
            <Text style={{color: '#8A8F98', fontSize: 12, marginTop: 16, marginBottom: 8, fontWeight: '700', textTransform: 'uppercase'}}>Or Select Individual Devices</Text>
            <ScrollView style={{maxHeight: 250}}>
              {forceSyncDevices.map((device, i) => (
                <TouchableOpacity key={i} style={[styles.targetOption, {marginBottom: 8}]} onPress={() => executeForcedSync([device.key])}>
                  <View style={{width: 10, height: 10, borderRadius: 5, backgroundColor: device.IsOnline ? '#10B981' : '#4C5361', marginRight: 10}} />
                  <IconSymbol name={device.DeviceType === 'PC' ? 'laptopcomputer' : 'iphone'} size={22} color={device.IsOnline ? '#10B981' : '#4C5361'} />
                  <View style={{marginLeft: 10, flex: 1}}>
                    <Text style={{color: '#FFF', fontSize: 15, fontWeight: '600'}}>{device.DeviceName || device.key}</Text>
                    <View style={{flexDirection: 'row', alignItems: 'center', gap: 6, marginTop: 2}}>
                      <View style={{backgroundColor: connectionColors[getConnectionType(device, pcLocalIp)] + '22', borderRadius: 4, paddingHorizontal: 6, paddingVertical: 1}}><Text style={{color: connectionColors[getConnectionType(device, pcLocalIp)], fontSize: 10, fontWeight: '700'}}>{getConnectionType(device, pcLocalIp)}</Text></View>
                      <Text style={{color: device.IsOnline ? '#10B981' : '#8A8F98', fontSize: 11}}>{device.IsOnline ? 'Online' : 'Offline'}</Text>
                    </View>
                  </View>
                  <IconSymbol name="bolt.fill" size={16} color="#F59E0B" />
                </TouchableOpacity>
              ))}
              {forceSyncDevices.length === 0 && <Text style={{color: '#8A8F98', textAlign: 'center', marginTop: 20}}>No devices registered yet.</Text>}
            </ScrollView>
            <TouchableOpacity style={{backgroundColor: '#2A2F3A', paddingVertical: 14, borderRadius: 14, alignItems: 'center', marginTop: 12}} onPress={() => setIsForceSyncModalVisible(false)}><Text style={{color: '#FFF', fontSize: 14, fontWeight: '600'}}>Cancel</Text></TouchableOpacity>
          </View></View>
        </Modal>

        {/* Input Area */}
        <View style={styles.inputArea}>
          <TouchableOpacity style={styles.attachButton} onPress={pickImageAndSend} disabled={isSending}><IconSymbol name="photo.on.rectangle.angled" size={24} color="#8A8F98" /></TouchableOpacity>
          <TouchableOpacity style={styles.attachButton} onPress={pickFileAndSend} disabled={isSending}><IconSymbol name="paperclip" size={24} color="#8A8F98" /></TouchableOpacity>
          <TouchableOpacity style={styles.attachButton} onPress={() => setIsCameraOptionsVisible(true)} disabled={isSending}><IconSymbol name="camera.fill" size={24} color="#8A8F98" /></TouchableOpacity>
          <TextInput style={styles.textInput} placeholder="Type or paste to send to PC..." placeholderTextColor="#4C5361" value={inputText} onChangeText={setInputText} multiline />
          <TouchableOpacity style={styles.sendButton} onPress={sendTextToPc} disabled={isSending || !inputText}>
            {isSending ? <ActivityIndicator color="#fff" /> : <IconSymbol name="arrow.up.circle.fill" size={36} color={inputText ? "#4A62EB" : "#2A2F3A"} />}
          </TouchableOpacity>
        </View>
      </KeyboardAvoidingView>

      {/* Expanded Image Modal */}
      <Modal visible={!!expandedImage} transparent={true} animationType="fade" onRequestClose={() => setExpandedImage(null)}>
        <View style={{flex: 1, backgroundColor: 'rgba(0,0,0,0.95)', justifyContent: 'center', alignItems: 'center'}}>
          <TouchableOpacity style={{position: 'absolute', top: 60, right: 20, zIndex: 10, padding: 10, backgroundColor: 'rgba(255,255,255,0.15)', borderRadius: 20, width: 44, height: 44, alignItems: 'center', justifyContent: 'center'}} onPress={() => setExpandedImage(null)}><IconSymbol name="xmark" size={24} color="#FFF" /></TouchableOpacity>
          {expandedImage && <Image source={{uri: expandedImage, headers: { 'X-Advance-Client': 'MobileCompanion' }}} style={{width: '100%', height: '80%'}} contentFit="contain" />}
          {expandedImage && (
            <View style={{position: 'absolute', bottom: 50, flexDirection: 'row', gap: 30, zIndex: 10}}>
              <TouchableOpacity style={{backgroundColor: 'rgba(255,255,255,0.15)', borderRadius: 30, width: 60, height: 60, alignItems: 'center', justifyContent: 'center'}} onPress={async () => {
                if (Platform.OS === 'web') return;
                try { const safeName = `image_${Date.now()}.jpg`; const localUri = DOWNLOAD_BASE + safeName; const dl = await FileSystem.downloadAsync(expandedImage, localUri, { headers: { 'X-Advance-Client': 'MobileCompanion' } }); const perm = await MediaLibrary.requestPermissionsAsync(); if (perm.status === 'granted') { await MediaLibrary.saveToLibraryAsync(dl.uri); if (Platform.OS === 'android') ToastAndroid.show("Saved to Gallery", ToastAndroid.SHORT); } } catch(e) {}
              }}><IconSymbol name="arrow.down" size={26} color="#FFF" /></TouchableOpacity>
              <TouchableOpacity style={{backgroundColor: '#4A62EB', borderRadius: 30, width: 60, height: 60, alignItems: 'center', justifyContent: 'center'}} onPress={async () => {
                if (Platform.OS === 'web') return;
                try { const safeName = `image_share_${Date.now()}.jpg`; const localUri = SYNC_CACHE_BASE + safeName; const dl = await FileSystem.downloadAsync(expandedImage, localUri, { headers: { 'X-Advance-Client': 'MobileCompanion' } }); if (await Sharing.isAvailableAsync()) await Sharing.shareAsync(dl.uri); } catch(e) {}
              }}><IconSymbol name="square.and.arrow.up" size={26} color="#FFF" /></TouchableOpacity>
            </View>
          )}
        </View>
      </Modal>
    </SafeAreaView>
    </LinearGradient>
  );
}
