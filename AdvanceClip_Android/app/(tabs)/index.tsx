import React, { useState, useEffect, useRef } from 'react';
import { View, Text, TextInput, TouchableOpacity, FlatList, ActivityIndicator, KeyboardAvoidingView, Platform, Alert, AppState, AppStateStatus, Modal, ToastAndroid, NativeModules, ScrollView } from 'react-native';
import { SafeAreaView } from 'react-native-safe-area-context';
import * as Sharing from 'expo-sharing';
import * as IntentLauncher from 'expo-intent-launcher';
import { useSettings } from '../../context/SettingsContext';
import { IconSymbol } from '@/components/ui/icon-symbol';
import { database, storage } from '../../firebaseConfig';
import { ref, push, set, get, onValue, query, limitToLast, orderByChild, update, remove } from 'firebase/database';
import { ref as storageRef, uploadBytesResumable, getDownloadURL } from 'firebase/storage';
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
import CachedImage from '../../components/CachedImage';

const { AdvanceOverlay } = NativeModules;

// ════════════════════════════════════════════════════════
// MAIN SCREEN
// ════════════════════════════════════════════════════════
export default function SyncScreen() {
  const { pcLocalIp, deviceName, setDeviceName, isGlobalSyncEnabled, setGlobalSyncEnabled, isFloatingBallEnabled } = useSettings();

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

  // ─── PC URL (auto-discovered from Firebase, no manual config needed) ───
  const cachedPcUrlRef = useRef<string | null>(null);
  const cachedPcUrlTimestampRef = useRef<number>(0);

  const getCachedPcUrl = async (): Promise<string> => {
    // Return cached URL if fresh (15s TTL)
    const now = Date.now();
    if (cachedPcUrlRef.current && (now - cachedPcUrlTimestampRef.current) < 15_000) {
      return cachedPcUrlRef.current;
    }
    // Find PC from Firebase auto-discovered devices
    const pc = activeDevices.find(d => d.DeviceType === 'PC');
    if (pc) {
      const urls = getDeviceUrls(pc);
      // Single URL = use directly (no health check waste)
      // Multiple URLs = resolveOptimalUrl picks fastest (LAN biased)
      const resolved = urls.length === 1 ? urls[0] : await resolveOptimalUrl(pc);
      if (resolved) {
        cachedPcUrlRef.current = resolved;
        cachedPcUrlTimestampRef.current = now;
        return resolved;
      }
    }
    // Fallback: manual IP from Settings (legacy, rarely needed)
    const raw = pcLocalIp?.trim();
    if (raw) {
      const fallback = raw.startsWith('http') ? raw.replace(/\/$/, '') : `http://${raw.includes(':') ? raw : raw + ':8999'}`;
      return fallback;
    }
    return 'http://localhost:8999';
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
        const mapped = deduped.slice(0, 20).map(c => {
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
        try { AdvanceOverlay.syncNativeDB(JSON.stringify(mapped)); } catch(e) {}
      } else {
        try { AdvanceOverlay.syncNativeDB("[]"); } catch(e) {}
      }
    }
  }, [clips, isFloatingBallEnabled, localWipeTimestamp, localDeletedIds]);

  // ─── Bidirectional Overlay Sync ───
  useEffect(() => {
    if (Platform.OS !== 'android' || !AdvanceOverlay || !isFloatingBallEnabled || !deviceName) return;
    const pollInterval = setInterval(async () => {
      try {
        const copiedText = await AdvanceOverlay.getLastCopiedFromOverlay();
        if (copiedText && copiedText.trim().length > 0) {
          const newItem: ClipItem = {
            Title: copiedText.substring(0, 80), Type: 'Text', Raw: copiedText,
            Time: new Date().toLocaleString(), SourceDeviceName: deviceName,
            SourceDeviceType: 'Mobile', Timestamp: Date.now(),
          };
          setClips(prev => [newItem, ...prev]);
          if (isGlobalSyncEnabled) {
            try { const clipRef = push(ref(database, 'global_clipboard')); await set(clipRef, newItem); } catch(e) {}
          }
        }
      } catch(e) {}
    }, 1500);
    return () => clearInterval(pollInterval);
  }, [isFloatingBallEnabled, deviceName, isGlobalSyncEnabled]);

  // ─── Device Discovery ───
  const [activeDevices, setActiveDevices] = useState<any[]>([]);

  // ─── Screenshot Detection (ONLY when overlay service is NOT running) ───
  // When floating ball is active, OverlayService.startScreenshotPoll() handles detection.
  const lastScreenshotTsRef = useRef<number>(Date.now());
  useEffect(() => {
    if (Platform.OS !== 'android') return;
    // Skip — OverlayService handles screenshot detection when floating ball is active
    if (isFloatingBallEnabled && AdvanceOverlay) return;
    const screenshotPoll = setInterval(async () => {
      // Try native overlay first, fallback to MediaLibrary polling
      if (AdvanceOverlay) {
      try {
        const result = await AdvanceOverlay.getLatestScreenshot();
        if (result && result.path && result.timestamp > lastScreenshotTsRef.current) {
          lastScreenshotTsRef.current = result.timestamp;

          // Step 1: Copy screenshot to app-local storage (scoped storage blocks raw paths)
          const fileName = result.name || `screenshot_${Date.now()}.png`;
          const safeName = fileName.replace(/[^a-zA-Z0-9.-]/g, '_');
          await FileSystem.makeDirectoryAsync(IMAGE_CACHE_BASE, { intermediates: true }).catch(() => {});
          const localCopy = `${IMAGE_CACHE_BASE}${safeName}`;
          const sourceUri = result.path.startsWith('file://') ? result.path : `file://${result.path}`;

          // Try content URI approach first (more reliable on Android 11+)
          let localUri = localCopy;
          try {
            // Try reading from MediaLibrary instead of raw path
            const media = await MediaLibrary.getAssetsAsync({ first: 1, mediaType: ['photo'], sortBy: [[MediaLibrary.SortBy.creationTime, false]] });
            if (media.assets.length > 0) {
              const latestAsset = media.assets[0];
              const assetInfo = await MediaLibrary.getAssetInfoAsync(latestAsset.id);
              const assetUri = assetInfo.localUri || assetInfo.uri;
              if (assetUri) {
                await FileSystem.copyAsync({ from: assetUri, to: localCopy });
                localUri = localCopy;
              }
            }
          } catch (copyErr) {
            // Fallback: try direct file copy from raw path
            try { await FileSystem.copyAsync({ from: sourceUri, to: localCopy }); } catch (e2) {
              // Last resort: try downloading as content URI
              try {
                const contentUri = await FileSystem.getContentUriAsync(sourceUri);
                await FileSystem.copyAsync({ from: contentUri, to: localCopy });
              } catch (e3) { localUri = sourceUri; /* Use raw path as final fallback */ }
            }
          }

          // Step 2: Create clip item with local app-accessible path
          const screenshotItem: ClipItem = {
            Title: fileName, Type: 'ImageLink', Raw: localUri,
            Time: new Date().toLocaleString(), SourceDeviceName: deviceName || 'Phone',
            SourceDeviceType: 'Mobile', Timestamp: Date.now(), CachedUri: localUri,
          };
          setClips(prev => [screenshotItem, ...prev]);
          if (Platform.OS === 'android') ToastAndroid.show(`📸 Screenshot captured!`, ToastAndroid.SHORT);

          // Step 3: Copy to clipboard
          try {
            const base64 = await FileSystem.readAsStringAsync(localUri, { encoding: FileSystem.EncodingType.Base64 });
            await Clipboard.setImageAsync(base64);
          } catch(e) {}

          // Step 4: Upload to Firebase if global sync enabled
          if (isGlobalSyncEnabled) {
            try {
              const sRef = storageRef(storage, `clipboard_images/${safeName}`);
              const fileResp = await fetch(localUri);
              const blob = await fileResp.blob();
              await uploadBytesResumable(sRef, blob);
              const downloadURL = await getDownloadURL(sRef);
              screenshotItem.Raw = downloadURL;
              const clipRef = push(ref(database, 'clipboard'));
              await set(clipRef, screenshotItem);
              setClips(prev => prev.map(c => c.Title === fileName && c.Type === 'ImageLink' ? { ...c, Raw: downloadURL } : c));
            } catch(e) {}
          }

          // Step 5: Relay to PC
          try {
            const optimal = await getCachedPcUrl();
            if (optimal) {
              await FileSystem.uploadAsync(
                `${optimal}/api/sync_file?name=${encodeURIComponent(fileName)}&type=ImageLink&sourceDevice=${encodeURIComponent(deviceName || 'Phone')}`,
                localUri,
                { httpMethod: 'POST', uploadType: 0 as any, headers: { 'X-Advance-Client': 'MobileCompanion', 'X-Original-Date': Date.now().toString() } }
              );
            }
          } catch(e) {}
        }
      } catch(e) {}
      } else {
        // Fallback: poll MediaLibrary for new screenshots when overlay isn't active
        try {
          const media = await MediaLibrary.getAssetsAsync({ first: 1, mediaType: ['photo'], sortBy: [[MediaLibrary.SortBy.creationTime, false]] });
          if (media.assets.length > 0) {
            const latest = media.assets[0];
            const createdMs = latest.creationTime * 1000;
            if (createdMs > lastScreenshotTsRef.current && (latest.filename || '').toLowerCase().includes('screenshot')) {
              lastScreenshotTsRef.current = createdMs;
              const assetInfo = await MediaLibrary.getAssetInfoAsync(latest.id);
              const assetUri = assetInfo.localUri || assetInfo.uri;
              if (assetUri) {
                const safeName = (latest.filename || `screenshot_${Date.now()}.png`).replace(/[^a-zA-Z0-9.-]/g, '_');
                await FileSystem.makeDirectoryAsync(IMAGE_CACHE_BASE, { intermediates: true }).catch(() => {});
                const localCopy = `${IMAGE_CACHE_BASE}${safeName}`;
                await FileSystem.copyAsync({ from: assetUri, to: localCopy });
                const screenshotItem: ClipItem = {
                  Title: latest.filename || safeName, Type: 'ImageLink', Raw: localCopy,
                  Time: new Date().toLocaleString(), SourceDeviceName: deviceName || 'Phone',
                  SourceDeviceType: 'Mobile', Timestamp: Date.now(), CachedUri: localCopy,
                };
                setClips(prev => [screenshotItem, ...prev]);
                ToastAndroid.show(`📸 Screenshot captured!`, ToastAndroid.SHORT);
                try {
                  const base64 = await FileSystem.readAsStringAsync(localCopy, { encoding: FileSystem.EncodingType.Base64 });
                  await Clipboard.setImageAsync(base64);
                } catch {}
              }
            }
          }
        } catch {}
      }
    }, 3000);
    return () => clearInterval(screenshotPoll);
  }, [deviceName, isGlobalSyncEnabled, activeDevices, isFloatingBallEnabled]);

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

  // ─── Persistence ───
  useEffect(() => {
    AsyncStorage.getItem('localWipeTimestamp').then(val => {
      if (val) { setLocalWipeTimestamp(parseInt(val)); }
      else { const now = Date.now(); setLocalWipeTimestamp(now); AsyncStorage.setItem('localWipeTimestamp', now.toString()); }
    });
    AsyncStorage.getItem('localDeletedIds').then(val => {
      if (val) { try { setLocalDeletedIds(new Set(JSON.parse(val))); } catch(e) {} }
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
                  await MediaLibrary.createAlbumAsync("AdvanceClip Extractions", asset, false);
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
    const clipsRef = query(ref(database, 'clipboard'), orderByChild('Timestamp'), limitToLast(30));
    const unsubscribeFeed = onValue(clipsRef, (snapshot) => {
      if (snapshot.exists()) {
        const data = snapshot.val();
        const parsed = Object.keys(data).map(k => ({ id: k, ...data[k] })).reverse();
        const now = Date.now();
        recentSyncFingerprintsRef.current.forEach((ts, fp) => { if (now - ts > 30_000) recentSyncFingerprintsRef.current.delete(fp); });
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
        // Auto-download files with absolute HTTP URLs from Firebase (remote PC via Cloudflare)
        if (Platform.OS === 'android') {
          const latest = parsed[0];
          if (latest && latest.Raw?.startsWith('http') && ['Pdf', 'Document', 'File', 'Video', 'Audio', 'Archive', 'Presentation', 'Image', 'ImageLink'].includes(latest.Type)) {
            const fp = `dl::${latest.Raw.substring(0, 100)}`;
            if (!recentSyncFingerprintsRef.current.has(fp)) {
              recentSyncFingerprintsRef.current.set(fp, Date.now());
              // Fire-and-forget download
              (async () => {
                try {
                  if (latest.Type === 'Image' || latest.Type === 'ImageLink') {
                    const localUri = `${SYNC_CACHE_BASE}fb_img_${Date.now()}.png`;
                    const { uri } = await FileSystem.downloadAsync(latest.Raw, localUri, { headers: { 'X-Advance-Client': 'MobileCompanion' } });
                    const b64 = await FileSystem.readAsStringAsync(uri, { encoding: FileSystem.EncodingType.Base64 });
                    await Clipboard.setImageAsync(b64);
                    ToastAndroid.show(`🖼️ Image synced from ${latest.SourceDeviceName || 'PC'}`, ToastAndroid.SHORT);
                  } else {
                    const subfolder = latest.Type === 'Pdf' ? 'PDFs' : latest.Type === 'Video' ? 'Videos' : 'Documents';
                    const safeName = (latest.Title || `file_${Date.now()}`).replace(/[^a-zA-Z0-9._-]/g, '_');
                    const destPath = await getDownloadPath(subfolder, safeName);
                    const existing = await FileSystem.getInfoAsync(destPath);
                    if (!existing.exists) {
                      ToastAndroid.show(`⬇️ Downloading ${latest.Title}...`, ToastAndroid.SHORT);
                      const dl = await FileSystem.downloadAsync(latest.Raw, destPath, { headers: { 'X-Advance-Client': 'MobileCompanion' } });
                      if (dl.status === 200) ToastAndroid.show(`✅ ${latest.Title} saved`, ToastAndroid.SHORT);
                    }
                  }
                } catch (e) {}
              })();
            }
          }
        }
        setClips(parsed);
      } else { setClips([]); }
    });

    const nodesRef = query(ref(database, 'active_devices'));
    const unsubscribeNodes = onValue(nodesRef, async (snapshot) => {
      let rawDevices: any[] = [];
      if (snapshot.exists()) {
        const data = snapshot.val();
        const now = Date.now();
        rawDevices = Object.keys(data).map(k => ({ ...data[k], _key: k })).filter(d => d.IsOnline && d.Timestamp && (now - d.Timestamp) < 300_000);
      }
      // If no PC found in Firebase, probe manual IP from Settings as fallback
      const hasPc = rawDevices.some(d => d.DeviceType === 'PC');
      if (!hasPc && pcLocalIp) {
        try {
          const raw = pcLocalIp.trim();
          const probeUrl = raw.startsWith('http') ? raw.replace(/\/$/, '') : `http://${raw.includes(':') ? raw : raw.split(':')[0] + ':8999'}`;
          const res = await fetch(`${probeUrl}/api/health`, { method: 'GET', headers: { 'X-Advance-Client': 'MobileCompanion' }, signal: AbortSignal.timeout(2000) });
          if (res.ok) rawDevices.push({ DeviceName: 'PC (LAN)', DeviceType: 'PC', IsOnline: true, Url: probeUrl, LocalIp: probeUrl, _key: 'local_direct', Timestamp: Date.now() });
        } catch {}
      }
      setActiveDevices(rawDevices);
    });

    return () => { unsubscribeFeed(); unsubscribeNodes(); };
  }, [isGlobalSyncEnabled]);

  // ─── Local PC Polling ───
  useEffect(() => {
    const interval = setInterval(async () => {
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
                  try {
                    let mediaUrl = '';
                    if (latest.DownloadUrl?.startsWith('/')) mediaUrl = `${targetUrl}${latest.DownloadUrl}`;
                    else if (latest.PreviewUrl?.startsWith('/')) mediaUrl = `${targetUrl}${latest.PreviewUrl}`;
                    else if (latest.Raw?.startsWith('http')) mediaUrl = latest.Raw;
                    if (mediaUrl) {
                      const localUri = `${SYNC_CACHE_BASE}clip_sync_${Date.now()}.png`;
                      const { uri } = await FileSystem.downloadAsync(mediaUrl, localUri, { headers: { 'X-Advance-Client': 'MobileCompanion' } });
                      const b64 = await FileSystem.readAsStringAsync(uri, { encoding: (FileSystem as any).EncodingType.Base64 });
                      await Clipboard.setImageAsync(b64);
                      if (Platform.OS === 'android') ToastAndroid.show(`🖼️ Screenshot synced from PC!`, ToastAndroid.SHORT);
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
    }, 1000);
    return () => clearInterval(interval);
  }, [isGlobalSyncEnabled, activeDevices, pcLocalIp]);

  // ─── Device Self-Registration ───
  useEffect(() => {
    if (!deviceName) return;
    const myDeviceId = `Mobile_${deviceName.replace(/[^a-zA-Z0-9_]/g, '_')}`;
    const registerSelf = async () => {
      try { await set(ref(database, `active_devices/${myDeviceId}`), { DeviceId: myDeviceId, DeviceName: deviceName, DeviceType: 'Mobile', IsOnline: true, Timestamp: Date.now() }); } catch(e) {}
    };
    registerSelf();
    const heartbeat = setInterval(registerSelf, 30000);
    return () => { clearInterval(heartbeat); if (!isFloatingBallEnabled) set(ref(database, `active_devices/${myDeviceId}/IsOnline`), false).catch(() => {}); };
  }, [deviceName, isFloatingBallEnabled]);

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
          if (Object.keys(updates).length > 0) await update(ref(database, 'clipboard'), updates);
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
        if (text && text !== lastCopiedRef.current) { setLastCopiedText(text); await transmitTextSecurely(text); }
      }
    } catch(e) {}
  };

  const handleForegroundMediaCheck = async () => {
    try {
      let perm = await MediaLibrary.getPermissionsAsync();
      if (perm.status !== 'granted') { perm = await MediaLibrary.requestPermissionsAsync(); if (perm.status !== 'granted') return; }
      const media = await MediaLibrary.getAssetsAsync({ first: 1, mediaType: ['photo'], sortBy: [[MediaLibrary.SortBy.creationTime, false]] });
      if (media.assets.length > 0) {
        const latest = media.assets[0];
        const isRecent = (Date.now() - latest.creationTime) < 2 * 60 * 1000;
        if (isRecent && latest.id !== lastScannedImageId) {
          setLastScannedImageId(latest.id);
          setIsSending(true);
          try {
            const assetInfo = await MediaLibrary.getAssetInfoAsync(latest.id);
            if (assetInfo.localUri || assetInfo.uri) {
              let targetUrl = `http://${pcLocalIp}`;
              const activePc = activeDevices.find(d => d.DeviceType === 'PC' && d.Url);
              if (activePc) targetUrl = (await resolveOptimalUrl(activePc)) ?? targetUrl;
              let localSuccess = false;
              try {
                const upRes = await FileSystem.uploadAsync(`${targetUrl}/api/sync_file?name=${encodeURIComponent(assetInfo.filename || 'screenshot.jpg')}&type=ImageLink&sourceDevice=${encodeURIComponent(deviceName || 'Mobile')}`, assetInfo.localUri || assetInfo.uri, {
                  httpMethod: 'POST', uploadType: 0 as any,
                  headers: { 'X-Original-Date': Date.now().toString(), 'X-Advance-Client': 'MobileCompanion' }
                });
                localSuccess = upRes.status === 200;
              } catch(e) {}
              if (!localSuccess && isGlobalSyncEnabled) {
                const response = await fetch(assetInfo.localUri || assetInfo.uri);
                const blob = await response.blob();
                const sf = storageRef(storage, `archives/Screenshot_${Date.now()}.jpg`);
                await uploadBytesResumable(sf, blob);
                const downloadUrl = await getDownloadURL(sf);
                const newRef = push(ref(database, 'clipboard'));
                await set(newRef, { Title: `Screenshot_${Date.now()}.jpg`, Type: 'ImageLink', Raw: downloadUrl, Time: new Date().toLocaleTimeString(), Timestamp: Date.now(), SourceDeviceName: deviceName || 'Mobile', SourceDeviceType: 'Mobile' });
              }
              Platform.OS === 'android' ? ToastAndroid.show("Extracted Screenshot ✨", ToastAndroid.SHORT) : null;
            }
          } catch(e) {}
          setIsSending(false);
        }
      }
    } catch(e) {}
  };

  useEffect(() => {
    // Skip foreground media check when floating ball is active — OverlayService handles it
    if (isFloatingBallEnabled && AdvanceOverlay) return;
    handleForegroundClipboardCheck();
    handleForegroundMediaCheck();
    const subscription = AppState.addEventListener('change', (nextAppState: AppStateStatus) => {
      if (nextAppState === 'active') { handleForegroundClipboardCheck(); handleForegroundMediaCheck(); }
    });
    let screenshotPollInterval: ReturnType<typeof setInterval> | null = null;
    if (Platform.OS !== 'web') { screenshotPollInterval = setInterval(() => handleForegroundMediaCheck(), 3000); }
    let mediaSub: any = null;
    if (Platform.OS !== 'web' && typeof MediaLibrary.addListener === 'function') {
      mediaSub = MediaLibrary.addListener((event) => { if (event.hasIncrementalChanges || (event as any).insertedMedia?.length > 0) handleForegroundMediaCheck(); });
    }
    return () => { subscription.remove(); if (mediaSub) mediaSub.remove(); if (screenshotPollInterval) clearInterval(screenshotPollInterval); };
  }, []);

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
        const response = await fetchWithTimeout(`${targetUrl}/api/sync_text`, { method: 'POST', headers: { 'Content-Type': 'text/plain', 'X-Advance-Client': 'MobileCompanion', 'X-Source-Device': deviceName || 'Mobile' }, body: finalRaw }, 1500);
        localSuccess = response.ok;
      } catch(e) { cachedPcUrlRef.current = null; }
      if (!localSuccess && isGlobalSyncEnabled) {
        const newRef = push(ref(database, 'clipboard'));
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
      if (Platform.OS === 'android') ToastAndroid.show('Sending PDFs to PC for merge...', ToastAndroid.LONG);
      let targetUrl = `http://${pcLocalIp}`;
      const activePc = activeDevices.find((d: any) => d.DeviceType === 'PC');
      if (activePc) { const opt = await resolveOptimalUrl(activePc); if (opt) targetUrl = opt; }
      const pdfUrls = mergeQueue.map(item => getMediaUrlForItem(item)).filter(u => u.startsWith('http'));
      if (pdfUrls.length < 2) { Alert.alert('Error', 'Could not resolve PDF URLs.'); return; }
      const res = await fetchWithTimeout(`${targetUrl}/api/merge_pdfs`, { method: 'POST', headers: { 'Content-Type': 'application/json', 'X-Advance-Client': 'MobileCompanion' }, body: JSON.stringify({ urls: pdfUrls, sourceDevice: deviceName || 'Mobile' }) }, 30000);
      if (res.ok) { const body = await res.json(); if (body.downloadUrl) { const mergedUrl = body.downloadUrl.startsWith('http') ? body.downloadUrl : `${targetUrl}${body.downloadUrl}`; const localUri = CONVERTED_BASE + `merged_${Date.now()}.pdf`; await FileSystem.downloadAsync(mergedUrl, localUri, { headers: { 'X-Advance-Client': 'MobileCompanion' } }); await Sharing.shareAsync(localUri, { mimeType: 'application/pdf', UTI: 'com.adobe.pdf', dialogTitle: 'Merged PDF' }); } } else Alert.alert('Merge Failed');
    } catch (e) { Alert.alert('Merge Error'); }
    exitMultiSelect();
  };

  // ─── Force Sync ───
  const openForceSyncModal = async () => {
    if (selectedItemIds.size === 0) { Alert.alert('Nothing Selected'); return; }
    try { const { get: firebaseGet } = await import('firebase/database'); const snapshot = await firebaseGet(ref(database, 'active_devices')); if (snapshot.exists()) { const data = snapshot.val(); setForceSyncDevices(Object.keys(data).map(k => ({ key: k, ...data[k] })).filter(d => d.DeviceName !== deviceName)); } else setForceSyncDevices([]); } catch (e) { setForceSyncDevices([]); }
    setIsForceSyncModalVisible(true);
  };
  const executeForcedSync = async (targetDeviceKeys: string[]) => {
    setIsForceSyncModalVisible(false);
    const selected = getSelectedClips();
    if (selected.length === 0) return;
    if (Platform.OS === 'android') ToastAndroid.show(`Force syncing ${selected.length} items...`, ToastAndroid.LONG);
    try {
      for (const deviceKey of targetDeviceKeys) { for (const item of selected) { const forcedRef = push(ref(database, `forced_sync/${deviceKey}`)); await set(forcedRef, { ...item, ForcedBy: deviceName, ForcedAt: Date.now(), SourceDeviceName: item.SourceDeviceName || deviceName }); } }
      for (const item of selected) { if (!item.id) { const clipRef = push(ref(database, 'global_clipboard')); await set(clipRef, { ...item, Timestamp: Date.now() }); } }
      for (const deviceKey of targetDeviceKeys) {
        const dev = forceSyncDevices.find(d => d.key === deviceKey);
        if (dev?.LocalIp) { try { const url = await resolveOptimalUrl(dev); if (url) { for (const item of selected) { await fetchWithTimeout(`${url}/api/sync`, { method: 'POST', headers: { 'Content-Type': 'application/json', 'X-Advance-Client': 'MobileCompanion' }, body: JSON.stringify({ title: item.Title, content: item.Raw, type: item.Type, sourceDevice: deviceName }) }, 5000).catch(() => {}); } } } catch (e) {} }
      }
      if (Platform.OS === 'android') ToastAndroid.show('Force sync complete ✅', ToastAndroid.SHORT);
    } catch (e) { Alert.alert('Sync Error'); }
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
      setIsTargetModalVisible(true);
    } catch (err) { Alert.alert('Upload Failed'); }
  };
  const launchDirectCamera = async () => {
    setIsCameraOptionsVisible(false);
    const result = await ImagePicker.launchCameraAsync({ mediaTypes: ['images'], allowsEditing: false, quality: 0.8 });
    if (!result.canceled) {
      const file = result.assets[0];
      try { const b64 = await FileSystem.readAsStringAsync(file.uri, { encoding: (FileSystem as any).EncodingType.Base64 }); await Clipboard.setImageAsync(b64); Platform.OS === 'android' ? ToastAndroid.show("Captured & Copied", ToastAndroid.SHORT) : null; } catch (e) {}
      setPendingUploadPayload({ uri: file.uri, name: file.fileName || `camera_${Date.now()}.jpg`, size: file.fileSize, type: 'Image' });
      setIsTargetModalVisible(true);
    }
  };
  const pickImageAndSend = async () => {
    const result = await ImagePicker.launchImageLibraryAsync({ mediaTypes: ['images', 'videos'], allowsEditing: false, quality: 0.8 });
    if (!result.canceled) {
      const file = result.assets[0];
      try { if (file.type === 'image') { const b64 = await FileSystem.readAsStringAsync(file.uri, { encoding: (FileSystem as any).EncodingType.Base64 }); await Clipboard.setImageAsync(b64); } } catch (e) {}
      setPendingUploadPayload({ uri: file.uri, name: file.fileName || `media_${Date.now()}`, size: file.fileSize, type: file.type === 'video' ? 'Video' : 'Image' });
      setIsTargetModalVisible(true);
    }
  };
  const launchQRScanner = async () => { setIsCameraOptionsVisible(false); if (!cameraPermission?.granted) { const perm = await requestCameraPermission(); if (!perm.granted) { Alert.alert("Permission Required"); return; } } setIsQRScannerActive(true); };
  const handleBarcodeScanned = async ({ data }: { data: string }) => {
    setIsQRScannerActive(false);
    await Clipboard.setStringAsync(data);
    Platform.OS === 'android' ? ToastAndroid.show("Content copied", ToastAndroid.SHORT) : null;
    if (data.toLowerCase().startsWith('http://') || data.toLowerCase().startsWith('https://')) Linking.openURL(data).catch(() => {});
    setInputText(data);
  };

  // ─── Heavy Upload ───
  const CHUNK_SIZE = 50 * 1024 * 1024; // 50MB (under Cloudflare 100MB limit)

  const executeHeavyUpload = async (targetDeviceOrGlobal: any) => {
    if (!pendingUploadPayload) return;
    setIsTargetModalVisible(false);
    setIsSending(true);
    const { uri: physicalPath, name, size, type } = pendingUploadPayload;
    try {
      const safeName = `sync_${Date.now()}_` + name.replace(/[^a-zA-Z0-9.-]/g, '_');
      const hydratedPath = `${SYNC_CACHE_BASE}${safeName}`;
      await FileSystem.copyAsync({ from: physicalPath, to: hydratedPath });

      if (targetDeviceOrGlobal === 'Global') {
        // Firebase Storage path (100MB limit enforced)
        if (!isGlobalSyncEnabled) { Alert.alert("Global Sync Disabled"); setIsTargetModalVisible(false); setPendingUploadPayload(null); return; }
        if (size && size > 100 * 1024 * 1024) { Alert.alert("Too Large", "100MB limit for Firebase."); setIsSending(false); return; }
        const response = await fetch(hydratedPath); const blob = await response.blob();
        const sf = storageRef(storage, `archives/${name}_${Date.now()}`); await uploadBytesResumable(sf, blob); const downloadUrl = await getDownloadURL(sf);
        const newRef = push(ref(database, 'clipboard'));
        const ext = name.split('.').pop()?.toLowerCase() || '';
        await set(newRef, { Title: name, Type: (() => { if (type === 'Image' || type === 'Video') return type; if (['apk','zip','rar'].includes(ext)) return 'Archive'; if (['doc','docx','txt'].includes(ext)) return 'Document'; if (ext === 'pdf') return 'Pdf'; if (['mp4','avi','mkv'].includes(ext)) return 'Video'; if (['ppt','pptx'].includes(ext)) return 'Presentation'; if (['jpg','jpeg','png','gif','webp'].includes(ext)) return 'Image'; return 'File'; })(), Raw: downloadUrl, Time: new Date().toLocaleTimeString(), Timestamp: Date.now(), SourceDeviceName: deviceName || 'Unknown Mobile', SourceDeviceType: 'Mobile' });
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
    } catch (err: any) { Alert.alert('Upload Failed', err.message); }
    setIsSending(false);
    setPendingUploadPayload(null);
  };

  // ─── Clip Visibility Filter ───
  const clipFilter = (c: ClipItem) => {
    const isVisible = (c.IsPinned || (c.Timestamp || 0) >= localWipeTimestamp) && (!c.id || !localDeletedIds.has(c.id)) && (c.Raw || c.Title);
    if (!isVisible) return false;
    if ((c.Type === 'Image' || c.Type === 'ImageLink') && !c.Raw && !c.CachedUri) return false;
    if (!c.Raw && !c.Title) return false;
    return true;
  };

  // ════════════════════════════════════════════════════════
  // RENDER
  // ════════════════════════════════════════════════════════
  return (
    <SafeAreaView style={styles.container}>
      {/* Device Name Setup Modal */}
      <Modal visible={!deviceName && deviceName === ''} animationType="fade" transparent={true}>
        <View style={styles.modalOverlay}><View style={styles.modalContent}>
          <Text style={styles.modalTitle}>Name this Device</Text>
          <Text style={styles.modalSubtitle}>Identify this device in the AdvanceClip mesh network.</Text>
          <TextInput style={styles.modalInput} value={setupName} onChangeText={setSetupName} placeholder="e.g. Galaxy S23" placeholderTextColor="#4C5361" autoFocus />
          <TouchableOpacity style={styles.modalButton} onPress={() => { if(setupName.trim()) setDeviceName(setupName.trim()); }}><Text style={styles.modalButtonText}>Join Mesh</Text></TouchableOpacity>
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
            <View style={{marginLeft: 12}}><Text style={{color: '#FFF', fontSize: 16, fontWeight: '600'}}>Scan QR Code</Text><Text style={{color: '#8A8F98', fontSize: 12}}>Extracts text or opens valid links.</Text></View>
          </TouchableOpacity>
          <TouchableOpacity style={[styles.modalButton, {backgroundColor: '#2A2F3A', marginTop: 10}]} onPress={() => setIsCameraOptionsVisible(false)}><Text style={styles.modalButtonText}>Cancel</Text></TouchableOpacity>
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
            <Text style={styles.title}>Clipboard Sync</Text>
            <View style={styles.statusRow}><View style={[styles.indicator, { backgroundColor: '#4A62EB' }]} /><Text style={styles.statusText}>Cloud Active</Text></View>
          </View>
          <View style={{flexDirection: 'row', gap: 10}}>
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
              renderItem={({ item }) => {
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
                  <TouchableOpacity
                    style={[styles.clipCard, isMultiSelectMode && selectedItemIds.has(item.id || '') && { borderColor: '#4A62EB', borderWidth: 1.5 }]}
                    activeOpacity={0.8}
                    onPress={() => { if (isMultiSelectMode) toggleSelectItem(item.id || ''); else if (activeOptionsId === item.id) setActiveOptionsId(null); else setActiveOptionsId(item.id!); }}
                    onLongPress={() => { if (!isMultiSelectMode) { setIsMultiSelectMode(true); setSelectedItemIds(new Set([item.id || ''])); setActiveOptionsId(null); } }}
                  >
                    {isMultiSelectMode && (
                      <View style={{position: 'absolute', left: 8, top: 8, width: 24, height: 24, borderRadius: 12, backgroundColor: selectedItemIds.has(item.id || '') ? '#4A62EB' : 'rgba(255,255,255,0.1)', borderWidth: 2, borderColor: selectedItemIds.has(item.id || '') ? '#4A62EB' : '#4C5361', alignItems: 'center', justifyContent: 'center', zIndex: 10}}>
                        {selectedItemIds.has(item.id || '') && <IconSymbol name="checkmark" size={12} color="#FFF" />}
                      </View>
                    )}
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
                    {activeOptionsId === item.id && !(isIncomingTransfer && isHeavyFile) && (
                      <View style={{ position: 'absolute', right: 10, top: 10, flexDirection: 'row', backgroundColor: 'rgba(20,24,36,0.9)', borderRadius: 12, padding: 8, gap: 8 }}>
                        <TouchableOpacity onPress={async () => {
                          try {
                            if (!item.id) { ToastAndroid.show("Pinning is restricted to Global Cloud payloads.", ToastAndroid.SHORT); return; }
                            await update(ref(database, `clipboard/${item.id}`), { IsPinned: !item.IsPinned });
                            setClips(prev => prev.map(c => c.id === item.id ? {...c, IsPinned: !c.IsPinned} : c));
                            ToastAndroid.show(item.IsPinned ? "Unpinned" : "Pinned!", ToastAndroid.SHORT);
                          } catch(e) {}
                        }} style={[styles.actionBtnIcon, {backgroundColor: item.IsPinned ? '#F59E0B33' : '#2A2F3A'}]}>
                          <IconSymbol name={item.IsPinned ? "pin.fill" : "pin"} size={18} color={item.IsPinned ? "#F59E0B" : "#8A8F98"} />
                        </TouchableOpacity>
                        <TouchableOpacity onPress={async () => {
                          const contentStr = item.Raw || item.Title || '';
                          if (item.Type === 'Image' || item.Type === 'ImageLink') {
                            try { const src = mediaUrl || item.CachedUri || item.Raw; if (src) { const localUri = `${SYNC_CACHE_BASE}copy_${Date.now()}.png`; const dl = await FileSystem.downloadAsync(src, localUri, { headers: { 'X-Advance-Client': 'MobileCompanion' } }); const b64 = await FileSystem.readAsStringAsync(dl.uri, { encoding: (FileSystem as any).EncodingType.Base64 }); await Clipboard.setImageAsync(b64); if (Platform.OS === 'android') ToastAndroid.show("Image Copied", ToastAndroid.SHORT); } } catch(e) { await Clipboard.setStringAsync(contentStr); if (Platform.OS === 'android') ToastAndroid.show("URL Copied", ToastAndroid.SHORT); }
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
                        {isPdf && mediaUrl.startsWith('http') && (() => {
                          const safeName = (item.Title || `file_${Date.now()}`).replace(/[^a-zA-Z0-9.-]/g, '_');
                          const localUri = DOWNLOAD_BASE + safeName;
                          const dlId = item.id || safeName;
                          const prog = downloadProgress[dlId];
                          const dlNow = prog !== undefined && prog < 1;
                          const dlDone = downloadedItems.has(item.id!) || (prog !== undefined && prog >= 1);
                          const doPdfDownload = async () => {
                            const fi = await FileSystem.getInfoAsync(localUri);
                            if (fi.exists) { setDownloadedItems(prev => { const u = new Set(prev); u.add(item.id!); return u; }); setDownloadProgress(p => ({...p, [dlId]: 1})); return localUri; }
                            setDownloadProgress(p => ({...p, [dlId]: 0}));
                            const res = FileSystem.createDownloadResumable(mediaUrl, localUri, { headers: { 'X-Advance-Client': 'MobileCompanion' } }, (dp) => { const pct = dp.totalBytesExpectedToWrite > 0 ? dp.totalBytesWritten / dp.totalBytesExpectedToWrite : 0; setDownloadProgress(p => ({...p, [dlId]: pct})); });
                            const result = await res.downloadAsync();
                            setDownloadProgress(p => ({...p, [dlId]: 1})); setDownloadedItems(prev => { const u = new Set(prev); u.add(item.id!); return u; });
                            return result?.uri || localUri;
                          };
                          return (<>
                            <TouchableOpacity onPress={async () => { try { const uri = await doPdfDownload(); const contentUri = await FileSystem.getContentUriAsync(uri); await IntentLauncher.startActivityAsync('android.intent.action.VIEW', { data: contentUri, flags: 1 }); } catch(e) { ToastAndroid.show('Could not open PDF', ToastAndroid.SHORT); } }} style={[styles.actionBtnIcon, {backgroundColor: dlDone ? '#10B98133' : '#EF444433'}]}>
                              {dlNow ? <ActivityIndicator size="small" color="#EF4444" /> : <IconSymbol name={dlDone ? "doc.fill" : "arrow.down"} size={18} color={dlDone ? "#10B981" : "#EF4444"} />}
                            </TouchableOpacity>
                          </>);
                        })()}
                        <TouchableOpacity onPress={async () => {
                          if (!item.id) return;
                          setLocalDeletedIds(prev => { const n = new Set(prev); n.add(item.id!); AsyncStorage.setItem('localDeletedIds', JSON.stringify([...n])).catch(() => {}); return n; });
                          if (isGlobalSyncEnabled) { try { await remove(ref(database, `clipboard/${item.id}`)); } catch(e) {} }
                          setActiveOptionsId(null);
                          if (Platform.OS === 'android') ToastAndroid.show("Deleted", ToastAndroid.SHORT);
                        }} style={[styles.actionBtnIcon, {backgroundColor: '#EF444433'}]}>
                          <IconSymbol name="trash" size={18} color="#EF4444" />
                        </TouchableOpacity>
                      </View>
                    )}
                  </TouchableOpacity>
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
  );
}
