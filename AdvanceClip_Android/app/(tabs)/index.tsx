import React, { useState, useEffect, useRef } from 'react';
import { StyleSheet, View, Text, TextInput, TouchableOpacity, FlatList, ActivityIndicator, KeyboardAvoidingView, Platform, Alert, AppState, AppStateStatus, Modal, ToastAndroid, NativeModules, ScrollView } from 'react-native';
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


type ClipItem = {
  id?: string;
  Title: string;
  Type: string;
  Raw: string;
  Time: string;
  SourceDeviceName?: string;
  SourceDeviceType?: string;
  IsPinned?: boolean;
  Timestamp?: number;
};

const fetchWithTimeout = async (url: string, options: any = {}, timeoutMs = 2500) => {
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

const { AdvanceOverlay } = NativeModules;

// Helper: extract subnet prefix from IP (first 3 octets)
const getSubnet = (ip: string): string => {
  if (!ip) return '';
  const clean = ip.replace(/^https?:\/\//, '').split(':')[0];
  const parts = clean.split('.');
  return parts.length >= 3 ? `${parts[0]}.${parts[1]}.${parts[2]}` : '';
};

// Helper: determine connection type for a device relative to this phone
const getConnectionType = (device: any, myLocalIp: string): 'Local' | 'Cloud' | 'P2P' => {
  const deviceIp = device.LocalIp || device.Url || '';
  const mySubnet = getSubnet(myLocalIp);
  const deviceSubnet = getSubnet(deviceIp);
  if (mySubnet && deviceSubnet && mySubnet === deviceSubnet) return 'Local';
  return 'Cloud';
};

const connectionColors: Record<string, string> = { Local: '#10B981', Cloud: '#F59E0B', P2P: '#06B6D4' };

// ─── CachedImage: Downloads remote images to local cache for reliable rendering ───
const safeHash = (s: string): string => {
  let h = 0;
  for (let i = 0; i < s.length; i++) {
    h = ((h * 31) + s.charCodeAt(i)) & 0x7fffffff; // keep positive 31-bit
  }
  return h.toString(16);
};

const CachedImage = React.memo(({ imgUri, onPress }: { imgUri: string; onPress: () => void }) => {
  const [localUri, setLocalUri] = React.useState<string | null>(null);
  const [errMsg, setErrMsg] = React.useState<string | null>(null);

  React.useEffect(() => {
    if (!imgUri) { setErrMsg('No URL'); return; }

    // Local file:// or absolute path — use directly, no download needed
    if (imgUri.startsWith('file://') || imgUri.startsWith('/')) {
      const uri = imgUri.startsWith('file://') ? imgUri : `file://${imgUri}`;
      setLocalUri(uri);
      return;
    }

    // Remote http:// — download to stable cache file
    if (imgUri.startsWith('http')) {
      const fname = `img_${safeHash(imgUri)}.jpg`;
      const cacheUri = (FileSystem as any).cacheDirectory + fname;

      (async () => {
        try {
          // Use cached version if it's valid (>500 bytes)
          const info = await FileSystem.getInfoAsync(cacheUri);
          if (info.exists && (info as any).size > 500) {
            setLocalUri(cacheUri);
            return;
          }
          // Download fresh
          const dl = await FileSystem.downloadAsync(imgUri, cacheUri, {
            headers: { 'X-Advance-Client': 'MobileCompanion' }
          });
          if (dl.status === 200) {
            setLocalUri(dl.uri);
          } else {
            // Retry once with direct URL as source
            const dl2 = await FileSystem.downloadAsync(imgUri, cacheUri, {
              headers: { 'X-Advance-Client': 'MobileCompanion' }
            });
            if (dl2.status === 200) setLocalUri(dl2.uri);
            else setErrMsg(`HTTP ${dl2.status}`);
          }
        } catch (e: any) {
          setErrMsg(e?.message || 'Download failed');
        }
      })();
      return;
    }

    setErrMsg('Unsupported URI');
  }, [imgUri]);

  if (errMsg) return (
    <View style={{ marginBottom: 8, height: 80, borderRadius: 12, backgroundColor: '#1C202B', justifyContent: 'center', alignItems: 'center', padding: 8 }}>
      <IconSymbol name="photo.fill" size={24} color="#4C5361" />
      <Text style={{ color: '#8A8F98', fontSize: 10, marginTop: 4, textAlign: 'center' }}>
        Image unavailable{'\n'}<Text style={{ color: '#4C5361', fontSize: 9 }}>{errMsg}</Text>
      </Text>
    </View>
  );
  if (!localUri) return (
    <View style={{ marginBottom: 8, height: 80, borderRadius: 12, backgroundColor: '#1C202B', justifyContent: 'center', alignItems: 'center' }}>
      <ActivityIndicator size="small" color="#4A62EB" />
      <Text style={{ color: '#8A8F98', fontSize: 10, marginTop: 4 }}>Loading image...</Text>
    </View>
  );
  return (
    <TouchableOpacity style={{ marginBottom: 8 }} onPress={onPress} activeOpacity={0.85}>
      <Image
        source={{ uri: localUri }}
        style={{ width: '100%', minHeight: 160, maxHeight: 320, borderRadius: 12, backgroundColor: '#1C202B' }}
        contentFit="contain"
      />
    </TouchableOpacity>
  );
});

export default function SyncScreen() {
  const { pcLocalIp, deviceName, setDeviceName, isGlobalSyncEnabled, setGlobalSyncEnabled, isFloatingBallEnabled } = useSettings();
  
  useEffect(() => {
    if (Platform.OS === 'android' && AdvanceOverlay && isFloatingBallEnabled) {
      AdvanceOverlay.startOverlay();
    }
  }, [isFloatingBallEnabled]);



  // Ghost Wipe Filter State
  const [localWipeTimestamp, setLocalWipeTimestamp] = useState<number>(0);
  const [localDeletedIds, setLocalDeletedIds] = useState<Set<string>>(new Set());

  const [clips, setClips] = useState<ClipItem[]>([]);
  const lastSyncedContentRef = useRef<string>('');
  const lastSyncedImageTsRef = useRef<number>(0);
  // Track items THIS device has sent to prevent echo-back loops
  const sentContentFingerprintsRef = useRef<Set<string>>(new Set());
  // Cross-channel dedup: fingerprint → timestamp. If local sync sees it first, Firebase skips auto-copy (and vice versa).
  const recentSyncFingerprintsRef = useRef<Map<string, number>>(new Map());

  // ═══ URL CACHE: Avoid redundant health checks on every poll cycle ═══
  const cachedPcUrlRef = useRef<string | null>(null);
  const cachedPcUrlTimestampRef = useRef<number>(0);
  const URL_CACHE_TTL = 30_000; // Re-validate every 30 seconds

  const getCachedPcUrl = async (): Promise<string> => {
    const now = Date.now();
    // Return cached URL if still fresh
    if (cachedPcUrlRef.current && (now - cachedPcUrlTimestampRef.current) < URL_CACHE_TTL) {
      return cachedPcUrlRef.current;
    }
    // Re-resolve
    const activePc = activeDevices.find(d => d.DeviceType === 'PC');
    if (activePc) {
      const resolved = await resolveOptimalUrl(activePc);
      if (resolved) {
        cachedPcUrlRef.current = resolved;
        cachedPcUrlTimestampRef.current = now;
        return resolved;
      }
    }
    return `http://${pcLocalIp}`;
  };

  // Throttle syncNativeDB to max once per 500ms to avoid excessive native bridge calls
  const lastNativeSyncRef = useRef<number>(0);
  useEffect(() => {
    if (Platform.OS === 'android' && AdvanceOverlay && isFloatingBallEnabled) {
      const now = Date.now();
      if (now - lastNativeSyncRef.current < 500) return; // Throttle
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
               rawData = (FileSystem as any).documentDirectory + safeName;
           }
           return {
               Title: c.Title,
               Raw: rawData || '',
               Type: c.Type || 'Text',
               SourceDeviceName: c.SourceDeviceName || 'Cloud',
               Timestamp: c.Timestamp,
               DownloadUrl: c.Raw?.startsWith?.('http') ? c.Raw : '',
           };
         });
         try { AdvanceOverlay.syncNativeDB(JSON.stringify(mapped)); } catch(e) {}
       } else {
         try { AdvanceOverlay.syncNativeDB("[]"); } catch(e) {}
       }
    }
  }, [clips, isFloatingBallEnabled, localWipeTimestamp, localDeletedIds]);

  // Bidirectional sync: detect copies from the floating ball and inject back into app
  useEffect(() => {
    if (Platform.OS !== 'android' || !AdvanceOverlay || !isFloatingBallEnabled || !deviceName) return;
    const pollInterval = setInterval(async () => {
      try {
        const copiedText = await AdvanceOverlay.getLastCopiedFromOverlay();
        if (copiedText && copiedText.trim().length > 0) {
          const newItem: ClipItem = {
            Title: copiedText.substring(0, 80),
            Type: 'Text',
            Raw: copiedText,
            Time: new Date().toLocaleString(),
            SourceDeviceName: deviceName,
            SourceDeviceType: 'Mobile',
            Timestamp: Date.now(),
          };
          setClips(prev => [newItem, ...prev]);
          // Push to Firebase for cross-device sync
          if (isGlobalSyncEnabled) {
            try {
              const clipRef = push(ref(database, 'global_clipboard'));
              await set(clipRef, newItem);
            } catch(e) {}
          }
        }
      } catch(e) {}
    }, 1500);
    return () => clearInterval(pollInterval);
  }, [isFloatingBallEnabled, deviceName, isGlobalSyncEnabled]);

  // Mesh Network States (declared early for dependency usage)
  const [activeDevices, setActiveDevices] = useState<any[]>([]);

  // Screenshot detection: poll the native ScreenshotObserver for new captures
  const lastScreenshotTsRef = useRef<number>(0);
  useEffect(() => {
    if (Platform.OS !== 'android' || !AdvanceOverlay) return;
    const screenshotPoll = setInterval(async () => {
      try {
        const result = await AdvanceOverlay.getLatestScreenshot();
        if (result && result.path && result.timestamp > lastScreenshotTsRef.current) {
          lastScreenshotTsRef.current = result.timestamp;
          
          const screenshotItem: ClipItem = {
            Title: result.name || 'Screenshot',
            Type: 'Image',
            Raw: result.path,
            Time: new Date().toLocaleString(),
            SourceDeviceName: deviceName || 'Phone',
            SourceDeviceType: 'Mobile',
            Timestamp: Date.now(),
          };

          // Add to clip feed
          setClips(prev => [screenshotItem, ...prev]);
          if (Platform.OS === 'android') ToastAndroid.show(`\ud83d\udcf8 Screenshot detected!`, ToastAndroid.SHORT);

          // Auto-copy the actual image to clipboard (not the path)
          try {
            const imageUri = result.path.startsWith('file://') ? result.path : `file://${result.path}`;
            const base64 = await FileSystem.readAsStringAsync(imageUri, { encoding: FileSystem.EncodingType.Base64 });
            await Clipboard.setImageAsync(base64);
          } catch(e) {
            // Fallback: at least copy the path
            await Clipboard.setStringAsync(result.path).catch(() => {});
          }

          // Upload to Firebase Storage and sync globally
          if (isGlobalSyncEnabled) {
            try {
              const fileUri = result.path.startsWith('file://') ? result.path : `file://${result.path}`;
              const fileName = result.name || `screenshot_${Date.now()}.png`;
              const storagePath = `clipboard_images/${fileName}`;
              const sRef = storageRef(storage, storagePath);
              const fileResp = await fetch(fileUri);
              const blob = await fileResp.blob();
              await uploadBytesResumable(sRef, blob);
              const downloadURL = await getDownloadURL(sRef);
              
              // Update the clip item with the actual download URL (not local path)
              screenshotItem.Raw = downloadURL;
              const clipRef = push(ref(database, 'clipboard'));
              await set(clipRef, screenshotItem);
              
              // Also update in local clips list
              setClips(prev => prev.map(c => c === screenshotItem ? { ...c, Raw: downloadURL } : c));
            } catch(e) {}
          }

          // Send to local PC via sync_file endpoint  
          try {
            const optimal = await getCachedPcUrl();
            if (optimal) {
                const fileUri = result.path.startsWith('file://') ? result.path : `file://${result.path}`;
                const fileResp2 = await fetch(fileUri);
                const blob2 = await fileResp2.blob();
                await fetchWithTimeout(`${optimal}/api/sync_file`, {
                  method: 'POST',
                  headers: { 
                    'X-Advance-Client': 'MobileCompanion',
                    'X-Source-Device': deviceName || 'Phone',
                    'X-File-Name': encodeURIComponent(result.name || 'screenshot.png'),
                    'X-File-Type': 'Image',
                    'Content-Type': 'application/octet-stream',
                  },
                  body: blob2,
                }, 15000);
                if (Platform.OS === 'android') ToastAndroid.show(`📸 Screenshot sent to PC!`, ToastAndroid.SHORT);
            }
          } catch(e) {}
        }
      } catch(e) {}
    }, 3000);
    return () => clearInterval(screenshotPoll);
  }, [deviceName, isGlobalSyncEnabled, activeDevices]);

  const [isRefreshing, setIsRefreshing] = useState(false);
  const [inputText, setInputText] = useState('');
  const [isSending, setIsSending] = useState(false);
  const [lastCopiedText, setLastCopiedText] = useState('');
  const [setupName, setSetupName] = useState('');
  
  // Mesh Network States
  // activeDevices declared above (before screenshot poll useEffect)
  const [isTargetModalVisible, setIsTargetModalVisible] = useState(false);
  const [pendingUploadPayload, setPendingUploadPayload] = useState<any>(null);
  const [downloadedItems, setDownloadedItems] = useState<Set<string>>(new Set());
  const [downloadProgress, setDownloadProgress] = useState<{[key: string]: number}>({});
  // Incoming file transfer progress: tracks background auto-downloads from PC sync
  const [incomingTransferProgress, setIncomingTransferProgress] = useState<{[key: string]: number}>({});
  
  useEffect(() => {
    AsyncStorage.getItem('localWipeTimestamp').then(val => {
      if (val) {
        setLocalWipeTimestamp(parseInt(val));
      } else {
        // FIRST INSTALL: Seed the wipe timestamp to NOW so old Firebase junk doesn't flood the feed.
        const now = Date.now();
        setLocalWipeTimestamp(now);
        AsyncStorage.setItem('localWipeTimestamp', now.toString());
      }
    });
    AsyncStorage.getItem('localDeletedIds').then(val => {
      if (val) {
         try {
             setLocalDeletedIds(new Set(JSON.parse(val)));
         } catch(e) {}
      }
    });
  }, []);

  // PEER RELAY ENGINE (Android -> Android)
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
               // Execute concurrent parallel background downloads into app cache
               ToastAndroid.show(`Incoming batch transfer from ${batch.sender}...`, ToastAndroid.LONG);
               
               try {
                  const perm = await MediaLibrary.requestPermissionsAsync();
                  if (perm.status === 'granted') {
                      await Promise.all(batch.urls.map(async (url: string, idx: number) => {
                          const localUri = `${(FileSystem as any).cacheDirectory}relayed_${Date.now()}_${idx}.jpg`;
                          const dl = await FileSystem.downloadAsync(url, localUri, { headers: { 'X-Advance-Client': 'MobileCompanion' } });
                          const asset = await MediaLibrary.createAssetAsync(dl.uri);
                          await MediaLibrary.createAlbumAsync("AdvanceClip Extractions", asset, false);
                      }));
                      ToastAndroid.show("Extraction successful: Saved to Native Gallery ✅", ToastAndroid.LONG);
                  }
               } catch (e) {
                  ToastAndroid.show("Failed to relay items to Gallery.", ToastAndroid.SHORT);
               }
               // Mark as consumed
               updates[key] = null;
            }
         }
         
         if (Object.keys(updates).length > 0) {
            await update(ref(database, `peer_transfers/${deviceName}`), updates);
         }
      }
    });
    return () => unsubscribePeer();
  }, [deviceName]);

  const [isCameraOptionsVisible, setIsCameraOptionsVisible] = useState(false);
  const [isQRScannerActive, setIsQRScannerActive] = useState(false);
  const [cameraPermission, requestCameraPermission] = useCameraPermissions();

  // Expanded Image Viewer
  const [expandedImage, setExpandedImage] = useState<string | null>(null);

  // Auto Screenshot Observer
  const [lastScannedImageId, setLastScannedImageId] = useState<string | null>(null);

  // Auto-Copy Text Injection State
  const [latestIngestedId, setLatestIngestedId] = useState<string | null>(null);

  // Visible Actions UI State
  const [activeOptionsId, setActiveOptionsId] = useState<string | null>(null);

  // Multi-Select Mode
  const [isMultiSelectMode, setIsMultiSelectMode] = useState(false);
  const [selectedItemIds, setSelectedItemIds] = useState<Set<string>>(new Set());

  // PDF Merge Modal
  const [isMergeModalVisible, setIsMergeModalVisible] = useState(false);
  const [mergeQueue, setMergeQueue] = useState<ClipItem[]>([]);

  // Forced Sync Modal
  const [isForceSyncModalVisible, setIsForceSyncModalVisible] = useState(false);
  const [forceSyncDevices, setForceSyncDevices] = useState<any[]>([]);

  const resolveOptimalUrl = async (targetDeviceOrGlobal: any) => {
    if (!targetDeviceOrGlobal || targetDeviceOrGlobal === 'Global') return null;

    // Build ordered list of URLs to try: Url first, then LocalIp entries, then GlobalUrl
    const candidates: string[] = [];
    if (targetDeviceOrGlobal.Url && targetDeviceOrGlobal.Url.startsWith('http')) {
      candidates.push(targetDeviceOrGlobal.Url.endsWith('/') ? targetDeviceOrGlobal.Url.slice(0, -1) : targetDeviceOrGlobal.Url);
    }
    if (targetDeviceOrGlobal.LocalIp) {
       targetDeviceOrGlobal.LocalIp.split(',').forEach((ip: string) => {
           let cleanIp = ip.trim();
           // Add http:// prefix if missing
           if (!cleanIp.startsWith('http')) cleanIp = `http://${cleanIp}`;
           // Add :8999 port if no port specified (raw IP like 192.168.1.106)
           // Check if there's a port after the host (e.g., :8999) by looking after stripping protocol
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
            const res = await fetchWithTimeout(`${url}/api/health`, { 
                method: 'GET',
                headers: { 'X-Advance-Client': 'MobileCompanion' }
            }, 1500);
            if (res.ok) return url;
        } catch(e) {}
    }

    // Final fallback: return first candidate even if health check failed
    return candidates.length > 0 ? candidates[0] : null;
  };

  const getMediaUrl = (item: any) => {
    // If any field is already a full URL, use it directly
    if (item.Raw && item.Raw.startsWith('http')) return item.Raw;
    if (item.DownloadUrl && item.DownloadUrl.startsWith('http')) return item.DownloadUrl;
    if (item.PreviewUrl && item.PreviewUrl.startsWith('http')) return item.PreviewUrl;
    if (item.CachedUri && (item.CachedUri.startsWith('file://') || item.CachedUri.startsWith('/'))) return item.CachedUri;

    // Relative URL — build base from known sources
    const relUrl = item.PreviewUrl || item.DownloadUrl || item.Raw || '';
    if (!relUrl) return '';

    // 1. Try activeDevices PC node first
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

    // 2. Always fall back to pcLocalIp from settings — most reliable
    if (pcLocalIp) {
      const rawIp = pcLocalIp.trim().replace(/\/$/, '');
      const base = rawIp.startsWith('http') ? rawIp : `http://${rawIp.includes(':') ? rawIp : rawIp + ':8999'}`;
      return `${base}${relUrl.startsWith('/') ? relUrl : '/' + relUrl}`;
    }

    return relUrl;
  };

  const fetchLocalClips = async () => {
    setIsRefreshing(true);
    // Use cached URL for instant resolution
    const targetUrl = await getCachedPcUrl();
    
    try {
      const response = await fetchWithTimeout(`${targetUrl}/api/sync`, {
          headers: { 'X-Advance-Client': 'MobileCompanion' }
      }, 2500);
      if (response.ok) {
        const data: any[] = await response.json();
        // Enrich images: convert relative PreviewUrl/DownloadUrl to absolute using targetUrl
        const enriched = data.map(item => {
          if (item.Type === 'Image' || item.Type === 'ImageLink' || item.Type === 'QRCode') {
            return {
              ...item,
              PreviewUrl: item.PreviewUrl?.startsWith('/') ? `${targetUrl}${item.PreviewUrl}` : item.PreviewUrl,
              DownloadUrl: item.DownloadUrl?.startsWith('/') ? `${targetUrl}${item.DownloadUrl}` : item.DownloadUrl,
              Raw: item.Raw?.startsWith('/') ? `${targetUrl}${item.Raw}` : item.Raw,
            };
          }
          return item;
        });
        setClips(enriched);
      }
    } catch (error) {
      // Invalidate cache on failure
      cachedPcUrlRef.current = null;
    }
    setIsRefreshing(false);
  };

  useEffect(() => {
    // Listen to Firebase directly for the unified feed
    const clipsRef = query(ref(database, 'clipboard'), orderByChild('Timestamp'), limitToLast(30));
    const unsubscribeFeed = onValue(clipsRef, (snapshot) => {
      if (snapshot.exists()) {
        const data = snapshot.val();
        const parsed = Object.keys(data).map(k => ({ id: k, ...data[k] })).reverse();
        
        // Clean stale fingerprints (older than 30s)
        const now = Date.now();
        recentSyncFingerprintsRef.current.forEach((ts, fp) => {
          if (now - ts > 30_000) recentSyncFingerprintsRef.current.delete(fp);
        });
        
        // Push only latest text clips to Native Overlay Database (max 5), skip if already synced locally
        if (Platform.OS === 'android' && AdvanceOverlay) {
           parsed.slice(0, 5).forEach((c: any) => {
               const fp = `${c.Type}::${(c.Raw || '').substring(0, 150)}`;
               if (recentSyncFingerprintsRef.current.has(fp)) return; // Already received via local sync
               if ((c.Type === 'Text' || c.Type === 'Url' || c.Type === 'Pdf' || c.Type === 'Document') && c.Raw) {
                   let rawData = c.Raw;
                   if (c.Type === 'Pdf' || c.Type === 'Document') {
                       const safeName = c.Title.replace(/[^a-zA-Z0-9.-]/g, '_');
                       rawData = (FileSystem as any).documentDirectory + safeName;
                   }
                   AdvanceOverlay.pushClipToNativeDB(rawData, c.SourceDeviceName || 'Cloud');
                   // Register so local sync won't duplicate
                   recentSyncFingerprintsRef.current.set(fp, Date.now());
               }
           });
        }
        
        setClips(parsed);
      } else {
        setClips([]);
      }
    });

    // Listen to Mesh Topology — store ALL online devices, with direct LAN fallback
    const nodesRef = query(ref(database, 'active_devices'));
    const unsubscribeNodes = onValue(nodesRef, async (snapshot) => {
      let rawDevices: any[] = [];
      if (snapshot.exists()) {
        const data = snapshot.val();
        const now = Date.now();
        const STALE_TTL = 300_000; // 5 minutes
        rawDevices = Object.keys(data)
          .map(k => ({ ...data[k], _key: k }))
          .filter(d => d.IsOnline && d.Timestamp && (now - d.Timestamp) < STALE_TTL);
      }
      
      // Direct LAN probe fallback — always try pcLocalIp even if Firebase has no data
      const hasPc = rawDevices.some(d => d.DeviceType === 'PC');
      if (!hasPc && pcLocalIp) {
        try {
          const rawIp = pcLocalIp.trim();
          const baseIp = rawIp.replace(/^https?:\/\//, '').split(':')[0];
          const probeUrl = rawIp.startsWith('http') ? rawIp.replace(/\/$/, '') : `http://${rawIp.includes(':') ? rawIp : baseIp + ':8999'}`;
          const res = await fetch(`${probeUrl}/api/health`, { method: 'GET', headers: { 'X-Advance-Client': 'MobileCompanion' }, signal: AbortSignal.timeout(2000) });
          if (res.ok) {
            rawDevices.push({
              DeviceName: 'PC', DeviceType: 'PC', IsOnline: true,
              Url: probeUrl, LocalIp: probeUrl, _key: 'local_direct',
              Timestamp: Date.now()
            });
          }
        } catch(e) {}
      }
      
      setActiveDevices(rawDevices);
    });

    return () => { unsubscribeFeed(); unsubscribeNodes(); };
  }, []);

  useEffect(() => {
    const interval = setInterval(async () => {
      // Use cached URL — no health check on every poll cycle
      const targetUrl = await getCachedPcUrl();

      // Pass PC URL and device name to native service so background sync works even when RN is paused
      if (Platform.OS === 'android' && AdvanceOverlay && targetUrl) {
        try { AdvanceOverlay.setPcUrl(targetUrl); } catch(e) {}
        try { if (deviceName) AdvanceOverlay.setDeviceName(deviceName); } catch(e) {}
      }
      try {
        const response = await fetchWithTimeout(`${targetUrl}/api/sync`, { headers: { 'X-Advance-Client': 'MobileCompanion' } }, 1500);
        if (response.ok) {
          const data = await response.json();
          if (data && data.length > 0) {
             const latest = data[0];
             
             // Content-based dedup: skip if we already synced this exact content
             const contentKey = `${latest.Type}_${latest.Title}_${latest.Timestamp}`;
             if (contentKey !== lastSyncedContentRef.current) {
                lastSyncedContentRef.current = contentKey;

                // Register fingerprint for cross-channel dedup (blocks Firebase duplicate)
                const crossFp = `${latest.Type}::${(latest.Raw || '').substring(0, 150)}`;
                recentSyncFingerprintsRef.current.set(crossFp, Date.now());

                // ECHO-BACK PREVENTION: Skip if this item originated from this phone
               const rawFingerprint = (latest.Raw || '').substring(0, 200);
               const isOwnEcho = 
                 (latest.SourceDeviceName && deviceName && latest.SourceDeviceName === deviceName) ||
                 (latest.SourceDeviceType === 'Mobile') ||
                 sentContentFingerprintsRef.current.has(rawFingerprint);
               
               if (isOwnEcho) {
                 // This item came from us — don't copy it back, just skip
               } else if (latest.Type === 'Text' || latest.Type === 'Code' || latest.Type === 'Url') {
                 const latestRaw = latest.Raw;
                 if (latestRaw) {
                   const currentContent = await Clipboard.getStringAsync();
                   if (currentContent !== latestRaw) {
                     // Use native suppressed clipboard set to avoid triggering overlay listener loop
                     if (Platform.OS === 'android' && AdvanceOverlay) {
                       try { AdvanceOverlay.setClipboardSuppressed(latestRaw); } catch(e) {
                         await Clipboard.setStringAsync(latestRaw);
                       }
                     } else {
                       await Clipboard.setStringAsync(latestRaw);
                     }
                     setLastCopiedText(latestRaw); 
                     lastCopiedRef.current = latestRaw;
                     if (Platform.OS === 'android') ToastAndroid.show(`📋 ${latestRaw.substring(0, 40)}...`, ToastAndroid.SHORT);
                   }
                 }
               } else if (latest.Type === 'Image' || latest.Type === 'ImageLink' || latest.Type === 'QRCode') {
                 try {
                   let mediaUrl = '';
                   if (latest.DownloadUrl && latest.DownloadUrl.startsWith('/')) {
                     mediaUrl = `${targetUrl}${latest.DownloadUrl}`;
                   } else if (latest.PreviewUrl && latest.PreviewUrl.startsWith('/')) {
                     mediaUrl = `${targetUrl}${latest.PreviewUrl}`;
                   } else if (latest.Raw && latest.Raw.startsWith('http')) {
                     mediaUrl = latest.Raw;
                   }
                   
                   if (mediaUrl) {
                     const localUri = `${(FileSystem as any).cacheDirectory}clip_sync_${Date.now()}.png`;
                     const { uri } = await FileSystem.downloadAsync(mediaUrl, localUri, { headers: { 'X-Advance-Client': 'MobileCompanion' } });
                     const b64 = await FileSystem.readAsStringAsync(uri, { encoding: (FileSystem as any).EncodingType.Base64 });
                     await Clipboard.setImageAsync(b64);
                     if (Platform.OS === 'android') ToastAndroid.show(`🖼️ Screenshot synced from PC!`, ToastAndroid.SHORT);
                   }
                 } catch (imgErr) {}
               } else if (latest.Type === 'Pdf' || latest.Type === 'Document' || latest.Type === 'File' || latest.Type === 'Video' || latest.Type === 'Audio' || latest.Type === 'Archive' || latest.Type === 'Presentation') {
                 // Don't auto-download files — they'll appear as cards with download buttons
                 if (Platform.OS === 'android') ToastAndroid.show(`📁 ${latest.Title} — tap to download`, ToastAndroid.SHORT);
               }

               // Push to overlay floating ball (only non-own items)
               if (!isOwnEcho && Platform.OS === 'android' && AdvanceOverlay && isFloatingBallEnabled) {
                 try {
                   if (latest.Type === 'Image' || latest.Type === 'ImageLink' || latest.Type === 'QRCode') {
                     // For images: push full URL so floating ball can display the thumbnail
                     const imgRaw = latest.PreviewUrl?.startsWith('/') ? `${targetUrl}${latest.PreviewUrl}` 
                       : latest.DownloadUrl?.startsWith('/') ? `${targetUrl}${latest.DownloadUrl}`
                       : latest.Raw?.startsWith('http') ? latest.Raw : '';
                     if (imgRaw) AdvanceOverlay.pushClipToNativeDB(imgRaw, 'PC');
                   } else {
                     const rawForOverlay = latest.Raw || latest.Title || '';
                     if (rawForOverlay) AdvanceOverlay.pushClipToNativeDB(rawForOverlay, 'PC');
                   }
                 } catch(e) {}
               }
             }
          }
          setClips(current => {
             const merged = [...current];
             let changed = false;
             data.forEach((localItem: any) => {
                 if (!merged.find(m => m.id === localItem.id || (m.Title === localItem.Title && m.Raw === localItem.Raw))) {
                     merged.push(localItem);
                     changed = true;
                 }
             });
             if (changed) {
                 return merged.sort((a,b) => (b.Timestamp || 0) - (a.Timestamp || 0));
             }
             return current;
          });
        }
      } catch (e) {
        // If poll failed, invalidate URL cache so next cycle re-resolves
        cachedPcUrlRef.current = null;
      }
    }, 1000); // Poll every 1 second — URL is cached so this is lightweight
    return () => clearInterval(interval);
  }, [isGlobalSyncEnabled, activeDevices, pcLocalIp]);

  // ═══ DEVICE SELF-REGISTRATION: Register this Android device as active in Firebase mesh ═══
  useEffect(() => {
    if (!deviceName) return;
    const myDeviceId = `Mobile_${deviceName.replace(/[^a-zA-Z0-9_]/g, '_')}`;
    
    const registerSelf = async () => {
      try {
        await set(ref(database, `active_devices/${myDeviceId}`), {
          DeviceId: myDeviceId,
          DeviceName: deviceName,
          DeviceType: 'Mobile',
          IsOnline: true,
          Timestamp: Date.now(),
        });
      } catch(e) {}
    };

    registerSelf();
    // Heartbeat every 30s to stay visible
    const heartbeat = setInterval(registerSelf, 30000);

    // On unmount: only mark offline if floating ball is OFF
    // When floating ball is ON, the native overlay service keeps running in the background,
    // meaning the device is still actively listening and should stay marked as online.
    return () => {
      clearInterval(heartbeat);
      if (!isFloatingBallEnabled) {
        set(ref(database, `active_devices/${myDeviceId}/IsOnline`), false).catch(() => {});
      }
    };
  }, [deviceName, isFloatingBallEnabled]);

  const clearAllClips = async () => {
    const executeWipe = async () => {
         try {
            const now = Date.now();
            setLocalWipeTimestamp(now);
            AsyncStorage.setItem('localWipeTimestamp', now.toString()).catch(() => {});

            if (isGlobalSyncEnabled) {
                const updates: any = {};
                let deletedCount = 0;
                
                clips.forEach(item => {
                  if (!item.IsPinned) {
                      updates[item.id!] = null;
                      deletedCount++;
                  }
                });

                if (deletedCount > 0) {
                  await update(ref(database, 'clipboard'), updates);
                }
            }

            Platform.OS === 'android' ? ToastAndroid.show(`Clean slate natively.`, ToastAndroid.SHORT) : alert(`Wiped visually & globally.`);
         } catch(e) { console.error(e); }
    };

    if (Platform.OS === 'web') {
        if (window.confirm("Are you sure you want to permanently delete all unpinned items from the Global Mesh network?")) {
            await executeWipe();
        }
        return;
    }

    Alert.alert(
      "Clear Entire Clipboard",
      "Are you sure you want to permanently delete all unpinned items from the Global Mesh network?",
      [
        { text: "Cancel", style: "cancel" },
        { 
          text: "Delete All", 
          style: "destructive",
          onPress: executeWipe
        }
      ]
    );
  };

    // Keep a stable ref for tracking background copies without triggering endless listener reconstructs
    const lastCopiedRef = React.useRef(lastCopiedText);
    useEffect(() => { lastCopiedRef.current = lastCopiedText; }, [lastCopiedText]);

    const handleForegroundClipboardCheck = async () => {
        if (Platform.OS === 'web') return; // Background fetching is fundamentally denied by Web Sandboxes
        try {
            const hasText = await Clipboard.hasStringAsync();
            if (hasText) {
                const text = await Clipboard.getStringAsync();
                
                if (text && text !== lastCopiedRef.current) {
                    setLastCopiedText(text);
                    await transmitTextSecurely(text);
                }
            }
        } catch(e) { }
    };

    const handleForegroundMediaCheck = async () => {
        try {
            let perm = await MediaLibrary.getPermissionsAsync();
            if (perm.status !== 'granted') {
                perm = await MediaLibrary.requestPermissionsAsync();
                if (perm.status !== 'granted') return;
            }
            
            const media = await MediaLibrary.getAssetsAsync({
                first: 1,
                mediaType: ['photo'],
                sortBy: [[MediaLibrary.SortBy.creationTime, false]]
            });

            if (media.assets.length > 0) {
                const latest = media.assets[0];
                const isRecent = (Date.now() - latest.creationTime) < 2 * 60 * 1000;
                
                if (isRecent && latest.id !== lastScannedImageId) {
                    setLastScannedImageId(latest.id);
                    setIsSending(true);
                    try {
                        const assetInfo = await MediaLibrary.getAssetInfoAsync(latest.id);
                        if (assetInfo.localUri || assetInfo.uri) {
                            
                            // Dynamic PC Route Resolution
                            let targetUrl = `http://${pcLocalIp}`;
                            const activePc = activeDevices.find(d => d.DeviceType === 'PC' && d.Url);
                            if (activePc) targetUrl = (await resolveOptimalUrl(activePc)) ?? targetUrl;
                            
                            // Relay Natively to Local/Tunnel Hub Pipeline
                            let localSuccess = false;
                            try {
                                const upRes = await FileSystem.uploadAsync(`${targetUrl}/api/sync_file?name=${encodeURIComponent(assetInfo.filename || 'screenshot.jpg')}&type=ImageLink&sourceDevice=${encodeURIComponent(deviceName || 'Mobile')}`, assetInfo.localUri || assetInfo.uri, {
                                    httpMethod: 'POST',
                                    uploadType: 0 as any, // FileSystemUploadType.BINARY_CONTENT
                                    headers: { 
                                        'X-Original-Date': Date.now().toString(), 
                                        'X-Advance-Client': 'MobileCompanion'
                                    }
                                });
                                localSuccess = upRes.status === 200;
                            } catch(e) {}

                            // Globally Stream to Mesh if Enabled or Local Failed
                            if (!localSuccess && isGlobalSyncEnabled) {
                               const response = await fetch(assetInfo.localUri || assetInfo.uri);
                               const blob = await response.blob();
                               const sf = storageRef(storage, `archives/Screenshot_${Date.now()}.jpg`);
                               await uploadBytesResumable(sf, blob);
                               const downloadUrl = await getDownloadURL(sf);
                               
                               const newRef = push(ref(database, 'clipboard'));
                               await set(newRef, {
                                 Title: `Screenshot_${Date.now()}.jpg`, Type: 'ImageLink', Raw: downloadUrl,
                                 Time: new Date().toLocaleTimeString(), Timestamp: Date.now(),
                                 SourceDeviceName: deviceName || 'Mobile', SourceDeviceType: 'Mobile'
                               });
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
        handleForegroundClipboardCheck();
        handleForegroundMediaCheck();

        const subscription = AppState.addEventListener('change', (nextAppState: AppStateStatus) => {
            if (nextAppState === 'active') {
                handleForegroundClipboardCheck();
                handleForegroundMediaCheck();
            }
        });

        // Periodic screenshot poll every 5s — more reliable than MediaLibrary.addListener on many Android builds
        let screenshotPollInterval: ReturnType<typeof setInterval> | null = null;
        if (Platform.OS !== 'web') {
            screenshotPollInterval = setInterval(() => {
                handleForegroundMediaCheck();
            }, 3000); // Poll every 3s for faster screenshot sync
        }

        // Natively bind to active OS media sweeps for organic background screenshots
        let mediaSub: any = null;
        if (Platform.OS !== 'web' && typeof MediaLibrary.addListener === 'function') {
            mediaSub = MediaLibrary.addListener((event) => {
                if (event.hasIncrementalChanges || (event as any).insertedMedia?.length > 0) {
                    handleForegroundMediaCheck();
                }
            });
        }

        return () => { 
            subscription.remove(); 
            if (mediaSub) mediaSub.remove(); 
            if (screenshotPollInterval) clearInterval(screenshotPollInterval);
        };
    }, []);

  // Auto-Copy Hook for Incoming Keyboard Text Natively
  useEffect(() => {
    if (clips.length === 0) return;
    const latest = clips[0];
    
    // Validate it's extremely new and originally pushed from another device natively
    if (latest.id !== latestIngestedId) {
       setLatestIngestedId(latest.id!);
       
       if (latest.SourceDeviceName !== deviceName) {
           if (Platform.OS === 'web') return; // Web sandboxes inherently restrict background injections without clicks
           const executeAutoCopy = async () => {
               try {
                   if (latest.Type === 'Text' || latest.Type === 'Url' || latest.Type === 'Code') {
                       const currentClip = await Clipboard.getStringAsync();
                       if (currentClip !== latest.Raw) {
                           await Clipboard.setStringAsync(latest.Raw);
                           setLastCopiedText(latest.Raw);
                           lastCopiedRef.current = latest.Raw;
                           Platform.OS === 'android' && ToastAndroid.show("Copied Natively", ToastAndroid.SHORT);
                       }
                   } else if (latest.Type === 'Image' || latest.Type === 'ImageLink') {
                       const mediaUrl = getMediaUrl(latest);
                       if (mediaUrl) {
                           const { uri } = await FileSystem.downloadAsync(mediaUrl, (FileSystem as any).cacheDirectory + 'clip_sync_global.png', { headers: { 'X-Advance-Client': 'MobileCompanion' } });
                           const b64 = await FileSystem.readAsStringAsync(uri, { encoding: (FileSystem as any).EncodingType.Base64 });
                           await Clipboard.setImageAsync(b64);
                           Platform.OS === 'android' && ToastAndroid.show("Image Copied Natively", ToastAndroid.SHORT);
                       }
                   }
               } catch (e) { }
           };
           executeAutoCopy();
       }
    }
  }, [clips, deviceName, latestIngestedId]);

  // Auto-Download Hook for Rich Media
  useEffect(() => {
    if (clips.length === 0) return;
    
    clips.forEach(async (item) => {
      if (!item.id || downloadedItems.has(item.id)) return;

      const autoTargetTypes = ['ImageLink', 'Image', 'Pdf', 'Document', 'Archive', 'Video', 'File', 'Presentation'];
      const mediaUrl = getMediaUrl(item);
      if (autoTargetTypes.includes(item.Type) && mediaUrl.startsWith('http')) {
        try {
          if (Platform.OS === 'web') return; // FileSystem operations evaluate to null and fatally crash React Native Web

          // File Name normalizer
          const safeName = item.Title.replace(/[^a-zA-Z0-9.-]/g, '_');
          const localUri = (FileSystem as any).documentDirectory + safeName;
          const transferId = item.id || safeName;
          
          const fileInfo = await FileSystem.getInfoAsync(localUri);
          if (fileInfo.exists) {
            setDownloadedItems(prev => new Set(prev).add(item.id!));
            setIncomingTransferProgress(p => { const n = {...p}; delete n[transferId]; return n; });
            return;
          }

          // Skip auto-download for APKs — user must manually trigger install
          const lowerTitle = (item.Title || '').toLowerCase();
          if (lowerTitle.endsWith('.apk')) return;

          // Verify Size Limits safely
          try {
             const headRes = await fetch(mediaUrl, { method: 'HEAD', headers: { 'X-Advance-Client': 'MobileCompanion' } });
             const sizeStr = headRes.headers.get('content-length');
             if (sizeStr) {
                const sizeBytes = parseInt(sizeStr);
                const isLocalRoute = !mediaUrl.includes('firebasestorage.googleapis.com');
                
                // Skip auto-download for files >100MB over cloud — leave DOWNLOAD button visible
                if (!isLocalRoute && sizeBytes > 100 * 1024 * 1024) { 
                  console.log("Global File > 100MB, skipped auto-download.");
                  return;  // DON'T mark as downloaded — DOWNLOAD button stays visible
                }
             }
          } catch(e) { }

          // Use resumable download with progress tracking for all incoming files
          setIncomingTransferProgress(p => ({...p, [transferId]: 0}));
          const resumable = FileSystem.createDownloadResumable(
            mediaUrl, localUri,
            { headers: { 'X-Advance-Client': 'MobileCompanion' } },
            (dp) => {
              const pct = dp.totalBytesExpectedToWrite > 0 
                ? dp.totalBytesWritten / dp.totalBytesExpectedToWrite : 0;
              setIncomingTransferProgress(p => ({...p, [transferId]: pct}));
            }
          );
          await resumable.downloadAsync();
          setIncomingTransferProgress(p => { const n = {...p}; delete n[transferId]; return n; });
          
          if (item.Type === 'ImageLink' || item.Type === 'Image') {
             try { 
                 const perm = await MediaLibrary.requestPermissionsAsync();
                 if (perm.status === 'granted') {
                     await MediaLibrary.saveToLibraryAsync(localUri); 
                 }
             } catch (err) { }
          }
          
        } catch(e) { 
          // Clear progress on failure
          const transferId = item.id || (item.Title || '').replace(/[^a-zA-Z0-9.-]/g, '_');
          setIncomingTransferProgress(p => { const n = {...p}; delete n[transferId]; return n; });
        } finally {
          setDownloadedItems(prev => new Set(prev).add(item.id!));
        }
      } else {
        setDownloadedItems(prev => new Set(prev).add(item.id!));
      }
    });
  }, [clips]);

  const transmitTextSecurely = async (payloadText: string) => {
    const isDuplicate = clips.some(c => c.Raw === payloadText || c.Title === payloadText);
    if (isDuplicate) return;

    setIsSending(true);
    try {
      let finalRaw = payloadText;
      let finalType = 'Text';
      
      if (payloadText.startsWith('http')) finalType = 'Url';
      else if (payloadText.includes('meet.google.com') || payloadText.includes('zoom.us') || payloadText.startsWith('www.')) {
          finalType = 'Url';
          finalRaw = `https://${payloadText}`;
      }

      // Use cached URL — instant, no health check
      const targetUrl = await getCachedPcUrl();

      // Register fingerprint to prevent echo-back
      sentContentFingerprintsRef.current.add(finalRaw.substring(0, 200));
      if (sentContentFingerprintsRef.current.size > 100) {
        const arr = Array.from(sentContentFingerprintsRef.current);
        sentContentFingerprintsRef.current = new Set(arr.slice(-50));
      }



      // PRIORITY 2: Local HTTP POST with tight timeout
      let localSuccess = false;
      try {
          const response = await fetchWithTimeout(`${targetUrl}/api/sync_text`, {
              method: 'POST',
              headers: { 'Content-Type': 'text/plain', 'X-Advance-Client': 'MobileCompanion', 'X-Source-Device': deviceName || 'Mobile' },
              body: finalRaw
          }, 1500);
          localSuccess = response.ok;
      } catch(e) {
        cachedPcUrlRef.current = null;
      }

      // SPEED: Firebase push is fire-and-forget — don't block the UI
      if (!localSuccess && isGlobalSyncEnabled) {
          const newRef = push(ref(database, 'clipboard'));
          set(newRef, {
            Title: payloadText.length > 50 ? payloadText.substring(0, 50) + '...' : payloadText,
            Type: finalType,
            Raw: finalRaw,
            Time: new Date().toLocaleTimeString(),
            Timestamp: Date.now(),
            SourceDeviceName: deviceName || 'Unknown Mobile',
            SourceDeviceType: 'Mobile'
          }).catch(() => {});
      }

    } catch (e) {}
    setIsSending(false);
  };

  // --- Multi-Select Helpers ---
  const toggleSelectItem = (id: string) => {
    setSelectedItemIds(prev => {
      const updated = new Set(prev);
      if (updated.has(id)) updated.delete(id);
      else updated.add(id);
      return updated;
    });
  };

  const exitMultiSelect = () => {
    setIsMultiSelectMode(false);
    setSelectedItemIds(new Set());
  };

  const getSelectedClips = () => {
    const filtered = clips.filter(c => (c.IsPinned || (c.Timestamp || 0) >= localWipeTimestamp) && (!c.id || !localDeletedIds.has(c.id)) && (c.Raw || c.Title));
    return filtered.filter(c => selectedItemIds.has(c.id || ''));
  };

  // --- PDF Merge ---
  const openMergeModal = () => {
    const selected = getSelectedClips().filter(c => c.Type === 'Pdf' || (c.Title || '').toLowerCase().endsWith('.pdf'));
    if (selected.length < 2) {
      Alert.alert('Need 2+ PDFs', 'Select at least 2 PDF files to merge.');
      return;
    }
    setMergeQueue([...selected]);
    setIsMergeModalVisible(true);
  };

  const moveMergeItem = (fromIdx: number, toIdx: number) => {
    if (toIdx < 0 || toIdx >= mergeQueue.length) return;
    setMergeQueue(prev => {
      const arr = [...prev];
      const [moved] = arr.splice(fromIdx, 1);
      arr.splice(toIdx, 0, moved);
      return arr;
    });
  };

  const executePdfMerge = async () => {
    try {
      setIsMergeModalVisible(false);
      if (Platform.OS === 'android') ToastAndroid.show('Sending PDFs to PC for merge...', ToastAndroid.LONG);

      let targetUrl = `http://${pcLocalIp}`;
      const activePc = activeDevices.find((d: any) => d.DeviceType === 'PC');
      if (activePc) {
        const opt = await resolveOptimalUrl(activePc);
        if (opt) targetUrl = opt;
      }

      // Collect download URLs for each PDF
      const pdfUrls = mergeQueue.map(item => {
        const mUrl = getMediaUrl(item);
        return mUrl;
      }).filter(u => u.startsWith('http'));

      if (pdfUrls.length < 2) {
        Alert.alert('Error', 'Could not resolve download URLs for the selected PDFs.');
        return;
      }

      const res = await fetchWithTimeout(`${targetUrl}/api/merge_pdfs`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json', 'X-Advance-Client': 'MobileCompanion' },
        body: JSON.stringify({ urls: pdfUrls, sourceDevice: deviceName || 'Mobile' }),
      }, 30000);

      if (res.ok) {
        const body = await res.json();
        if (body.downloadUrl) {
          const mergedUrl = body.downloadUrl.startsWith('http') ? body.downloadUrl : `${targetUrl}${body.downloadUrl}`;
          const localUri = (FileSystem as any).documentDirectory + `merged_${Date.now()}.pdf`;
          await FileSystem.downloadAsync(mergedUrl, localUri, { headers: { 'X-Advance-Client': 'MobileCompanion' } });
          await Sharing.shareAsync(localUri, { mimeType: 'application/pdf', UTI: 'com.adobe.pdf', dialogTitle: 'Merged PDF' });
        } else {
          if (Platform.OS === 'android') ToastAndroid.show('Merged! Check PC clipboard.', ToastAndroid.SHORT);
        }
      } else {
        Alert.alert('Merge Failed', 'PC could not merge these PDFs.');
      }
    } catch (e) {
      Alert.alert('Merge Error', 'Could not connect to PC for merging.');
    }
    exitMultiSelect();
  };

  // --- Forced Sync ---
  const openForceSyncModal = async () => {
    if (selectedItemIds.size === 0) {
      Alert.alert('Nothing Selected', 'Select items first to force sync.');
      return;
    }
    // Fetch all registered devices from Firebase
    try {
      const { get: firebaseGet } = await import('firebase/database');
      const snapshot = await firebaseGet(ref(database, 'active_devices'));
      if (snapshot.exists()) {
        const data = snapshot.val();
        const devices = Object.keys(data).map(k => ({ key: k, ...data[k] })).filter(d => d.DeviceName !== deviceName);
        setForceSyncDevices(devices);
      } else {
        setForceSyncDevices([]);
      }
    } catch (e) {
      setForceSyncDevices([]);
    }
    setIsForceSyncModalVisible(true);
  };

  const executeForcedSync = async (targetDeviceKeys: string[]) => {
    setIsForceSyncModalVisible(false);
    const selected = getSelectedClips();
    if (selected.length === 0) return;

    if (Platform.OS === 'android') ToastAndroid.show(`Force syncing ${selected.length} items to ${targetDeviceKeys.length} device(s)...`, ToastAndroid.LONG);

    try {
      // Write each item to forced_sync/<targetDevice>/items
      for (const deviceKey of targetDeviceKeys) {
        for (const item of selected) {
          const forcedRef = push(ref(database, `forced_sync/${deviceKey}`));
          await set(forcedRef, {
            ...item,
            ForcedBy: deviceName,
            ForcedAt: Date.now(),
            SourceDeviceName: item.SourceDeviceName || deviceName,
          });
        }
      }

      // Also push to global_clipboard if not already there
      for (const item of selected) {
        if (!item.id) {
          const clipRef = push(ref(database, 'global_clipboard'));
          await set(clipRef, { ...item, Timestamp: Date.now() });
        }
      }

      // Also try local sync for each active PC
      for (const deviceKey of targetDeviceKeys) {
        const dev = forceSyncDevices.find(d => d.key === deviceKey);
        if (dev?.LocalIp) {
          try {
            const url = await resolveOptimalUrl(dev);
            if (url) {
              for (const item of selected) {
                await fetchWithTimeout(`${url}/api/sync`, {
                  method: 'POST',
                  headers: { 'Content-Type': 'application/json', 'X-Advance-Client': 'MobileCompanion' },
                  body: JSON.stringify({ title: item.Title, content: item.Raw, type: item.Type, sourceDevice: deviceName }),
                }, 5000).catch(() => {});
              }
            }
          } catch (e) {}
        }
      }

      if (Platform.OS === 'android') ToastAndroid.show('Force sync complete ✅', ToastAndroid.SHORT);
    } catch (e) {
      Alert.alert('Sync Error', 'Failed to force sync some items.');
    }
    exitMultiSelect();
  };

  const sendTextToPc = async () => {
    if (!inputText.trim()) return;
    await transmitTextSecurely(inputText);
    setInputText('');
  };

  const pickFileAndSend = async () => {
    try {
      const result = await DocumentPicker.getDocumentAsync({ type: '*/*' });
      if (result.canceled) return;
      
      const file = result.assets[0];
      let assignedType = 'Document';
      const ext = file.name.split('.').pop()?.toLowerCase() || '';
      if (ext === 'apk' || ext === 'zip' || ext === 'rar') assignedType = 'Archive';
      else if (ext === 'pdf') assignedType = 'Pdf';
      else if (ext === 'mp4' || ext === 'avi' || ext === 'mkv') assignedType = 'Video';
      else if (ext === 'ppt' || ext === 'pptx') assignedType = 'Presentation';
      else if (ext === 'jpg' || ext === 'jpeg' || ext === 'png' || ext === 'gif' || ext === 'webp') assignedType = 'Image';
      else if (ext === 'doc' || ext === 'docx' || ext === 'txt') assignedType = 'Document';
      else assignedType = 'File';

      setPendingUploadPayload({ uri: file.uri, name: file.name, size: file.size, type: assignedType });
      setIsTargetModalVisible(true);
    } catch (err) {
      Alert.alert('Upload Failed');
    }
  };

  const launchDirectCamera = async () => {
    setIsCameraOptionsVisible(false);
    const result = await ImagePicker.launchCameraAsync({
      mediaTypes: ['images'],
      allowsEditing: false,
      quality: 0.8,
    });

    if (!result.canceled) {
       const file = result.assets[0];
       try {
           const b64 = await FileSystem.readAsStringAsync(file.uri, { encoding: (FileSystem as any).EncodingType.Base64 });
           await Clipboard.setImageAsync(b64);
           Platform.OS === 'android' ? ToastAndroid.show("Captured & Copied to Dashboard", ToastAndroid.SHORT) : null;
       } catch (e) {}

       setPendingUploadPayload({ uri: file.uri, name: file.fileName || `camera_${Date.now()}.jpg`, size: file.fileSize, type: 'Image' });
       setIsTargetModalVisible(true);
    }
  };

  const pickImageAndSend = async () => {
    const result = await ImagePicker.launchImageLibraryAsync({
      mediaTypes: ['images', 'videos'],
      allowsEditing: false,
      quality: 0.8,
    });
    if (!result.canceled) {
       const file = result.assets[0];
       try {
           if (file.type === 'image') {
               const b64 = await FileSystem.readAsStringAsync(file.uri, { encoding: (FileSystem as any).EncodingType.Base64 });
               await Clipboard.setImageAsync(b64);
           }
       } catch (e) {}
       
       setPendingUploadPayload({ uri: file.uri, name: file.fileName || `media_${Date.now()}`, size: file.fileSize, type: file.type === 'video' ? 'Video' : 'Image' });
       setIsTargetModalVisible(true);
    }
  };

  const launchQRScanner = async () => {
    setIsCameraOptionsVisible(false);
    if (!cameraPermission?.granted) {
       const perm = await requestCameraPermission();
       if (!perm.granted) {
          Alert.alert("Permission Required", "Camera access is needed to scan QR codes.");
          return;
       }
    }
    setIsQRScannerActive(true);
  };

  const handleBarcodeScanned = async ({ data }: { data: string }) => {
     setIsQRScannerActive(false);
     await Clipboard.setStringAsync(data);
     Platform.OS === 'android' ? ToastAndroid.show("Content copied to clipboard", ToastAndroid.SHORT) : null;
     
     if (data.toLowerCase().startsWith('http://') || data.toLowerCase().startsWith('https://')) {
        Linking.openURL(data).catch(() => {});
     }

     setInputText(data); // Pre-fill input
  };

  const executeHeavyUpload = async (targetDeviceOrGlobal: any) => {
    if (!pendingUploadPayload) return;
    
    setIsTargetModalVisible(false);
    setIsSending(true);
    const { uri: physicalPath, name, size, type } = pendingUploadPayload;

    try {
      // Re-hydrate the original physical extension natively so PC OS doesn't corrupt it
      const safeName = `sync_${Date.now()}_` + name.replace(/[^a-zA-Z0-9.-]/g, '_');
      const hydratedPath = `${(FileSystem as any).cacheDirectory}${safeName}`;
      await FileSystem.copyAsync({ from: physicalPath, to: hydratedPath });

      if (targetDeviceOrGlobal === 'Global') {
        if (!isGlobalSyncEnabled) {
            Alert.alert("Global Sync Disabled", "Turn on Global Cloud Transfer in Settings to push items to the remote Firebase feed.");
            setIsTargetModalVisible(false);
            setPendingUploadPayload(null);
            return;
        }

        const THRESHOLD = 100 * 1024 * 1024; // 100MB Split
        if (size && size > THRESHOLD) {
            Alert.alert("Too Large", "Global Firebase route strictly limited to 100MB to save costs. Pick an active PC Proxy to bypass limits!");
            setIsSending(false);
            return;
        }

        const response = await fetch(hydratedPath);
        const blob = await response.blob();
        const sf = storageRef(storage, `archives/${name}_${Date.now()}`);
        await uploadBytesResumable(sf, blob);
        const downloadUrl = await getDownloadURL(sf);
        
        const newRef = push(ref(database, 'clipboard'));
        await set(newRef, {
          Title: name,
          Type: (() => {
             if (type === 'Image' || type === 'Video') return type;
             const ext = name.split('.').pop()?.toLowerCase() || '';
             if (ext === 'apk' || ext === 'zip' || ext === 'rar') return 'Archive';
             if (ext === 'doc' || ext === 'docx' || ext === 'txt') return 'Document';
             if (ext === 'pdf') return 'Pdf';
             if (ext === 'mp4' || ext === 'avi' || ext === 'mkv') return 'Video';
             if (ext === 'ppt' || ext === 'pptx') return 'Presentation';
             if (ext === 'jpg' || ext === 'jpeg' || ext === 'png' || ext === 'gif' || ext === 'webp') return 'Image';
             return 'File';
          })(),
          Raw: downloadUrl,
          Time: new Date().toLocaleTimeString(),
          Timestamp: Date.now(),
          SourceDeviceName: deviceName || 'Unknown Mobile',
          SourceDeviceType: 'Mobile'
        });
      } else {
        // High-Speed Relay prioritizes naked LAN via intelligent resolver
        const uploadUrl = await resolveOptimalUrl(targetDeviceOrGlobal) + `/api/sync_file?name=${encodeURIComponent(name)}&type=${encodeURIComponent(type)}&sourceDevice=${encodeURIComponent(deviceName || 'Mobile')}`;
        
        await FileSystem.uploadAsync(uploadUrl, hydratedPath, {
          httpMethod: 'POST',
          uploadType: 0 as any, // 0 = FileSystemUploadType.BINARY_CONTENT
          headers: { 
             'X-Original-Date': Date.now().toString(),
             'X-Advance-Client': 'MobileCompanion'
          }
        });
      }
    } catch (err: any) {
      Alert.alert('Upload Failed', `Target node was inaccessible or disconnected. ${err.message}`);
    }
    setIsSending(false);
    setPendingUploadPayload(null);
  };


  return (
    <SafeAreaView style={styles.container}>
      <Modal visible={!deviceName && deviceName === ''} animationType="fade" transparent={true}>
        <View style={styles.modalOverlay}>
          <View style={styles.modalContent}>
            <Text style={styles.modalTitle}>Name this Device</Text>
            <Text style={styles.modalSubtitle}>Identify this device in the AdvanceClip mesh network.</Text>
            <TextInput
              style={styles.modalInput}
              value={setupName}
              onChangeText={setSetupName}
              placeholder="e.g. Galaxy S23"
              placeholderTextColor="#4C5361"
              autoFocus
            />
            <TouchableOpacity 
              style={styles.modalButton} 
              onPress={() => { if(setupName.trim()) setDeviceName(setupName.trim()); }}
            >
              <Text style={styles.modalButtonText}>Join Mesh</Text>
            </TouchableOpacity>
          </View>
        </View>
      </Modal>

      {/* Target Device Selection Modal */}
      <Modal visible={isTargetModalVisible} animationType="slide" transparent={true}>
        <View style={styles.modalOverlay}>
          <View style={styles.modalContent}>
            <Text style={styles.modalTitle}>Select Target Node</Text>
            <Text style={styles.modalSubtitle}>Where do you want to transfer this payload?</Text>
            
            <TouchableOpacity style={styles.targetOption} onPress={() => executeHeavyUpload('Global')}>
              <IconSymbol name="cloud.fill" size={24} color="#4A62EB" />
              <View style={{marginLeft: 12}}>
                <Text style={{color: '#FFF', fontSize: 16, fontWeight: '600'}}>Global Cloud (Firebase)</Text>
                <Text style={{color: '#8A8F98', fontSize: 12}}>10MB Limit. Visible to all devices.</Text>
              </View>
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
                    <View style={{backgroundColor: connectionColors[connType] + '22', borderRadius: 4, paddingHorizontal: 6, paddingVertical: 1}}>
                      <Text style={{color: connectionColors[connType], fontSize: 10, fontWeight: '700'}}>{connType}</Text>
                    </View>
                    <Text style={{color: '#8A8F98', fontSize: 12}}>{connType === 'Local' ? 'Same network · Direct transfer' : 'Remote · Via tunnel'}</Text>
                  </View>
                </View>
              </TouchableOpacity>
              );
            })}

            <TouchableOpacity style={[styles.modalButton, {backgroundColor: '#2A2F3A', marginTop: 10}]} onPress={() => { setIsTargetModalVisible(false); setPendingUploadPayload(null); }}>
              <Text style={styles.modalButtonText}>Cancel</Text>
            </TouchableOpacity>
          </View>
        </View>
      </Modal>



      <Modal visible={isCameraOptionsVisible} animationType="slide" transparent={true}>
        <View style={styles.modalOverlay}>
          <View style={styles.modalContent}>
            <Text style={styles.modalTitle}>Capture Mode</Text>
            <Text style={styles.modalSubtitle}>Take a photo to transfer or scan a data code.</Text>
            
            <TouchableOpacity style={styles.targetOption} onPress={launchDirectCamera}>
              <IconSymbol name="camera.fill" size={24} color="#F59E0B" />
              <View style={{marginLeft: 12}}>
                <Text style={{color: '#FFF', fontSize: 16, fontWeight: '600'}}>Take Photo</Text>
                <Text style={{color: '#8A8F98', fontSize: 12}}>Instantly transfer a camera image.</Text>
              </View>
            </TouchableOpacity>

            <TouchableOpacity style={styles.targetOption} onPress={launchQRScanner}>
              <IconSymbol name="qrcode" size={24} color="#8B5CF6" />
              <View style={{marginLeft: 12}}>
                <Text style={{color: '#FFF', fontSize: 16, fontWeight: '600'}}>Scan QR Code</Text>
                <Text style={{color: '#8A8F98', fontSize: 12}}>Extracts text or opens valid links.</Text>
              </View>
            </TouchableOpacity>

            <TouchableOpacity style={[styles.modalButton, {backgroundColor: '#2A2F3A', marginTop: 10}]} onPress={() => setIsCameraOptionsVisible(false)}>
              <Text style={styles.modalButtonText}>Cancel</Text>
            </TouchableOpacity>
          </View>
        </View>
      </Modal>

      {isQRScannerActive && (
        <Modal visible={isQRScannerActive} animationType="fade" transparent={false}>
          <View style={{flex: 1, backgroundColor: '#000'}}>
             <CameraView 
                style={{flex: 1}} 
                facing="back" 
                barcodeScannerSettings={{ barcodeTypes: ["qr"] }}
                onBarcodeScanned={handleBarcodeScanned}
             />
             <TouchableOpacity style={{position: 'absolute', bottom: 50, alignSelf: 'center', backgroundColor: '#EF4444', padding: 15, borderRadius: 30}} onPress={() => setIsQRScannerActive(false)}>
                 <Text style={{color: '#fff', fontWeight: 'bold', fontSize: 16}}>Cancel Scan</Text>
             </TouchableOpacity>
          </View>
        </Modal>
      )}

      <KeyboardAvoidingView behavior={Platform.OS === 'ios' ? 'padding' : undefined} style={{flex: 1}}>
        <View style={[styles.header, { flexDirection: 'row', justifyContent: 'space-between', alignItems: 'center' }]}>
          <View>
            <Text style={styles.title}>Clipboard Sync</Text>
            <View style={styles.statusRow}>
               <View style={[styles.indicator, { backgroundColor: '#4A62EB' }]} />
               <Text style={styles.statusText}>Cloud Active</Text>
            </View>
          </View>
          
          <View style={{flexDirection: 'row', gap: 10}}>
             {Platform.OS === 'android' && (
                <TouchableOpacity onPress={() => AdvanceOverlay?.startOverlay()} style={{padding: 10, backgroundColor: '#4A62EB33', borderRadius: 10}}>
                  <IconSymbol name="macwindow" size={20} color="#4A62EB" />
                </TouchableOpacity>
             )}
             <TouchableOpacity onPress={clearAllClips} style={{padding: 10, backgroundColor: '#2A2F3A', borderRadius: 10}}>
               <IconSymbol name="trash" size={20} color="#EF4444" />
             </TouchableOpacity>
          </View>
        </View>

        <TouchableOpacity 
           activeOpacity={0.7} 
           onPress={() => setGlobalSyncEnabled(!isGlobalSyncEnabled)}
           style={{marginHorizontal: 20, marginBottom: 15, padding: 12, backgroundColor: isGlobalSyncEnabled ? '#10B98122' : '#2A2F3A', borderRadius: 12, borderWidth: 1, borderColor: isGlobalSyncEnabled ? '#10B98155' : '#4C5361', flexDirection: 'row', alignItems: 'center'}}>
           <IconSymbol name={isGlobalSyncEnabled ? "cloud.fill" : "cloud"} size={22} color={isGlobalSyncEnabled ? "#10B981" : "#8A8F98"} />
           <View style={{marginLeft: 12, flex: 1}}>
             <Text style={{color: '#FFF', fontSize: 14, fontWeight: '700', marginBottom: 2}}>Global Cloud Transfer</Text>
             <Text style={{color: '#8A8F98', fontSize: 11}}>
                {isGlobalSyncEnabled ? "Enabled. Syncing payloads across entire mesh." : "Disabled. Only connecting to local PC proxies."}
             </Text>
           </View>
           <View style={{width: 40, height: 24, borderRadius: 12, backgroundColor: isGlobalSyncEnabled ? '#10B981' : '#4C5361', justifyContent: 'center', alignItems: isGlobalSyncEnabled ? 'flex-end' : 'flex-start', paddingHorizontal: 2}}>
              <View style={{width: 20, height: 20, borderRadius: 10, backgroundColor: '#FFF'}} />
           </View>
        </TouchableOpacity>

        <View style={styles.feedContainer}>
          {isRefreshing && clips.length === 0 ? (
            <ActivityIndicator size="large" color="#4A62EB" style={{marginTop: 50}} />
          ) : clips.length === 0 || clips.filter(c => (c.IsPinned || (c.Timestamp || 0) >= localWipeTimestamp) && (!c.id || !localDeletedIds.has(c.id)) && (c.Raw || c.Title)).length === 0 ? (
            <Text style={styles.emptyText}>No clips synced yet.</Text>
          ) : (
            <FlatList
              data={clips.filter(c => (c.IsPinned || (c.Timestamp || 0) >= localWipeTimestamp) && (!c.id || !localDeletedIds.has(c.id)) && (c.Raw || c.Title))}
              keyExtractor={(item, index) => item.id ? item.id : index.toString()}
              showsVerticalScrollIndicator={false}
              renderItem={({ item }) => {
                let iconName = 'doc.text';
                let iconColor = '#8A8F98';
                const lowerTit = (item.Title || item.Raw || '').toLowerCase();
                const isApk = lowerTit.endsWith('.apk');
                const isPdf = item.Type === 'Pdf' || lowerTit.endsWith('.pdf');
                const isDoc = lowerTit.endsWith('.doc') || lowerTit.endsWith('.docx') || item.Type === 'Document';

                if (item.Type === 'ImageLink' || item.Type === 'Image') { iconName = 'photo'; iconColor = '#ec4899'; }
                else if (item.Type === 'Url') { iconName = 'globe'; iconColor = '#0EA5E9'; }
                else if (isPdf) { iconName = 'doc.richtext'; iconColor = '#EF4444'; }
                else if (isApk) { iconName = 'hammer.fill'; iconColor = '#10B981'; } 
                else if (isDoc) { iconName = 'doc.text.fill'; iconColor = '#3B82F6'; }
                else if (item.Type === 'File' || item.Type === 'Archive' || item.Type === 'Video' || item.Type === 'Presentation') { iconName = 'folder.fill'; iconColor = '#F59E0B'; }
                else if (item.Type === 'Code') { iconName = 'curlybraces'; iconColor = '#10B981'; }
                else if (item.Type === 'QRCode') { iconName = 'qrcode'; iconColor = '#8B5CF6'; }

                const mediaUrl = getMediaUrl(item);
                const isDownloading = mediaUrl.startsWith('http') && !downloadedItems.has(item.id!);
                
                // Incoming transfer progress tracking
                const transferId = item.id || (item.Title || '').replace(/[^a-zA-Z0-9.-]/g, '_');
                const incomingProgress = incomingTransferProgress[transferId];
                const isIncomingTransfer = incomingProgress !== undefined && incomingProgress < 1;
                // File types that need transfer-complete gating before showing action buttons
                const heavyFileTypes = ['Pdf', 'Document', 'Archive', 'Video', 'Audio', 'File', 'Presentation'];
                const isApkType = (item.Title || '').toLowerCase().endsWith('.apk');
                const isHeavyFile = heavyFileTypes.includes(item.Type) || isApkType;

                return (
                  <TouchableOpacity 
                     style={[styles.clipCard, isMultiSelectMode && selectedItemIds.has(item.id || '') && { borderColor: '#4A62EB', borderWidth: 1.5 }]}
                     activeOpacity={0.8}
                     onPress={() => {
                       if (isMultiSelectMode) {
                         toggleSelectItem(item.id || '');
                       } else if (activeOptionsId === item.id) {
                         setActiveOptionsId(null);
                       } else {
                         setActiveOptionsId(item.id!);
                       }
                     }}
                     onLongPress={() => {
                       if (!isMultiSelectMode) {
                         setIsMultiSelectMode(true);
                         setSelectedItemIds(new Set([item.id || '']));
                         setActiveOptionsId(null);
                       }
                     }}
                  >
                    {/* Multi-Select Checkbox */}
                    {isMultiSelectMode && (
                      <View style={{position: 'absolute', left: 8, top: 8, width: 24, height: 24, borderRadius: 12, backgroundColor: selectedItemIds.has(item.id || '') ? '#4A62EB' : 'rgba(255,255,255,0.1)', borderWidth: 2, borderColor: selectedItemIds.has(item.id || '') ? '#4A62EB' : '#4C5361', alignItems: 'center', justifyContent: 'center', zIndex: 10}}>
                        {selectedItemIds.has(item.id || '') && <IconSymbol name="checkmark" size={12} color="#FFF" />}
                      </View>
                    )}

                    <View style={{ flex: 1, padding: 4, paddingLeft: isMultiSelectMode ? 32 : 4 }}>
                      {(() => {
                         if (item.Type === 'Image' || item.Type === 'ImageLink') {
                           const imgUri = mediaUrl || item.CachedUri || item.Raw || '';
                           if (!imgUri) return (
                             <View style={{ marginBottom: 8, height: 100, borderRadius: 12, backgroundColor: '#1C202B', justifyContent: 'center', alignItems: 'center' }}>
                               <IconSymbol name="photo.fill" size={32} color="#4C5361" />
                               <Text style={{color: '#8A8F98', fontSize: 12, marginTop: 8}}>No image URL</Text>
                             </View>
                           );

                           // CachedImageLoader — downloads http:// images to local cache on first render
                           return <CachedImage imgUri={imgUri} onPress={() => setExpandedImage(imgUri)} />;
                         }
                         return null;

                      })()}
                      
                      {(item.Type !== 'Image' && item.Type !== 'ImageLink') && (
                          <Text style={styles.clipTitle}>{item.Raw || item.Title || `${item.Type || 'Clip'} from ${item.SourceDeviceName || 'Unknown'}`}</Text>
                      )}
                      
                      {/* Incoming Transfer Progress Bar */}
                      {isIncomingTransfer && isHeavyFile && (
                        <View style={{position: 'absolute', bottom: 0, left: 0, right: 0, borderBottomLeftRadius: 16, borderBottomRightRadius: 16, overflow: 'hidden', zIndex: 20}}>
                          <View style={{height: 28, backgroundColor: 'rgba(15,17,21,0.92)', flexDirection: 'row', alignItems: 'center', paddingHorizontal: 12}}>
                            <ActivityIndicator size="small" color="#4A62EB" style={{marginRight: 8}} />
                            <Text style={{color: '#8A8F98', fontSize: 11, fontWeight: '600', flex: 1}}>Receiving file...</Text>
                            <Text style={{color: '#4A62EB', fontSize: 12, fontWeight: '800'}}>{Math.round((incomingProgress || 0) * 100)}%</Text>
                          </View>
                          <View style={{height: 3, backgroundColor: 'rgba(74,98,235,0.15)'}}>
                            <View style={{height: 3, backgroundColor: '#4A62EB', width: `${Math.round((incomingProgress || 0) * 100)}%`, borderRadius: 2}} />
                          </View>
                        </View>
                      )}
                    </View>

                    {/* Actions Overlay — hidden during incoming heavy file transfer */}
                    {activeOptionsId === item.id && !(isIncomingTransfer && isHeavyFile) && (
                    <View style={{ position: 'absolute', right: 10, top: 10, flexDirection: 'row', backgroundColor: 'rgba(20,24,36,0.9)', borderRadius: 12, padding: 8, gap: 8 }}>
                      {/* Pin Toggle Button */}
                      <TouchableOpacity onPress={async () => {
                         try {
                           if (!item.id) {
                               // Fallback logic if it's a Local PC item without Firebase ID
                               ToastAndroid.show("Pinning is restricted to Global Cloud payloads.", ToastAndroid.SHORT);
                               return; 
                           }
                           await update(ref(database, `clipboard/${item.id}`), {
                               IsPinned: !item.IsPinned,
                               Timestamp: Date.now() // Prevent limitToLast(30) from dropping it upon updating state
                           });
                         } catch(e) {}
                      }} style={[styles.actionBtnIcon, {backgroundColor: item.IsPinned ? '#F59E0B' : 'rgba(255,255,255,0.05)'}]}>
                         <IconSymbol name={item.IsPinned ? "pin.fill" : "pin"} size={16} color={item.IsPinned ? "#FFF" : "#8A8F98"} />
                      </TouchableOpacity>

                      {/* Individual Delete Button */}
                      <TouchableOpacity onPress={async () => {
                         try {
                           if (!item.id) return;
                           
                           // Actually remove from Firebase
                           await remove(ref(database, `clipboard/${item.id}`));
                           
                           // Also hide locally as backup
                           setLocalDeletedIds(prev => {
                               const updated = new Set(prev);
                               updated.add(item.id!);
                               AsyncStorage.setItem('localDeletedIds', JSON.stringify(Array.from(updated))).catch(() => {});
                               return updated;
                           });
                           
                           Platform.OS === 'android' ? ToastAndroid.show("Deleted", ToastAndroid.SHORT) : alert("Deleted");
                         } catch(e) {
                           Platform.OS === 'android' ? ToastAndroid.show("Delete failed", ToastAndroid.SHORT) : alert("Delete failed");
                         }
                      }} style={[styles.actionBtnIcon, {backgroundColor: 'rgba(239,68,68,0.1)'}]}>
                         <IconSymbol name="trash" size={16} color="#EF4444" />
                      </TouchableOpacity>

                      {/* Global Copy Button */}
                      <TouchableOpacity onPress={async () => {
                         const contentStr = item.Raw || item.Title || '';
                         try { await Clipboard.setStringAsync(contentStr); } catch (e) {}
                         Platform.OS === 'android' ? ToastAndroid.show("Copied", ToastAndroid.SHORT) : Alert.alert("Copied", "Copied to your clipboard.");
                      }} style={[styles.actionBtnIcon, {backgroundColor: 'rgba(255,255,255,0.05)'}]}>
                         <IconSymbol name="doc.on.doc" size={16} color="#FFF" />
                      </TouchableOpacity>

                      {/* URL specific Open Button */}
                      {item.Type === 'Url' && item.Raw.startsWith('http') && (
                        <TouchableOpacity onPress={() => WebBrowser.openBrowserAsync(item.Raw)} style={[styles.actionBtnIcon, {backgroundColor: '#3B82F6'}]}>
                          <IconSymbol name="arrow.up.right" size={16} color="#FFF" />
                        </TouchableOpacity>
                      )}

                      {/* QR Code specific Button */}
                      {item.Type === 'QRCode' && (
                        <TouchableOpacity onPress={async () => {
                          if (item.Raw.startsWith('http')) {
                            WebBrowser.openBrowserAsync(item.Raw);
                          } else {
                            await Clipboard.setStringAsync(item.Raw);
                            Platform.OS === 'android' ? ToastAndroid.show("Copied", ToastAndroid.SHORT) : Alert.alert("Copied", "Copied to your clipboard.");
                          }
                        }} style={[styles.actionBtnIcon, {backgroundColor: '#8B5CF6'}]}>
                          <IconSymbol name={item.Raw.startsWith('http') ? "arrow.up.right" : "doc.on.doc"} size={16} color="#FFF" />
                        </TouchableOpacity>
                      )}

                      {/* PDF - Download then Open */}
                      {isPdf && (() => {
                        const safeName = (item.Title || `file_${Date.now()}`).replace(/[^a-zA-Z0-9.-]/g, '_');
                        const localUri = (FileSystem as any).documentDirectory + safeName;
                        const dlId = item.id || safeName;
                        const prog = downloadProgress[dlId];
                        const dlNow = prog !== undefined && prog < 1;
                        const dlDone = downloadedItems.has(item.id!) || (prog !== undefined && prog >= 1);

                        const doPdfDownload = async () => {
                          const fi = await FileSystem.getInfoAsync(localUri);
                          if (fi.exists && (fi as any).size > 0) {
                            setDownloadedItems(prev => new Set(prev).add(item.id!));
                            return localUri;
                          }
                          if (Platform.OS === 'android') ToastAndroid.show('Downloading PDF...', ToastAndroid.SHORT);
                          setDownloadProgress(p => ({...p, [dlId]: 0.001}));
                          const res = FileSystem.createDownloadResumable(mediaUrl, localUri,
                            { headers: { 'X-Advance-Client': 'MobileCompanion' } },
                            (dp) => { const pct = dp.totalBytesExpectedToWrite > 0 ? dp.totalBytesWritten / dp.totalBytesExpectedToWrite : 0; setDownloadProgress(p => ({...p, [dlId]: Math.max(0.001, pct)})); }
                          );
                          const result = await res.downloadAsync();
                          setDownloadProgress(p => ({...p, [dlId]: 1}));
                          setDownloadedItems(prev => new Set(prev).add(item.id!));
                          if (Platform.OS === 'android') ToastAndroid.show(`${safeName} ready!`, ToastAndroid.SHORT);
                          return result?.uri || localUri;
                        };

                        return (
                          <>
                            {dlNow && (
                              <View style={{flexDirection: 'row', alignItems: 'center', gap: 4}}>
                                <View style={{width: 50, height: 5, borderRadius: 3, backgroundColor: 'rgba(239,68,68,0.2)', overflow: 'hidden'}}>
                                  <View style={{width: `${Math.round((prog || 0) * 100)}%` as any, height: '100%', backgroundColor: '#EF4444', borderRadius: 3}} />
                                </View>
                                <Text style={{color: '#EF4444', fontSize: 9, fontWeight: '800'}}>{Math.round((prog || 0) * 100)}%</Text>
                              </View>
                            )}
                            {!dlDone && !dlNow && (
                              <TouchableOpacity onPress={async () => { try { await doPdfDownload(); } catch(e) { if (Platform.OS === 'android') ToastAndroid.show("Download failed", ToastAndroid.SHORT); } }}
                                style={[styles.actionBtnIcon, {backgroundColor: '#EF4444', paddingHorizontal: 12, width: 'auto'}]}>
                                <Text style={{color: '#FFF', fontSize: 10, fontWeight: '800'}}>DOWNLOAD</Text>
                              </TouchableOpacity>
                            )}
                            {dlDone && (
                              <TouchableOpacity onPress={async () => {
                                try {
                                  const contentUri = await FileSystem.getContentUriAsync(localUri);
                                  await IntentLauncher.startActivityAsync('android.intent.action.VIEW', { data: contentUri, flags: 1, type: 'application/pdf' });
                                } catch(e) { try { await Sharing.shareAsync(localUri, { mimeType: 'application/pdf', dialogTitle: 'Open PDF' }); } catch(_) {} }
                              }} style={[styles.actionBtnIcon, {backgroundColor: '#EF4444', paddingHorizontal: 14, width: 'auto'}]}>
                                <Text style={{color: '#FFF', fontSize: 11, fontWeight: '700'}}>OPEN PDF</Text>
                              </TouchableOpacity>
                            )}
                          </>
                        );
                      })()}

                      {/* Word Doc - Download then Open */}
                      {isDoc && (() => {
                        const safeName = (item.Title || `file_${Date.now()}`).replace(/[^a-zA-Z0-9.-]/g, '_');
                        const localUri = (FileSystem as any).documentDirectory + safeName;
                        const dlId = item.id || safeName;
                        const prog = downloadProgress[dlId];
                        const dlNow = prog !== undefined && prog < 1;
                        const dlDone = downloadedItems.has(item.id!) || (prog !== undefined && prog >= 1);

                        const doDocDownload = async () => {
                          const fi = await FileSystem.getInfoAsync(localUri);
                          if (fi.exists && (fi as any).size > 0) {
                            setDownloadedItems(prev => new Set(prev).add(item.id!));
                            return localUri;
                          }
                          if (Platform.OS === 'android') ToastAndroid.show('Downloading document...', ToastAndroid.SHORT);
                          setDownloadProgress(p => ({...p, [dlId]: 0.001}));
                          const res = FileSystem.createDownloadResumable(mediaUrl, localUri,
                            { headers: { 'X-Advance-Client': 'MobileCompanion' } },
                            (dp) => { const pct = dp.totalBytesExpectedToWrite > 0 ? dp.totalBytesWritten / dp.totalBytesExpectedToWrite : 0; setDownloadProgress(p => ({...p, [dlId]: Math.max(0.001, pct)})); }
                          );
                          const result = await res.downloadAsync();
                          setDownloadProgress(p => ({...p, [dlId]: 1}));
                          setDownloadedItems(prev => new Set(prev).add(item.id!));
                          if (Platform.OS === 'android') ToastAndroid.show(`${safeName} ready!`, ToastAndroid.SHORT);
                          return result?.uri || localUri;
                        };

                        return (
                          <>
                            {dlNow && (
                              <View style={{flexDirection: 'row', alignItems: 'center', gap: 4}}>
                                <View style={{width: 50, height: 5, borderRadius: 3, backgroundColor: 'rgba(59,130,246,0.2)', overflow: 'hidden'}}>
                                  <View style={{width: `${Math.round((prog || 0) * 100)}%` as any, height: '100%', backgroundColor: '#3B82F6', borderRadius: 3}} />
                                </View>
                                <Text style={{color: '#3B82F6', fontSize: 9, fontWeight: '800'}}>{Math.round((prog || 0) * 100)}%</Text>
                              </View>
                            )}
                            {!dlDone && !dlNow && (
                              <TouchableOpacity onPress={async () => { try { await doDocDownload(); } catch(e) { if (Platform.OS === 'android') ToastAndroid.show("Download failed", ToastAndroid.SHORT); } }}
                                style={[styles.actionBtnIcon, {backgroundColor: '#3B82F6', paddingHorizontal: 12, width: 'auto'}]}>
                                <Text style={{color: '#FFF', fontSize: 10, fontWeight: '800'}}>DOWNLOAD</Text>
                              </TouchableOpacity>
                            )}
                            {dlDone && (
                              <TouchableOpacity onPress={async () => {
                                try {
                                  const contentUri = await FileSystem.getContentUriAsync(localUri);
                                  await IntentLauncher.startActivityAsync('android.intent.action.VIEW', { data: contentUri, flags: 1, type: 'application/msword' });
                                } catch(e) { try { await Sharing.shareAsync(localUri, { mimeType: 'application/msword', dialogTitle: 'Open Document' }); } catch(_) {} }
                              }} style={[styles.actionBtnIcon, {backgroundColor: '#3B82F6', paddingHorizontal: 12, width: 'auto'}]}>
                                <Text style={{color: '#FFF', fontSize: 11, fontWeight: '700'}}>OPEN</Text>
                              </TouchableOpacity>
                            )}
                          </>
                        );
                      })()}

                      {/* Word Doc - Convert to PDF Button */}
                      {isDoc && (
                        <TouchableOpacity onPress={async () => {
                           try {
                               const safeName = item.Title.replace(/[^a-zA-Z0-9.-]/g, '_') || `file_${Date.now()}`;
                               const localUri = (FileSystem as any).documentDirectory + safeName;
                               const fileInfo = await FileSystem.getInfoAsync(localUri);
                               
                               if (!fileInfo.exists) {
                                   if (Platform.OS === 'android') ToastAndroid.show('Downloading document for conversion...', ToastAndroid.SHORT);
                                   await FileSystem.downloadAsync(mediaUrl, localUri, { headers: { 'X-Advance-Client': 'MobileCompanion' } });
                                   setDownloadedItems(prev => new Set(prev).add(item.id!));
                               }

                               // Send to PC for conversion
                               let targetUrl = `http://${pcLocalIp}`;
                               const activePc = activeDevices.find((d: any) => d.DeviceType === 'PC');
                               if (activePc) {
                                  const opt = await resolveOptimalUrl(activePc);
                                  if (opt) targetUrl = opt;
                               }

                               if (Platform.OS === 'android') ToastAndroid.show('Converting to PDF...', ToastAndroid.SHORT);
                               
                               const uploadRes = await FileSystem.uploadAsync(
                                 `${targetUrl}/api/convert_to_pdf?name=${encodeURIComponent(safeName)}&sourceDevice=${encodeURIComponent(deviceName || 'Mobile')}`,
                                 localUri,
                                 {
                                   httpMethod: 'POST',
                                   uploadType: 0 as any,
                                   headers: { 'X-Advance-Client': 'MobileCompanion' }
                                 }
                               );

                               if (uploadRes.status === 200) {
                                  // Download the converted PDF
                                  const pdfName = safeName.replace(/\.(docx?|doc)$/i, '.pdf');
                                  const pdfUri = (FileSystem as any).documentDirectory + pdfName;
                                  const body = JSON.parse(uploadRes.body);
                                  if (body.downloadUrl) {
                                     const pdfUrl = body.downloadUrl.startsWith('http') ? body.downloadUrl : `${targetUrl}${body.downloadUrl}`;
                                     await FileSystem.downloadAsync(pdfUrl, pdfUri, { headers: { 'X-Advance-Client': 'MobileCompanion' } });
                                     await Sharing.shareAsync(pdfUri, { mimeType: 'application/pdf', UTI: 'com.adobe.pdf', dialogTitle: 'Converted PDF' });
                                  } else {
                                     if (Platform.OS === 'android') ToastAndroid.show('Converted! Check PC clipboard.', ToastAndroid.SHORT);
                                  }
                               } else {
                                  Alert.alert("Conversion Failed", "PC could not convert this document. Make sure LibreOffice or Word is installed on the PC.");
                               }
                           } catch(e) {
                               Alert.alert("Conversion Error", "Could not connect to PC for conversion. Make sure your PC is reachable.");
                           }
                        }} style={[styles.actionBtnIcon, {backgroundColor: '#F59E0B', paddingHorizontal: 12, width: 'auto'}]}>
                           <Text style={{color: '#FFF', fontSize: 11, fontWeight: '700'}}>→ PDF</Text>
                        </TouchableOpacity>
                      )}

                      {/* Image/HTML → PDF Conversion */}
                      {(item.Type === 'Image' || item.Type === 'ImageLink' || lowerTit.endsWith('.html') || lowerTit.endsWith('.htm')) && (
                        <TouchableOpacity onPress={async () => {
                           try {
                               let targetUrl = `http://${pcLocalIp}`;
                               const activePc = activeDevices.find((d: any) => d.DeviceType === 'PC');
                               if (activePc) { const opt = await resolveOptimalUrl(activePc); if (opt) targetUrl = opt; }
                               if (Platform.OS === 'android') ToastAndroid.show('Converting to PDF...', ToastAndroid.SHORT);
                               const mUrl = getMediaUrl(item);
                               const safeName = (item.Title || `file_${Date.now()}`).replace(/[^a-zA-Z0-9.-]/g, '_');
                               const res = await fetchWithTimeout(`${targetUrl}/api/convert_to_pdf?name=${encodeURIComponent(safeName)}&sourceDevice=${encodeURIComponent(deviceName || 'Mobile')}&url=${encodeURIComponent(mUrl)}`, { method: 'POST', headers: { 'X-Advance-Client': 'MobileCompanion' } }, 30000);
                               if (res.ok) {
                                  const body = await res.json();
                                  if (body.downloadUrl) {
                                     const pdfUrl = body.downloadUrl.startsWith('http') ? body.downloadUrl : `${targetUrl}${body.downloadUrl}`;
                                     const localUri = (FileSystem as any).documentDirectory + safeName.replace(/\.[^.]+$/, '.pdf');
                                     await FileSystem.downloadAsync(pdfUrl, localUri, { headers: { 'X-Advance-Client': 'MobileCompanion' } });
                                     await Sharing.shareAsync(localUri, { mimeType: 'application/pdf', UTI: 'com.adobe.pdf', dialogTitle: 'Converted PDF' });
                                  } else { if (Platform.OS === 'android') ToastAndroid.show('Converted! Check PC.', ToastAndroid.SHORT); }
                               } else { Alert.alert('Conversion Failed', 'PC could not convert this file.'); }
                           } catch(e) { Alert.alert('Error', 'Could not connect to PC for conversion.'); }
                        }} style={[styles.actionBtnIcon, {backgroundColor: '#F59E0B', paddingHorizontal: 12, width: 'auto'}]}>
                           <Text style={{color: '#FFF', fontSize: 11, fontWeight: '700'}}>→ PDF</Text>
                        </TouchableOpacity>
                      )}

                      {/* PDF → Word Conversion */}
                      {isPdf && (
                        <TouchableOpacity onPress={async () => {
                           try {
                               let targetUrl = `http://${pcLocalIp}`;
                               const activePc = activeDevices.find((d: any) => d.DeviceType === 'PC');
                               if (activePc) { const opt = await resolveOptimalUrl(activePc); if (opt) targetUrl = opt; }
                               if (Platform.OS === 'android') ToastAndroid.show('Converting PDF to Word...', ToastAndroid.SHORT);
                               const safeName = (item.Title || `file_${Date.now()}`).replace(/[^a-zA-Z0-9.-]/g, '_');
                               const localUri = (FileSystem as any).documentDirectory + safeName;
                               const fileInfo = await FileSystem.getInfoAsync(localUri);
                               if (!fileInfo.exists) {
                                  await FileSystem.downloadAsync(mediaUrl, localUri, { headers: { 'X-Advance-Client': 'MobileCompanion' } });
                               }
                               const uploadRes = await FileSystem.uploadAsync(
                                 `${targetUrl}/api/convert_pdf_to_word?name=${encodeURIComponent(safeName)}&sourceDevice=${encodeURIComponent(deviceName || 'Mobile')}`,
                                 localUri, { httpMethod: 'POST', uploadType: 0 as any, headers: { 'X-Advance-Client': 'MobileCompanion' } }
                               );
                               if (uploadRes.status === 200) {
                                  const body = JSON.parse(uploadRes.body);
                                  if (body.downloadUrl) {
                                     const docUrl = body.downloadUrl.startsWith('http') ? body.downloadUrl : `${targetUrl}${body.downloadUrl}`;
                                     const docUri = (FileSystem as any).documentDirectory + safeName.replace(/\.pdf$/i, '.docx');
                                     await FileSystem.downloadAsync(docUrl, docUri, { headers: { 'X-Advance-Client': 'MobileCompanion' } });
                                     await Sharing.shareAsync(docUri, { mimeType: 'application/vnd.openxmlformats-officedocument.wordprocessingml.document', dialogTitle: 'Converted Word' });
                                  } else { if (Platform.OS === 'android') ToastAndroid.show('Converted! Check PC.', ToastAndroid.SHORT); }
                               } else { Alert.alert('Conversion Failed', 'PC could not convert this PDF.'); }
                           } catch(e) { Alert.alert('Error', 'Could not connect to PC.'); }
                        }} style={[styles.actionBtnIcon, {backgroundColor: '#3B82F6', paddingHorizontal: 12, width: 'auto'}]}>
                           <Text style={{color: '#FFF', fontSize: 11, fontWeight: '700'}}>→ WORD</Text>
                        </TouchableOpacity>
                      )}

                      {/* ===== Universal File Actions: Download / Open / Share ===== */}
                      {(() => {
                        const isFileType = isApk || item.Type === 'File' || item.Type === 'Archive' || 
                                           item.Type === 'Video' || item.Type === 'Audio' || 
                                           item.Type === 'Presentation' ||
                                           (item.Type === 'Image' || item.Type === 'ImageLink');
                        const showFileActions = !isPdf && !isDoc && isFileType && mediaUrl.startsWith('http');
                        if (!showFileActions) return null;

                        const safeName = (item.Title || `file_${Date.now()}`).replace(/[^a-zA-Z0-9.-]/g, '_');
                        const localUri = (FileSystem as any).documentDirectory + safeName;
                        const downloadId = item.id || safeName;
                        const progress = downloadProgress[downloadId];
                        const isDownloadingNow = progress !== undefined && progress < 1;
                        const isDownloaded = downloadedItems.has(item.id!) || (progress !== undefined && progress >= 1);

                        let mimeType = 'application/octet-stream';
                        if (isApk) mimeType = 'application/vnd.android.package-archive';
                        else if (item.Type === 'Video') mimeType = 'video/*';
                        else if (item.Type === 'Audio') mimeType = 'audio/*';
                        else if (item.Type === 'Image' || item.Type === 'ImageLink') mimeType = 'image/*';
                        else if (item.Type === 'Archive') mimeType = 'application/zip';

                        const doDownload = async () => {
                          const fileInfo = await FileSystem.getInfoAsync(localUri);
                          if (fileInfo.exists && (fileInfo as any).size > 0) {
                            try {
                              const headRes = await fetch(mediaUrl, { method: 'HEAD', headers: { 'X-Advance-Client': 'MobileCompanion' } });
                              const expectedSize = parseInt(headRes.headers.get('content-length') || '0', 10);
                              if (expectedSize === 0 || Math.abs((fileInfo as any).size - expectedSize) < 1024) {
                                setDownloadedItems(prev => new Set(prev).add(item.id!));
                                return localUri;
                              }
                              await FileSystem.deleteAsync(localUri, { idempotent: true });
                            } catch(e) {
                              setDownloadedItems(prev => new Set(prev).add(item.id!));
                              return localUri;
                            }
                          }
                          if (Platform.OS === 'android') ToastAndroid.show(`Downloading ${safeName}...`, ToastAndroid.SHORT);
                          setDownloadProgress(p => ({...p, [downloadId]: 0.001}));
                          const resumable = FileSystem.createDownloadResumable(
                            mediaUrl, localUri,
                            { headers: { 'X-Advance-Client': 'MobileCompanion' } },
                            (dp) => {
                              const pct = dp.totalBytesExpectedToWrite > 0 ? dp.totalBytesWritten / dp.totalBytesExpectedToWrite : 0;
                              setDownloadProgress(p => ({...p, [downloadId]: Math.max(0.001, pct)}));
                            }
                          );
                          const result = await resumable.downloadAsync();
                          setDownloadProgress(p => ({...p, [downloadId]: 1}));
                          setDownloadedItems(prev => new Set(prev).add(item.id!));
                          if (Platform.OS === 'android') ToastAndroid.show(`${safeName} ready!`, ToastAndroid.SHORT);
                          return result?.uri || localUri;
                        };

                        return (
                          <>
                            {isDownloadingNow && (
                              <View style={{flexDirection: 'row', alignItems: 'center', gap: 4}}>
                                <View style={{width: 50, height: 5, borderRadius: 3, backgroundColor: 'rgba(74,98,235,0.2)', overflow: 'hidden'}}>
                                  <View style={{width: `${Math.round((progress || 0) * 100)}%` as any, height: '100%', backgroundColor: '#4A62EB', borderRadius: 3}} />
                                </View>
                                <Text style={{color: '#4A62EB', fontSize: 9, fontWeight: '800'}}>{Math.round((progress || 0) * 100)}%</Text>
                              </View>
                            )}
                            {!isDownloaded && !isDownloadingNow && (
                              <TouchableOpacity onPress={async () => {
                                try { await doDownload(); } catch(e) { if (Platform.OS === 'android') ToastAndroid.show("Download failed", ToastAndroid.SHORT); }
                              }} style={[styles.actionBtnIcon, {backgroundColor: '#10B981', paddingHorizontal: 10, width: 'auto'}]}>
                                <Text style={{color: '#FFF', fontSize: 10, fontWeight: '800'}}>DOWNLOAD</Text>
                              </TouchableOpacity>
                            )}
                            {isDownloaded && (
                              <TouchableOpacity onPress={async () => {
                                try {
                                  const fileInfo = await FileSystem.getInfoAsync(localUri);
                                  const uri = fileInfo.exists ? localUri : await doDownload();
                                  if (!uri) return;
                                  
                                  if (isApk) {
                                    // APK: use Android package-archive intent directly
                                    const contentUri = await FileSystem.getContentUriAsync(uri);
                                    await IntentLauncher.startActivityAsync('android.intent.action.VIEW', {
                                      data: contentUri, flags: 1, type: 'application/vnd.android.package-archive'
                                    });
                                  } else if (item.Type === 'Video') {
                                    const contentUri = await FileSystem.getContentUriAsync(uri);
                                    await IntentLauncher.startActivityAsync('android.intent.action.VIEW', {
                                      data: contentUri, flags: 1, type: 'video/*'
                                    });
                                  } else if (item.Type === 'Audio') {
                                    const contentUri = await FileSystem.getContentUriAsync(uri);
                                    await IntentLauncher.startActivityAsync('android.intent.action.VIEW', {
                                      data: contentUri, flags: 1, type: 'audio/*'
                                    });
                                  } else if (item.Type === 'Image' || item.Type === 'ImageLink') {
                                    // Save to gallery
                                    const p = await MediaLibrary.requestPermissionsAsync();
                                    if (p.status === 'granted') {
                                      await MediaLibrary.saveToLibraryAsync(uri);
                                      if (Platform.OS === 'android') ToastAndroid.show('✅ Saved to Gallery!', ToastAndroid.SHORT);
                                    }
                                  } else if (mimeType === 'application/pdf' || safeName.endsWith('.pdf')) {
                                    const contentUri = await FileSystem.getContentUriAsync(uri);
                                    await IntentLauncher.startActivityAsync('android.intent.action.VIEW', {
                                      data: contentUri, flags: 1, type: 'application/pdf'
                                    });
                                  } else {
                                    // Generic: open with correct MIME type (no share sheet)
                                    const contentUri = await FileSystem.getContentUriAsync(uri);
                                    await IntentLauncher.startActivityAsync('android.intent.action.VIEW', {
                                      data: contentUri, flags: 1, type: mimeType || '*/*'
                                    });
                                  }
                                } catch(e: any) {
                                  // Fallback to share sheet if intent fails
                                  try { await Sharing.shareAsync(localUri, { mimeType, dialogTitle: `Open ${safeName}` }); } catch(_) {}
                                  if (Platform.OS === 'android') ToastAndroid.show('Opening...', ToastAndroid.SHORT);
                                }
                              }} style={[styles.actionBtnIcon, {backgroundColor: isApk ? '#10B981' : '#3B82F6', paddingHorizontal: 10, width: 'auto'}]}>
                                <Text style={{color: '#FFF', fontSize: 10, fontWeight: '800'}}>{isApk ? 'INSTALL' : 'OPEN'}</Text>
                              </TouchableOpacity>
                            )}
                          </>
                        );
                      })()}
                    </View>
                    )}
                  </TouchableOpacity>
                );
              }}
            />
          )}
        </View>

        {/* Multi-Select Action Bar */}
        {isMultiSelectMode && (
          <View style={{backgroundColor: '#1C1F26', borderTopWidth: 1, borderColor: '#2A2F3A', padding: 12, flexDirection: 'row', alignItems: 'center', gap: 8}}>
            <Text style={{color: '#8A8F98', fontSize: 13, fontWeight: '600', marginRight: 4}}>{selectedItemIds.size} selected</Text>
            
            {/* Merge PDFs - only if all selected are PDFs */}
            {(() => {
              const sel = getSelectedClips();
              const allPdf = sel.length >= 2 && sel.every(c => c.Type === 'Pdf' || (c.Title || '').toLowerCase().endsWith('.pdf'));
              if (allPdf) return (
                <TouchableOpacity style={{backgroundColor: '#EF4444', paddingHorizontal: 14, paddingVertical: 8, borderRadius: 10, flexDirection: 'row', alignItems: 'center', gap: 4}} onPress={openMergeModal}>
                  <IconSymbol name="doc.on.doc.fill" size={14} color="#FFF" />
                  <Text style={{color: '#FFF', fontSize: 12, fontWeight: '700'}}>Merge PDFs</Text>
                </TouchableOpacity>
              );
              return null;
            })()}

            {/* Share Selected */}
            <TouchableOpacity style={{backgroundColor: '#10B981', paddingHorizontal: 14, paddingVertical: 8, borderRadius: 10, flexDirection: 'row', alignItems: 'center', gap: 4}} onPress={async () => {
              try {
                const selected = clips.filter(c => selectedItemIds.has(c.id || ''));
                if (selected.length === 0) return;
                // Share first selected item's content
                const item = selected[0];
                const mUrl = getMediaUrl(item);
                if (mUrl.startsWith('http')) {
                  const safeName = (item.Title || `file_${Date.now()}`).replace(/[^a-zA-Z0-9.-]/g, '_');
                  const localUri = (FileSystem as any).documentDirectory + safeName;
                  const fileInfo = await FileSystem.getInfoAsync(localUri);
                  let uri = localUri;
                  if (!fileInfo.exists) {
                    if (Platform.OS === 'android') ToastAndroid.show('Downloading for share...', ToastAndroid.SHORT);
                    const dl = await FileSystem.downloadAsync(mUrl, localUri, { headers: { 'X-Advance-Client': 'MobileCompanion' } });
                    uri = dl.uri;
                  }
                  await Sharing.shareAsync(uri, { dialogTitle: `Share ${safeName}` });
                } else {
                  const text = item.Raw || item.Title || '';
                  await Sharing.shareAsync(text, { dialogTitle: 'Share' }).catch(() => {
                    Clipboard.setStringAsync(text);
                    if (Platform.OS === 'android') ToastAndroid.show('Copied to clipboard', ToastAndroid.SHORT);
                  });
                }
              } catch(e) {
                if (Platform.OS === 'android') ToastAndroid.show('Share failed', ToastAndroid.SHORT);
              }
            }}>
              <IconSymbol name="square.and.arrow.up" size={14} color="#FFF" />
              <Text style={{color: '#FFF', fontSize: 12, fontWeight: '700'}}>Share</Text>
            </TouchableOpacity>

            {/* Force Sync */}
            <TouchableOpacity style={{backgroundColor: '#4A62EB', paddingHorizontal: 14, paddingVertical: 8, borderRadius: 10, flexDirection: 'row', alignItems: 'center', gap: 4}} onPress={openForceSyncModal}>
              <IconSymbol name="bolt.fill" size={14} color="#FFF" />
              <Text style={{color: '#FFF', fontSize: 12, fontWeight: '700'}}>Force Sync</Text>
            </TouchableOpacity>

            <View style={{flex: 1}} />
            <TouchableOpacity style={{backgroundColor: '#2A2F3A', paddingHorizontal: 14, paddingVertical: 8, borderRadius: 10}} onPress={exitMultiSelect}>
              <Text style={{color: '#FFF', fontSize: 12, fontWeight: '700'}}>Cancel</Text>
            </TouchableOpacity>
          </View>
        )}

        {/* PDF Merge Modal */}
        <Modal visible={isMergeModalVisible} animationType="slide" transparent={true}>
          <View style={styles.modalOverlay}>
            <View style={[styles.modalContent, {maxHeight: '80%'}]}>
              <Text style={styles.modalTitle}>Arrange & Merge PDFs</Text>
              <Text style={styles.modalSubtitle}>Drag items up/down to reorder before merging.</Text>
              
              <ScrollView style={{maxHeight: 350, marginTop: 12}}>
                {mergeQueue.map((item, idx) => (
                  <View key={idx} style={{flexDirection: 'row', alignItems: 'center', backgroundColor: '#2A2F3A', borderRadius: 12, padding: 12, marginBottom: 8}}>
                    <View style={{width: 28, height: 28, borderRadius: 14, backgroundColor: '#EF4444', alignItems: 'center', justifyContent: 'center', marginRight: 10}}>
                      <Text style={{color: '#FFF', fontSize: 12, fontWeight: '800'}}>{idx + 1}</Text>
                    </View>
                    <Text style={{color: '#FFF', fontSize: 13, flex: 1, fontWeight: '500'}} numberOfLines={1}>{item.Title}</Text>
                    <View style={{flexDirection: 'row', gap: 6}}>
                      <TouchableOpacity onPress={() => moveMergeItem(idx, idx - 1)} style={{backgroundColor: '#1C1F26', width: 30, height: 30, borderRadius: 8, alignItems: 'center', justifyContent: 'center'}}>
                        <IconSymbol name="chevron.up" size={14} color="#FFF" />
                      </TouchableOpacity>
                      <TouchableOpacity onPress={() => moveMergeItem(idx, idx + 1)} style={{backgroundColor: '#1C1F26', width: 30, height: 30, borderRadius: 8, alignItems: 'center', justifyContent: 'center'}}>
                        <IconSymbol name="chevron.down" size={14} color="#FFF" />
                      </TouchableOpacity>
                    </View>
                  </View>
                ))}
              </ScrollView>

              <TouchableOpacity style={{backgroundColor: '#EF4444', paddingVertical: 16, borderRadius: 14, alignItems: 'center', marginTop: 16}} onPress={executePdfMerge}>
                <Text style={{color: '#FFF', fontSize: 16, fontWeight: '800'}}>Merge {mergeQueue.length} PDFs</Text>
              </TouchableOpacity>
              <TouchableOpacity style={{backgroundColor: '#2A2F3A', paddingVertical: 14, borderRadius: 14, alignItems: 'center', marginTop: 8}} onPress={() => setIsMergeModalVisible(false)}>
                <Text style={{color: '#FFF', fontSize: 14, fontWeight: '600'}}>Cancel</Text>
              </TouchableOpacity>
            </View>
          </View>
        </Modal>

        {/* Forced Sync Device Picker Modal */}
        <Modal visible={isForceSyncModalVisible} animationType="slide" transparent={true}>
          <View style={styles.modalOverlay}>
            <View style={[styles.modalContent, {maxHeight: '80%'}]}>
              <Text style={styles.modalTitle}>⚡ Force Sync</Text>
              <Text style={styles.modalSubtitle}>Push {selectedItemIds.size} items to selected devices. This bypasses sync settings and forces delivery.</Text>

              {/* Send to All */}
              <TouchableOpacity style={{backgroundColor: '#4A62EB', paddingVertical: 14, borderRadius: 14, alignItems: 'center', marginTop: 12, flexDirection: 'row', justifyContent: 'center', gap: 6}} onPress={() => executeForcedSync(forceSyncDevices.map(d => d.key))}>
                <IconSymbol name="bolt.fill" size={16} color="#FFF" />
                <Text style={{color: '#FFF', fontSize: 15, fontWeight: '800'}}>Force to ALL Devices ({forceSyncDevices.length})</Text>
              </TouchableOpacity>

              <Text style={{color: '#8A8F98', fontSize: 12, marginTop: 16, marginBottom: 8, fontWeight: '700', textTransform: 'uppercase'}}>Or Select Individual Devices</Text>

              <ScrollView style={{maxHeight: 250}}>
                {forceSyncDevices.map((device, i) => (
                  <TouchableOpacity key={i} style={[styles.targetOption, {marginBottom: 8}]} onPress={() => executeForcedSync([device.key])}>
                    <View style={{width: 10, height: 10, borderRadius: 5, backgroundColor: device.IsOnline ? '#10B981' : '#4C5361', marginRight: 10}} />
                    <IconSymbol name={device.DeviceType === 'PC' ? 'laptopcomputer' : 'iphone'} size={22} color={device.IsOnline ? '#10B981' : '#4C5361'} />
                    <View style={{marginLeft: 10, flex: 1}}>
                      <Text style={{color: '#FFF', fontSize: 15, fontWeight: '600'}}>{device.DeviceName || device.key}</Text>
                      <View style={{flexDirection: 'row', alignItems: 'center', gap: 6, marginTop: 2}}>
                        <View style={{backgroundColor: connectionColors[getConnectionType(device, pcLocalIp)] + '22', borderRadius: 4, paddingHorizontal: 6, paddingVertical: 1}}>
                          <Text style={{color: connectionColors[getConnectionType(device, pcLocalIp)], fontSize: 10, fontWeight: '700'}}>{getConnectionType(device, pcLocalIp)}</Text>
                        </View>
                        <Text style={{color: device.IsOnline ? '#10B981' : '#8A8F98', fontSize: 11}}>{device.IsOnline ? 'Online' : 'Offline'}</Text>
                      </View>
                    </View>
                    <IconSymbol name="bolt.fill" size={16} color="#F59E0B" />
                  </TouchableOpacity>
                ))}
                {forceSyncDevices.length === 0 && (
                  <Text style={{color: '#8A8F98', textAlign: 'center', marginTop: 20}}>No devices registered yet.</Text>
                )}
              </ScrollView>

              <TouchableOpacity style={{backgroundColor: '#2A2F3A', paddingVertical: 14, borderRadius: 14, alignItems: 'center', marginTop: 12}} onPress={() => setIsForceSyncModalVisible(false)}>
                <Text style={{color: '#FFF', fontSize: 14, fontWeight: '600'}}>Cancel</Text>
              </TouchableOpacity>
            </View>
          </View>
        </Modal>

        <View style={styles.inputArea}>
          <TouchableOpacity style={styles.attachButton} onPress={pickImageAndSend} disabled={isSending}>
            <IconSymbol name="photo.on.rectangle.angled" size={24} color="#8A8F98" />
          </TouchableOpacity>
          <TouchableOpacity style={styles.attachButton} onPress={pickFileAndSend} disabled={isSending}>
            <IconSymbol name="paperclip" size={24} color="#8A8F98" />
          </TouchableOpacity>
          <TouchableOpacity style={styles.attachButton} onPress={() => setIsCameraOptionsVisible(true)} disabled={isSending}>
            <IconSymbol name="camera.fill" size={24} color="#8A8F98" />
          </TouchableOpacity>
          <TextInput
             style={styles.textInput}
             placeholder="Type or paste to send to PC..."
             placeholderTextColor="#4C5361"
             value={inputText}
             onChangeText={setInputText}
             multiline
          />
          <TouchableOpacity style={styles.sendButton} onPress={sendTextToPc} disabled={isSending || !inputText}>
            {isSending ? <ActivityIndicator color="#fff" /> : <IconSymbol name="arrow.up.circle.fill" size={36} color={inputText ? "#4A62EB" : "#2A2F3A"} />}
          </TouchableOpacity>
        </View>
      </KeyboardAvoidingView>

      {/* Expanded Image Modal */}
      <Modal visible={!!expandedImage} transparent={true} animationType="fade" onRequestClose={() => setExpandedImage(null)}>
         <View style={{flex: 1, backgroundColor: 'rgba(0,0,0,0.95)', justifyContent: 'center', alignItems: 'center'}}>
            <TouchableOpacity style={{position: 'absolute', top: 60, right: 20, zIndex: 10, padding: 10, backgroundColor: 'rgba(255,255,255,0.15)', borderRadius: 20, width: 44, height: 44, alignItems: 'center', justifyContent: 'center'}} onPress={() => setExpandedImage(null)}>
               <IconSymbol name="xmark" size={24} color="#FFF" />
            </TouchableOpacity>
            
            {expandedImage && (
               <Image source={{uri: expandedImage, headers: { 'X-Advance-Client': 'MobileCompanion' }}} style={{width: '100%', height: '80%'}} contentFit="contain" />
            )}

            {expandedImage && (
               <View style={{position: 'absolute', bottom: 50, flexDirection: 'row', gap: 30, zIndex: 10}}>
                  <TouchableOpacity style={{backgroundColor: 'rgba(255,255,255,0.15)', borderRadius: 30, width: 60, height: 60, alignItems: 'center', justifyContent: 'center'}} onPress={async () => {
                      if (Platform.OS === 'web') return;
                      try {
                          const safeName = `image_${Date.now()}.jpg`;
                          const localUri = (FileSystem as any).documentDirectory + safeName;
                          const dl = await FileSystem.downloadAsync(expandedImage, localUri, { headers: { 'X-Advance-Client': 'MobileCompanion' } });
                          const perm = await MediaLibrary.requestPermissionsAsync();
                          if (perm.status === 'granted') {
                              await MediaLibrary.saveToLibraryAsync(dl.uri);
                              if (Platform.OS === 'android') ToastAndroid.show("Saved to Gallery", ToastAndroid.SHORT);
                          }
                      } catch(e) {}
                  }}>
                     <IconSymbol name="arrow.down" size={26} color="#FFF" />
                  </TouchableOpacity>

                  <TouchableOpacity style={{backgroundColor: '#4A62EB', borderRadius: 30, width: 60, height: 60, alignItems: 'center', justifyContent: 'center'}} onPress={async () => {
                      if (Platform.OS === 'web') return;
                      try {
                          const safeName = `image_share_${Date.now()}.jpg`;
                          const localUri = (FileSystem as any).cacheDirectory + safeName;
                          const dl = await FileSystem.downloadAsync(expandedImage, localUri, { headers: { 'X-Advance-Client': 'MobileCompanion' } });
                          if (await Sharing.isAvailableAsync()) {
                              await Sharing.shareAsync(dl.uri);
                          }
                      } catch(e) {}
                  }}>
                     <IconSymbol name="square.and.arrow.up" size={26} color="#FFF" />
                  </TouchableOpacity>
               </View>
            )}
         </View>
      </Modal>

    </SafeAreaView>
  );
}

const styles = StyleSheet.create({
  container: {
    flex: 1,
    backgroundColor: '#0F1115',
  },
  header: {
    paddingTop: 15,
    paddingHorizontal: 24,
    marginBottom: 20,
    flexDirection: 'row',
    justifyContent: 'space-between',
    alignItems: 'center',
  },
  title: {
    fontSize: 28,
    fontWeight: '800',
    color: '#FFFFFF',
    letterSpacing: -0.5,
  },
  statusRow: {
    flexDirection: 'row',
    alignItems: 'center',
    backgroundColor: '#1C1F26',
    paddingHorizontal: 12,
    paddingVertical: 6,
    borderRadius: 20,
  },
  indicator: {
    width: 6,
    height: 6,
    borderRadius: 3,
    marginRight: 6,
  },
  statusText: {
    color: '#8A8F98',
    fontSize: 10,
    fontWeight: '600',
    textTransform: 'uppercase',
  },
  feedContainer: {
    flex: 1,
    paddingHorizontal: 20,
  },
  clipCard: {
    backgroundColor: '#1C1F26',
    borderRadius: 16,
    padding: 12,
    marginBottom: 12,
    borderWidth: 1,
    borderColor: '#2A2F3A',
    flexDirection: 'row',
    alignItems: 'center',
  },
  clipIconContainer: {
    width: 48,
    height: 48,
    borderRadius: 12,
    backgroundColor: '#0F1115',
    justifyContent: 'center',
    alignItems: 'center',
    marginRight: 12,
  },
  clipContentContainer: {
    flex: 1,
    justifyContent: 'center',
  },
  clipActionsRow: {
    flexDirection: 'row',
    alignItems: 'center',
    marginLeft: 8,
  },
  clipType: {
    color: '#3B82F6',
    fontSize: 12,
    fontWeight: '700',
  },
  clipTime: {
    color: '#8A8F98',
    fontSize: 12,
  },
  clipTitle: {
    color: '#FFFFFF',
    fontSize: 15,
    fontWeight: '600',
    marginBottom: 4,
  },
  actionBtnIcon: {
    width: 40,
    height: 40,
    borderRadius: 8,
    justifyContent: 'center',
    alignItems: 'center',
    marginLeft: 8,
  },
  emptyText: {
    color: '#4C5361',
    textAlign: 'center',
    marginTop: 50,
  },
  inputArea: {
    flexDirection: 'row',
    paddingHorizontal: 20,
    paddingVertical: 15,
    backgroundColor: '#1C1F26',
    borderTopWidth: 1,
    borderTopColor: '#2A2F3A',
    alignItems: 'flex-end',
  },
  textInput: {
    flex: 1,
    backgroundColor: '#0F1115',
    color: '#FFFFFF',
    borderRadius: 20,
    paddingHorizontal: 20,
    paddingTop: 15,
    paddingBottom: 15,
    fontSize: 15,
    maxHeight: 120,
    borderWidth: 1,
    borderColor: '#2A2F3A',
  },
  sendButton: {
    marginLeft: 12,
    marginBottom: 5,
  },
  attachButton: {
    marginRight: 12,
    marginBottom: 10,
    justifyContent: 'center',
    alignItems: 'center',
  },
  modalOverlay: {
    flex: 1,
    backgroundColor: 'rgba(0,0,0,0.85)',
    justifyContent: 'center',
    alignItems: 'center',
    padding: 20,
  },
  modalContent: {
    backgroundColor: '#1C1F26',
    borderRadius: 20,
    padding: 24,
    width: '100%',
    maxWidth: 400,
    borderWidth: 1,
    borderColor: '#2A2F3A',
  },
  modalTitle: {
    fontSize: 24,
    fontWeight: '800',
    color: '#FFF',
    marginBottom: 8,
  },
  modalSubtitle: {
    fontSize: 14,
    color: '#8A8F98',
    marginBottom: 24,
    lineHeight: 20,
  },
  modalInput: {
    backgroundColor: '#0F1115',
    color: '#FFF',
    padding: 16,
    borderRadius: 12,
    fontSize: 16,
    borderWidth: 1,
    borderColor: '#2A2F3A',
    marginBottom: 20,
  },
  modalButton: {
    backgroundColor: '#4A62EB',
    padding: 16,
    borderRadius: 12,
    alignItems: 'center',
  },
  modalButtonText: {
    color: '#FFF',
    fontSize: 16,
    fontWeight: '700',
  },
  targetOption: {
    flexDirection: 'row',
    alignItems: 'center',
    paddingVertical: 12,
    paddingHorizontal: 16,
    backgroundColor: '#0F1115',
    borderRadius: 12,
    borderWidth: 1,
    borderColor: '#2A2F3A',
    marginBottom: 10,
  }
});
