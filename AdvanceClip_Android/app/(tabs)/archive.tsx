import React, { useState, useEffect, useRef } from 'react';
import { StyleSheet, Text, View, TouchableOpacity, SafeAreaView, ActivityIndicator, Dimensions, Modal, Alert, ScrollView, Image, Platform, FlatList, ToastAndroid, Linking, TextInput } from 'react-native';
import { IconSymbol } from '@/components/ui/icon-symbol';
import * as MediaLibrary from 'expo-media-library';
import * as FileSystem from 'expo-file-system/legacy';
import * as DocumentPicker from 'expo-document-picker';
import DateTimePicker from '@react-native-community/datetimepicker';
import AsyncStorage from '@react-native-async-storage/async-storage';
import { useSettings } from '../../context/SettingsContext';
import { database, storage } from '../../firebaseConfig';
import { ref as dbRef, push, set, onValue, query } from 'firebase/database';
import { ref as storageRef, uploadBytesResumable, getDownloadURL } from 'firebase/storage';

type DeviceGroup = { id: string; name: string; deviceNames: string[] };

const { width } = Dimensions.get('window');
const THUMB_SIZE = (width - 50) / 4;

type SourceFilter = 'Camera' | 'WhatsApp' | 'Downloads' | 'All';

export default function ConnectScreen() {
  const { pcLocalIp, deviceName, defaultTargetDeviceName } = useSettings();
  const [hasPermission, setHasPermission] = useState<boolean | null>(null);
  
  // Transfer state
  const [isPaused, setIsPaused] = useState(false);
  const isPausedRef = useRef(false);
  const isCancelledRef = useRef(false);
  
  // Date range — default 7 days
  const [startDate, setStartDate] = useState(new Date(Date.now() - 7 * 24 * 60 * 60 * 1000)); 
  const [endDate, setEndDate] = useState(new Date());
  const [showStartPicker, setShowStartPicker] = useState(false);
  const [showEndPicker, setShowEndPicker] = useState(false);
  
  // Media state
  const [mediaAssets, setMediaAssets] = useState<any[]>([]);
  const [isScanning, setIsScanning] = useState(false);
  const [activeTab, setActiveTab] = useState<'Images'|'Videos'|'PDFs'|'Docs'|'All'>('All');
  const [sourceFilter, setSourceFilter] = useState<SourceFilter>('All');
  const [selectedIds, setSelectedIds] = useState<Set<string>>(new Set());
  const [enlargedPreview, setEnlargedPreview] = useState<any>(null);
  
  // Upload state
  const [isUploading, setIsUploading] = useState(false);
  const [uploadIndex, setUploadIndex] = useState(0);
  const [uploadTotal, setUploadTotal] = useState(0);
  const [uploadProgress, setUploadProgress] = useState<Record<string, string>>({});
  
  // Device state — two categories
  const [localDevices, setLocalDevices] = useState<any[]>([]);
  const [globalDevices, setGlobalDevices] = useState<any[]>([]);
  const [allFirebaseDevices, setAllFirebaseDevices] = useState<any[]>([]);
  
  // Screen mode
  const [selectedTarget, setSelectedTarget] = useState<any>(null);
  const [browserFiles, setBrowserFiles] = useState<any[]>([]);
  const [urlPopup, setUrlPopup] = useState<{ device: any, localUrl: string, globalUrl: string } | null>(null);

  // Device Groups — synced via Firebase for cross-device access
  const [deviceGroups, setDeviceGroups] = useState<DeviceGroup[]>([]);
  const [showGroupModal, setShowGroupModal] = useState(false);
  const [editingGroup, setEditingGroup] = useState<DeviceGroup | null>(null);
  const [newGroupName, setNewGroupName] = useState('');
  const [selectedGroupDevices, setSelectedGroupDevices] = useState<Set<string>>(new Set());

  // Real-time Firebase sync for groups
  useEffect(() => {
    const groupsRef = dbRef(database, 'device_groups');
    const unsubGroups = onValue(groupsRef, (snapshot) => {
      if (snapshot.exists()) {
        const data = snapshot.val();
        const groups = Object.keys(data).map(k => ({ ...data[k], id: k }));
        setDeviceGroups(groups);
      } else {
        setDeviceGroups([]);
      }
    });
    return () => unsubGroups();
  }, []);

  const saveGroupToFirebase = async (group: DeviceGroup) => {
    const groupRef = dbRef(database, `device_groups/${group.id}`);
    await set(groupRef, { name: group.name, deviceNames: group.deviceNames });
  };

  const createOrUpdateGroup = async () => {
    if (!newGroupName.trim()) { Alert.alert('Error', 'Group name is required'); return; }
    const deviceNames = Array.from(selectedGroupDevices);
    if (deviceNames.length === 0) { Alert.alert('Error', 'Select at least one device'); return; }
    const groupId = editingGroup ? editingGroup.id : `grp_${Date.now()}`;
    await saveGroupToFirebase({ id: groupId, name: newGroupName.trim(), deviceNames });
    setShowGroupModal(false);
    setEditingGroup(null);
    setNewGroupName('');
    setSelectedGroupDevices(new Set());
    if (Platform.OS === 'android') ToastAndroid.show(`Group "${newGroupName.trim()}" saved`, ToastAndroid.SHORT);
  };

  const deleteGroup = (groupId: string) => {
    Alert.alert('Delete Group', 'Are you sure?', [
      { text: 'Cancel' },
      { text: 'Delete', style: 'destructive', onPress: async () => {
        const groupRef = dbRef(database, `device_groups/${groupId}`);
        await set(groupRef, null);
      }}
    ]);
  };

  // Device discovery — hybrid: direct LAN probe + Firebase fallback
  useEffect(() => {
    // Direct LAN probe using pcLocalIp from settings — most reliable, no Firebase needed
    const probeLocalPc = async (): Promise<any | null> => {
      if (!pcLocalIp) return null;
      const candidates: string[] = [];
      const rawIp = pcLocalIp.trim();
      if (rawIp.startsWith('http')) {
        candidates.push(rawIp.endsWith('/') ? rawIp.slice(0, -1) : rawIp);
      } else {
        const withPort = rawIp.includes(':') ? rawIp : `${rawIp}:8999`;
        candidates.push(`http://${withPort}`);
      }
      const baseIp = rawIp.replace(/^https?:\/\//, '').split(':')[0];
      if (baseIp && !candidates.includes(`http://${baseIp}:8999`)) {
        candidates.push(`http://${baseIp}:8999`);
      }
      for (const url of candidates) {
        try {
          const res = await fetch(`${url}/api/health`, { 
            method: 'GET', 
            headers: { 'X-Advance-Client': 'MobileCompanion' }, 
            signal: AbortSignal.timeout(2000) 
          });
          if (res.ok) {
            return { 
              DeviceName: 'PC', DeviceType: 'PC', IsOnline: true, 
              resolvedUrl: url, connectionType: 'local', firebaseKey: 'local_direct',
              Url: url, LocalIp: url
            };
          }
        } catch(e) {}
      }
      return null;
    };

    const nodesRef = query(dbRef(database, 'active_devices'));
    const unsubscribeNodes = onValue(nodesRef, async (snapshot) => {
      const locals: any[] = [];
      const globals: any[] = [];
      const directPc = await probeLocalPc();
      
      let allDevs: any[] = [];
      if (snapshot.exists()) {
        const data = snapshot.val();
        allDevs = Object.keys(data).map(k => ({ ...data[k], firebaseKey: k })).filter(d => d.IsOnline);
        setAllFirebaseDevices(allDevs);
      } else {
        setAllFirebaseDevices([]);
      }
      
      for (const dev of allDevs) {
        let localReachable = false;
        let resolvedLocalUrl = '';
        
        if (directPc && dev.DeviceType === 'PC') {
          localReachable = true;
          resolvedLocalUrl = directPc.resolvedUrl;
          directPc.DeviceName = dev.DeviceName || directPc.DeviceName;
          directPc.GlobalUrl = dev.GlobalUrl;
        } else {
          const localCandidates: string[] = [];
          if (dev.Url) {
            dev.Url.split(',').forEach((u: string) => {
              const cleaned = u.trim();
              if (cleaned.startsWith('http') && !cleaned.includes('trycloudflare.com')) {
                const noTrail = cleaned.endsWith('/') ? cleaned.slice(0, -1) : cleaned;
                if (!localCandidates.includes(noTrail)) localCandidates.push(noTrail);
              }
            });
          }
          if (dev.LocalIp) {
            dev.LocalIp.split(',').forEach((ip: string) => {
              const clean = ip.trim().startsWith('http') ? ip.trim() : `http://${ip.trim()}`;
              const noTrail = clean.endsWith('/') ? clean.slice(0, -1) : clean;
              if (!localCandidates.includes(noTrail)) localCandidates.push(noTrail);
            });
          }
          for (const url of localCandidates) {
            try {
              const res = await fetch(`${url}/api/health`, { method: 'GET', headers: { 'X-Advance-Client': 'MobileCompanion' }, signal: AbortSignal.timeout(2000) });
              if (res.ok) { localReachable = true; resolvedLocalUrl = url; break; }
            } catch(e) {}
          }
        }
        
        if (localReachable && !locals.find(l => l.DeviceName === dev.DeviceName)) {
          locals.push({ ...dev, resolvedUrl: resolvedLocalUrl, connectionType: 'local' });
        }
        
        const hasCloudflare = dev.GlobalUrl && dev.GlobalUrl.includes('trycloudflare.com');
        if (hasCloudflare && !localReachable) {
          try {
            const cfUrl = dev.GlobalUrl.endsWith('/') ? dev.GlobalUrl.slice(0, -1) : dev.GlobalUrl;
            const res = await fetch(`${cfUrl}/api/health`, { method: 'GET', headers: { 'X-Advance-Client': 'MobileCompanion' }, signal: AbortSignal.timeout(3000) });
            if (res.ok) {
              globals.push({ ...dev, resolvedUrl: cfUrl, connectionType: 'cloudflare' });
            } else {
              globals.push({ ...dev, resolvedUrl: cfUrl, connectionType: 'cloudflare-unverified' });
            }
          } catch {
            const cfUrl = dev.GlobalUrl.endsWith('/') ? dev.GlobalUrl.slice(0, -1) : dev.GlobalUrl;
            globals.push({ ...dev, resolvedUrl: cfUrl, connectionType: 'cloudflare-unverified' });
          }
        } else if (!localReachable && !hasCloudflare) {
          globals.push({ ...dev, resolvedUrl: '', connectionType: 'sync-only' });
        }
      }
      
      // If direct probe found PC but no Firebase entry matched, add as standalone
      if (directPc && !locals.find(l => l.DeviceType === 'PC')) {
        locals.push(directPc);
      }
      
      setLocalDevices(locals);
      setGlobalDevices(globals);
    });
    
    return () => unsubscribeNodes();
  }, [deviceName, pcLocalIp]);

  // Permissions
  useEffect(() => {
    (async () => {
      try {
        const { status } = await MediaLibrary.requestPermissionsAsync(false, ['photo', 'video']);
        setHasPermission(status === 'granted');
      } catch { setHasPermission(false); }
    })();
  }, []);

  // Media scan
  const scanMedia = async () => {
    if (hasPermission === false) return;
    setIsScanning(true);
    setMediaAssets([]);
    setSelectedIds(new Set());
    setBrowserFiles([]);
    
    try {
      let allFound: any[] = [];
      let hasNextPage = true;
      let after = undefined;

      // Gallery scan
      while (hasNextPage) {
        let media = await MediaLibrary.getAssetsAsync({
          first: 100,
          after: after,
          mediaType: ['photo', 'video'],
          createdAfter: startDate.getTime(),
          createdBefore: endDate.getTime(),
          sortBy: [[MediaLibrary.SortBy.creationTime, false]]
        });
        allFound = [...allFound, ...media.assets.map(a => ({ ...a, source: 'Camera' }))];
        hasNextPage = media.hasNextPage;
        after = media.endCursor;
      }

      // File system scan for WhatsApp, Downloads, Documents
      if (Platform.OS !== 'web') {
        const sourceRoots: { path: string, source: SourceFilter }[] = [
          { path: 'file:///storage/emulated/0/WhatsApp/Media/WhatsApp Images/', source: 'WhatsApp' },
          { path: 'file:///storage/emulated/0/WhatsApp/Media/WhatsApp Images/Sent/', source: 'WhatsApp' },
          { path: 'file:///storage/emulated/0/Android/media/com.whatsapp/WhatsApp/Media/WhatsApp Images/', source: 'WhatsApp' },
          { path: 'file:///storage/emulated/0/Android/media/com.whatsapp/WhatsApp/Media/WhatsApp Video/', source: 'WhatsApp' },
          { path: 'file:///storage/emulated/0/Download/', source: 'Downloads' },
          { path: 'file:///storage/emulated/0/Documents/', source: 'Downloads' },
        ];

        for (const { path: rawRoot, source } of sourceRoots) {
          try {
            const check = await FileSystem.getInfoAsync(rawRoot);
            if (check.exists && check.isDirectory) {
              const files = await FileSystem.readDirectoryAsync(rawRoot);
              for (const file of files) {
                if (file === '.nomedia' || file.startsWith('.')) continue;
                const fullPath = rawRoot + file;
                try {
                  const fInfo = await FileSystem.getInfoAsync(fullPath);
                  if (fInfo.exists && !fInfo.isDirectory) {
                    const modTimeMs = (fInfo.modificationTime || 0) * 1000;
                    if (modTimeMs >= startDate.getTime() && modTimeMs <= endDate.getTime()) {
                      const lowerFile = file.toLowerCase();
                      let mediaType = 'photo';
                      if (lowerFile.endsWith('.pdf')) mediaType = 'pdf';
                      else if (lowerFile.match(/\.(doc|docx|txt|xlsx|pptx)$/)) mediaType = 'doc';
                      else if (lowerFile.match(/\.(mp4|avi|mkv|mov|3gp)$/)) mediaType = 'video';
                      else if (lowerFile.match(/\.(apk|zip|rar|7z|tar|gz)$/)) mediaType = 'doc';
                      
                      allFound.push({ id: fullPath, uri: fullPath, filename: file, creationTime: modTimeMs, mediaType, source });
                    }
                  }
                } catch {}
              }
            }
          } catch {}
        }
      }

      const uniqueAssets = Array.from(new Map(allFound.map(item => [item.id, item])).values());
      uniqueAssets.sort((a, b) => b.creationTime - a.creationTime);
      setMediaAssets(uniqueAssets);
    } catch (e) { console.error(e); }
    setIsScanning(false);
  };

  // Browse Android files
  const browseFiles = async () => {
    try {
      const result = await DocumentPicker.getDocumentAsync({ multiple: true, copyToCacheDirectory: true });
      if (!result.canceled && result.assets && result.assets.length > 0) {
        const newFiles = result.assets.map((f: any) => ({
          id: `browse_${f.uri}_${Date.now()}`,
          uri: f.uri,
          filename: f.name || 'file',
          creationTime: Date.now(),
          mediaType: f.mimeType?.includes('image') ? 'photo' : f.mimeType?.includes('video') ? 'video' : f.mimeType?.includes('pdf') ? 'pdf' : 'doc',
          source: 'Browse' as any,
          fileSize: f.size || 0,
        }));
        setBrowserFiles(prev => [...prev, ...newFiles]);
        // Auto-select browsed files
        setSelectedIds(prev => {
          const updated = new Set(prev);
          newFiles.forEach((f: any) => updated.add(f.id));
          return updated;
        });
        if (Platform.OS === 'android') ToastAndroid.show(`Added ${newFiles.length} file(s)`, ToastAndroid.SHORT);
      }
    } catch (err) {
      Alert.alert('Browse Failed', 'Could not open file picker.');
    }
  };

  // Build date range folder name
  const buildBatchName = () => {
    const sender = deviceName || 'Mobile';
    const from = startDate.toLocaleDateString('en-US', { month: 'short', day: 'numeric' }).replace(/\s/g, '');
    const to = endDate.toLocaleDateString('en-US', { month: 'short', day: 'numeric' }).replace(/\s/g, '');
    return `${sender}_${from}_to_${to}`;
  };

  // Execute transfer
  const executeTransfer = async (targetNode: any) => {
    const allItems = [...getFilteredAssets(), ...browserFiles];
    const targetQueue = allItems.filter(a => selectedIds.has(a.id));
    
    if (targetQueue.length === 0) {
      Alert.alert("No Items", "Select items to send first.");
      return;
    }

    // Find a PC relay if target has no direct route
    let resolvedUrl = targetNode.resolvedUrl;
    let useRelay = false;
    
    if (targetNode.connectionType === 'sync-only' || !resolvedUrl) {
      // Find any PC with Cloudflare to relay through
      const allDevs = [...localDevices, ...globalDevices];
      const relayPC = allDevs.find(d => d.DeviceType === 'PC' && d.resolvedUrl && d.connectionType !== 'sync-only');
      
      if (relayPC) {
        resolvedUrl = relayPC.resolvedUrl;
        useRelay = true;
        if (Platform.OS === 'android') ToastAndroid.show(`📡 Relaying via ${relayPC.DeviceName}`, ToastAndroid.SHORT);
      } else {
        Alert.alert("No Route Available", "No PC with Cloudflare is online to relay files.\n\nEnsure at least one PC is running AdvanceClip with internet access.");
        return;
      }
    }
    
    if (!resolvedUrl) {
      Alert.alert("Route Failed", "No reachable URL for this device.");
      return;
    }

    setIsUploading(true);
    setUploadIndex(0);
    setUploadTotal(targetQueue.length);
    isCancelledRef.current = false;
    isPausedRef.current = false;
    setIsPaused(false);
    setUploadProgress({});

    const batchName = buildBatchName();
    const isCloudflare = useRelay || targetNode.connectionType === 'cloudflare' || targetNode.connectionType === 'cloudflare-unverified';
    const CHUNK_SIZE = 50 * 1024 * 1024; // 50MB chunks (under Cloudflare 100MB limit)

    const processUpload = async (asset: any): Promise<void> => {
      if (isCancelledRef.current) return;
      while (isPausedRef.current) {
        if (isCancelledRef.current) return;
        await new Promise(resolve => setTimeout(resolve, 500));
      }
      if (isCancelledRef.current) return;

      setUploadIndex(prev => prev + 1);
      setUploadProgress(prev => ({ ...prev, [asset.id]: 'sending' }));

      let attempt = 0;
      let success = false;

      while (attempt < 3 && !success && !isCancelledRef.current) {
        attempt++;
        try {
          let finalUploadUri = asset.uri;
          
          // Handle content:// URIs
          if (asset.uri.startsWith('content://') || (!asset.uri.startsWith('file://') && !asset.uri.startsWith('http'))) {
            if (asset.id && !asset.id.startsWith('browse_')) {
              const assetInfo = await MediaLibrary.getAssetInfoAsync(asset.id);
              finalUploadUri = assetInfo.localUri || assetInfo.uri;
            }
          }
          if (finalUploadUri.startsWith('content://')) {
            const safeName = `transfer_${Date.now()}_` + (asset.filename || 'file.bin').replace(/[^a-zA-Z0-9.-]/g, '_');
            const cachePath = `${FileSystem.cacheDirectory}${safeName}`;
            await FileSystem.copyAsync({ from: finalUploadUri, to: cachePath });
            finalUploadUri = cachePath;
          }

          const fileInfo = await FileSystem.getInfoAsync(finalUploadUri);
          if (fileInfo.exists) {
            const fileSize = (fileInfo as any).size || 0;
            const uploadEndpoint = useRelay ? 'relay_upload' : 'archive_upload';
            
            // Use chunked upload for large files over Cloudflare (>50MB)
            if (isCloudflare && fileSize > CHUNK_SIZE) {
              await uploadChunked(resolvedUrl, finalUploadUri, asset, batchName, fileSize);
            } else {
              // Single-POST for LAN or small files
              const uploadUrl = `${resolvedUrl}/api/${uploadEndpoint}`;
              const response = await FileSystem.uploadAsync(uploadUrl, finalUploadUri, {
                httpMethod: 'POST',
                uploadType: FileSystem.FileSystemUploadType.BINARY_CONTENT,
                headers: {
                  'X-Advance-Client': 'MobileCompanion',
                  'X-Original-Date': (asset.creationTime || Date.now()).toString(),
                  'X-File-Name': encodeURIComponent(asset.filename || 'file.bin'),
                  'X-Batch-Name': encodeURIComponent(batchName),
                  'X-Source-Device': deviceName || 'Android',
                }
              });
              if (response.status !== 200) throw new Error("HTTP " + response.status);
            }
            setUploadProgress(prev => ({ ...prev, [asset.id]: 'done' }));
            success = true;
          }
        } catch (error) {
          if (attempt === 3) {
            setUploadProgress(prev => ({ ...prev, [asset.id]: 'error' }));
          }
        }
      }
    };

    // Chunked upload: split file into 50MB chunks, send each, then finalize
    const uploadChunked = async (baseUrl: string, fileUri: string, asset: any, batch: string, totalSize: number) => {
      const sessionId = `${Date.now()}_${Math.random().toString(36).substring(2, 10)}`;
      const totalChunks = Math.ceil(totalSize / CHUNK_SIZE);

      for (let i = 0; i < totalChunks; i++) {
        if (isCancelledRef.current) throw new Error('Cancelled');
        while (isPausedRef.current) {
          if (isCancelledRef.current) throw new Error('Cancelled');
          await new Promise(r => setTimeout(r, 500));
        }

        // Read chunk from file
        const offset = i * CHUNK_SIZE;
        const length = Math.min(CHUNK_SIZE, totalSize - offset);
        
        // Read chunk as base64, write to temp file, upload temp file
        const chunkB64 = await FileSystem.readAsStringAsync(fileUri, {
          encoding: FileSystem.EncodingType.Base64,
          position: offset,
          length: length,
        });
        const chunkTempUri = `${FileSystem.cacheDirectory}chunk_${sessionId}_${i}`;
        await FileSystem.writeAsStringAsync(chunkTempUri, chunkB64, { encoding: FileSystem.EncodingType.Base64 });

        // Upload chunk
        let chunkAttempt = 0;
        let chunkDone = false;
        while (chunkAttempt < 3 && !chunkDone) {
          chunkAttempt++;
          try {
            const res = await FileSystem.uploadAsync(`${baseUrl}/api/upload_chunk`, chunkTempUri, {
              httpMethod: 'POST',
              uploadType: FileSystem.FileSystemUploadType.BINARY_CONTENT,
              headers: {
                'X-Advance-Client': 'MobileCompanion',
                'X-Upload-Session': sessionId,
                'X-Chunk-Index': i.toString(),
              }
            });
            if (res.status === 200) chunkDone = true;
            else throw new Error(`Chunk ${i} failed: HTTP ${res.status}`);
          } catch (e) {
            if (chunkAttempt === 3) throw e;
            await new Promise(r => setTimeout(r, 1000));
          }
        }

        // Clean up temp chunk file
        try { await FileSystem.deleteAsync(chunkTempUri, { idempotent: true }); } catch {}

        // Update progress text
        setUploadProgress(prev => ({ ...prev, [asset.id]: `chunk ${i + 1}/${totalChunks}` }));
      }

      // Finalize — tell PC to merge all chunks
      const finRes = await fetch(`${baseUrl}/api/upload_finalize`, {
        method: 'POST',
        headers: {
          'X-Advance-Client': 'MobileCompanion',
          'X-Upload-Session': sessionId,
          'X-File-Name': encodeURIComponent(asset.filename || 'file.bin'),
          'X-Batch-Name': encodeURIComponent(batch),
          'X-Original-Date': (asset.creationTime || Date.now()).toString(),
          'X-Total-Chunks': totalChunks.toString(),
        }
      });
      if (!finRes.ok) throw new Error(`Finalize failed: ${finRes.status}`);
    };

    // Concurrent workers
    const workers = [];
    let currentIndex = 0;
    const CONCURRENCY = 2;
    for (let i = 0; i < Math.min(CONCURRENCY, targetQueue.length); i++) {
      workers.push((async function worker() {
        while (currentIndex < targetQueue.length) {
          if (isCancelledRef.current) break;
          const asset = targetQueue[currentIndex++];
          await processUpload(asset);
        }
      })());
    }
    await Promise.all(workers);

    setIsUploading(false);
    if (isCancelledRef.current) {
      Alert.alert('Cancelled', 'Transfer was cancelled.');
    } else {
      setTimeout(() => {
        setSelectedIds(new Set());
        setUploadProgress({});
        setBrowserFiles([]);
        Alert.alert('Transfer Complete ✅', `Sent ${targetQueue.length} items to ${targetNode.DeviceName}`);
      }, 1000);
    }
  };

  const toggleSelection = (id: string) => {
    setSelectedIds(prev => {
      const updated = new Set(prev);
      updated.has(id) ? updated.delete(id) : updated.add(id);
      return updated;
    });
  };

  const toggleSelectAll = (items: any[]) => {
    setSelectedIds(prev => {
      const updated = new Set(prev);
      const allSelected = items.every(i => updated.has(i.id));
      items.forEach(i => allSelected ? updated.delete(i.id) : updated.add(i.id));
      return updated;
    });
  };

  const getFilteredAssets = () => {
    let items = mediaAssets;
    // Source filter
    if (sourceFilter !== 'All') {
      items = items.filter(a => a.source === sourceFilter);
    }
    // Type filter
    if (activeTab !== 'All') {
      items = items.filter(a => {
        if (activeTab === 'Images') return a.mediaType === 'photo';
        if (activeTab === 'Videos') return a.mediaType === 'video';
        if (activeTab === 'PDFs') return a.mediaType === 'pdf';
        if (activeTab === 'Docs') return a.mediaType === 'doc';
        return true;
      });
    }
    return items;
  };

  // ─── DEVICE CARD COMPONENT ───
  const DeviceCard = ({ device, type }: { device: any, type: 'local' | 'global' }) => {
    const isPC = device.DeviceType === 'PC';
    const hasCloudflare = device.connectionType === 'cloudflare';
    const isSyncOnly = device.connectionType === 'sync-only';
    
    const getDeviceUrls = () => {
      let localUrl = '';
      let globalUrl = '';
      if (device.Url) {
        const candidates = device.Url.split(',').map((u: string) => u.trim()).filter((u: string) => u.startsWith('http'));
        localUrl = candidates.find((u: string) => !u.includes('trycloudflare.com')) || '';
      }
      if (device.GlobalUrl && device.GlobalUrl.includes('trycloudflare.com')) {
        globalUrl = device.GlobalUrl.endsWith('/') ? device.GlobalUrl.slice(0, -1) : device.GlobalUrl;
      }
      return { localUrl, globalUrl };
    };
    
    return (
      <TouchableOpacity 
        style={[s.deviceCard, { borderColor: type === 'local' ? '#10B98144' : '#3B82F644' }]}
        onPress={() => { setSelectedTarget(device); if (!isScanning && mediaAssets.length === 0) scanMedia(); }}
        onLongPress={() => {
          const { localUrl, globalUrl } = getDeviceUrls();
          if (localUrl || globalUrl) setUrlPopup({ device, localUrl, globalUrl });
          else if (Platform.OS === 'android') ToastAndroid.show('No URLs available', ToastAndroid.SHORT);
        }}
        activeOpacity={0.7}
      >
        <View style={[s.deviceIcon, { backgroundColor: type === 'local' ? '#10B98118' : '#3B82F618' }]}>
          <IconSymbol name={isPC ? "desktopcomputer" : "iphone"} size={22} color={type === 'local' ? '#10B981' : '#3B82F6'} />
        </View>
        <View style={{ flex: 1, marginLeft: 12 }}>
          <Text style={s.deviceName}>{device.DeviceName || 'Unknown'}</Text>
          <View style={{ flexDirection: 'row', alignItems: 'center', gap: 6, marginTop: 3 }}>
            {type === 'local' && (
              <View style={[s.badge, { backgroundColor: '#10B98122' }]}>
                <Text style={[s.badgeText, { color: '#10B981' }]}>⚡ LAN</Text>
              </View>
            )}
            {hasCloudflare && (
              <View style={[s.badge, { backgroundColor: '#3B82F622' }]}>
                <Text style={[s.badgeText, { color: '#3B82F6' }]}>☁️ Cloudflare</Text>
              </View>
            )}
            {isSyncOnly && (
              <View style={[s.badge, { backgroundColor: '#F59E0B22' }]}>
                <Text style={[s.badgeText, { color: '#F59E0B' }]}>📡 Sync Only</Text>
              </View>
            )}
            <Text style={{ color: '#555', fontSize: 10 }}>{device.DeviceType}</Text>
          </View>
        </View>
        <IconSymbol name="chevron.right" size={16} color="#4A5568" />
      </TouchableOpacity>
    );
  };

  // ─── UPLOAD PROGRESS SCREEN ───
  if (isUploading) {
    const pct = uploadTotal > 0 ? Math.round((uploadIndex / uploadTotal) * 100) : 0;
    return (
      <SafeAreaView style={s.container}>
        <View style={s.header}>
          <Text style={s.title}>Transferring...</Text>
          <Text style={s.subtitle}>{selectedTarget?.DeviceName || 'Device'}</Text>
        </View>
        <View style={s.card}>
          <Text style={{ color: '#FFF', fontSize: 16, fontWeight: '700', marginBottom: 6 }}>{uploadIndex} / {uploadTotal} files</Text>
          <Text style={{ color: '#8A8F98', fontSize: 12, marginBottom: 16 }}>{pct}% complete</Text>
          <View style={{ height: 8, backgroundColor: '#2A2F3A', borderRadius: 4, overflow: 'hidden', marginBottom: 24 }}>
            <View style={{ height: '100%', width: `${pct}%`, backgroundColor: '#10B981', borderRadius: 4 }} />
          </View>
          <View style={{ flexDirection: 'row', gap: 12 }}>
            <TouchableOpacity style={[s.controlBtn, { backgroundColor: isPaused ? '#10B981' : '#F59E0B', flex: 1 }]} onPress={() => { isPausedRef.current = !isPausedRef.current; setIsPaused(isPausedRef.current); }}>
              <Text style={s.controlBtnText}>{isPaused ? '▶ Resume' : '⏸ Pause'}</Text>
            </TouchableOpacity>
            <TouchableOpacity style={[s.controlBtn, { backgroundColor: '#EF4444', flex: 1 }]} onPress={() => { isCancelledRef.current = true; }}>
              <Text style={s.controlBtnText}>✕ Abort</Text>
            </TouchableOpacity>
          </View>
        </View>
      </SafeAreaView>
    );
  }

  // ─── TRANSFER PANEL (after device selected) ───
  if (selectedTarget) {
    const filteredAssets = getFilteredAssets();
    const allDisplayItems = [...filteredAssets, ...browserFiles];

    return (
      <SafeAreaView style={s.container}>
        {/* Header with prominent back */}
        <View style={[s.header, { flexDirection: 'row', alignItems: 'center', paddingTop: 50 }]}>
          <TouchableOpacity onPress={() => { setSelectedTarget(null); setMediaAssets([]); setBrowserFiles([]); setSelectedIds(new Set()); }} style={{ marginRight: 14, padding: 10, backgroundColor: '#EF4444', borderRadius: 12 }}>
            <IconSymbol name="chevron.left" size={18} color="#FFF" />
          </TouchableOpacity>
          <View style={{ flex: 1 }}>
            <Text style={[s.title, { fontSize: 22 }]}>Send to {selectedTarget.DeviceName}</Text>
            <View style={{ flexDirection: 'row', alignItems: 'center', gap: 6, marginTop: 3 }}>
              <View style={{ width: 8, height: 8, borderRadius: 4, backgroundColor: selectedTarget.connectionType === 'local' ? '#10B981' : '#3B82F6' }} />
              <Text style={{ color: '#8A8F98', fontSize: 11 }}>{selectedTarget.connectionType === 'local' ? 'Local Network' : selectedTarget.connectionType === 'cloudflare' ? 'Via Cloudflare' : 'Global Sync'}</Text>
            </View>
          </View>
        </View>

        {/* Date Range */}
        <View style={{ flexDirection: 'row', alignItems: 'center', paddingHorizontal: 20, marginBottom: 10, gap: 8 }}>
          <TouchableOpacity style={s.dateBtn} onPress={() => setShowStartPicker(true)}>
            <Text style={s.dateLabel}>FROM</Text>
            <Text style={s.dateValue}>{startDate.toLocaleDateString()}</Text>
          </TouchableOpacity>
          <TouchableOpacity style={s.dateBtn} onPress={() => setShowEndPicker(true)}>
            <Text style={s.dateLabel}>TO</Text>
            <Text style={s.dateValue}>{endDate.toLocaleDateString()}</Text>
          </TouchableOpacity>
          <TouchableOpacity style={{ backgroundColor: '#4A62EB', borderRadius: 12, padding: 12, paddingHorizontal: 16 }} onPress={scanMedia}>
            <Text style={{ color: '#FFF', fontSize: 13, fontWeight: '700' }}>Scan</Text>
          </TouchableOpacity>
        </View>
        {showStartPicker && <DateTimePicker value={startDate} mode="date" display="default" onChange={(e: any, d?: Date) => { setShowStartPicker(false); if (d) setStartDate(d); }} />}
        {showEndPicker && <DateTimePicker value={endDate} mode="date" display="default" onChange={(e: any, d?: Date) => { setShowEndPicker(false); if (d) setEndDate(d); }} />}

        {/* Source Filters */}
        <ScrollView horizontal showsHorizontalScrollIndicator={false} contentContainerStyle={{ paddingHorizontal: 20, gap: 6, marginBottom: 8 }}>
          {(['All', 'Camera', 'WhatsApp', 'Downloads'] as SourceFilter[]).map(src => (
            <TouchableOpacity key={src} style={[s.sourceChip, sourceFilter === src && s.sourceChipActive]} onPress={() => setSourceFilter(src)}>
              <Text style={[s.sourceChipText, sourceFilter === src && s.sourceChipTextActive]}>
                {src === 'Camera' ? '📷' : src === 'WhatsApp' ? '💬' : src === 'Downloads' ? '📂' : '🌐'} {src}
              </Text>
            </TouchableOpacity>
          ))}
          <TouchableOpacity style={[s.sourceChip, { backgroundColor: '#4A62EB33', borderColor: '#4A62EB' }]} onPress={browseFiles}>
            <Text style={[s.sourceChipText, { color: '#4A62EB', fontWeight: '700' }]}>📁 Browse Android</Text>
          </TouchableOpacity>
        </ScrollView>

        {/* Type Tabs */}
        <View style={s.tabRow}>
          {(['All', 'Images', 'Videos', 'PDFs', 'Docs'] as const).map(t => (
            <TouchableOpacity key={t} style={[s.tab, activeTab === t && s.tabActive]} onPress={() => setActiveTab(t)}>
              <Text style={[s.tabText, activeTab === t && s.tabTextActive]}>{t}</Text>
            </TouchableOpacity>
          ))}
        </View>

        {/* Count + Select All */}
        <View style={{ flexDirection: 'row', justifyContent: 'space-between', alignItems: 'center', paddingHorizontal: 20, paddingVertical: 8 }}>
          <Text style={{ color: '#8A8F98', fontSize: 12, fontWeight: '600' }}>
            {allDisplayItems.length} found{browserFiles.length > 0 ? ` (+${browserFiles.length} browsed)` : ''} · {selectedIds.size} selected
          </Text>
          <TouchableOpacity style={{ backgroundColor: '#2A2F3A', paddingHorizontal: 12, paddingVertical: 6, borderRadius: 8 }} onPress={() => toggleSelectAll(allDisplayItems)}>
            <Text style={{ color: '#FFF', fontSize: 11, fontWeight: 'bold' }}>Select All</Text>
          </TouchableOpacity>
        </View>

        {/* Grid */}
        {isScanning ? (
          <ActivityIndicator size="large" color="#4A62EB" style={{ marginTop: 40 }} />
        ) : (
          <ScrollView contentContainerStyle={{ paddingHorizontal: 12, paddingBottom: 120 }}>
            <View style={{ flexDirection: 'row', flexWrap: 'wrap' }}>
              {allDisplayItems.map((asset, idx) => {
                const isSelected = selectedIds.has(asset.id);
                const isImage = asset.mediaType === 'photo' || asset.mediaType === 'video';
                return (
                  <TouchableOpacity key={idx} style={{ margin: 3, width: THUMB_SIZE, height: THUMB_SIZE }} onPress={() => toggleSelection(asset.id)} onLongPress={() => setEnlargedPreview(asset)}>
                    {isImage ? (
                      <Image source={{ uri: asset.uri }} style={{ width: '100%', height: '100%', borderRadius: 10, backgroundColor: '#2A2F3A' }} />
                    ) : (
                      <View style={{ width: '100%', height: '100%', borderRadius: 10, backgroundColor: '#1C1F26', alignItems: 'center', justifyContent: 'center', borderWidth: 1, borderColor: '#2A2F3A' }}>
                        <IconSymbol name="doc.fill" size={24} color={asset.mediaType === 'pdf' ? '#EF4444' : '#3B82F6'} />
                        <Text style={{ color: '#AAA', fontSize: 8, marginTop: 4, paddingHorizontal: 4 }} numberOfLines={2}>{asset.filename}</Text>
                      </View>
                    )}
                    <TouchableOpacity style={[s.checkCircle, isSelected && s.checkCircleActive]} onPress={() => toggleSelection(asset.id)}>
                      {isSelected && <IconSymbol name="checkmark" size={12} color="#FFF" />}
                    </TouchableOpacity>
                    {asset.mediaType === 'video' && (
                      <View style={{ position: 'absolute', bottom: 4, left: 4, backgroundColor: 'rgba(0,0,0,0.6)', paddingHorizontal: 5, paddingVertical: 2, borderRadius: 5 }}>
                        <IconSymbol name="play.fill" size={10} color="#FFF" />
                      </View>
                    )}
                    {asset.source === 'Browse' && (
                      <View style={{ position: 'absolute', top: 4, left: 4, backgroundColor: '#4A62EB', paddingHorizontal: 4, paddingVertical: 1, borderRadius: 4 }}>
                        <Text style={{ color: '#FFF', fontSize: 7, fontWeight: 'bold' }}>FILE</Text>
                      </View>
                    )}
                  </TouchableOpacity>
                );
              })}
              {allDisplayItems.length === 0 && !isScanning && (
                <View style={{ width: '100%', alignItems: 'center', marginTop: 40 }}>
                  <IconSymbol name="magnifyingglass" size={40} color="#4A5568" />
                  <Text style={{ color: '#6B7280', marginTop: 12, fontSize: 14 }}>No files found. Try scanning or browsing.</Text>
                </View>
              )}
            </View>
          </ScrollView>
        )}

        {/* Send Button */}
        {selectedIds.size > 0 && (
          <View style={{ position: 'absolute', bottom: 20, left: 20, right: 20 }}>
            <TouchableOpacity style={s.sendButton} onPress={() => executeTransfer(selectedTarget)} activeOpacity={0.8}>
              <Text style={s.sendButtonText}>Send {selectedIds.size} Items to {selectedTarget.DeviceName}</Text>
              <Text style={{ color: '#CCFBF1', fontSize: 10, marginTop: 3 }}>{buildBatchName()}</Text>
            </TouchableOpacity>
          </View>
        )}

        {/* Preview Modal */}
        <Modal visible={!!enlargedPreview} animationType="fade" transparent>
          <View style={[s.modalOverlay, { backgroundColor: 'rgba(0,0,0,0.95)' }]}>
            <TouchableOpacity style={{ position: 'absolute', top: 50, right: 20, zIndex: 10 }} onPress={() => setEnlargedPreview(null)}>
              <View style={{ padding: 10, backgroundColor: '#2A2F3A', borderRadius: 20 }}>
                <IconSymbol name="xmark" size={24} color="#FFF" />
              </View>
            </TouchableOpacity>
            {enlargedPreview && (enlargedPreview.mediaType === 'photo' || enlargedPreview.mediaType === 'video') ? (
              <Image source={{ uri: enlargedPreview.uri }} style={{ width: '100%', height: '80%', resizeMode: 'contain' }} />
            ) : (
              <View style={{ alignItems: 'center' }}>
                <IconSymbol name="doc.fill" size={80} color="#F59E0B" />
                <Text style={{ color: '#FFF', marginTop: 20, fontSize: 18, fontWeight: 'bold' }}>{enlargedPreview?.filename}</Text>
              </View>
            )}
          </View>
        </Modal>
      </SafeAreaView>
    );
  }

  // ─── MAIN SCREEN: Device List ───
  return (
    <SafeAreaView style={s.container}>
      <View style={s.header}>
        <Text style={s.title}>Connect</Text>
        <Text style={s.subtitle}>Transfer Hub</Text>
      </View>

      <ScrollView contentContainerStyle={{ paddingBottom: 40 }}>
        {/* Local Devices Section */}
        <View style={{ paddingHorizontal: 20, marginBottom: 20 }}>
          <View style={{ flexDirection: 'row', alignItems: 'center', gap: 8, marginBottom: 12 }}>
            <View style={{ width: 10, height: 10, borderRadius: 5, backgroundColor: '#10B981' }} />
            <Text style={s.sectionTitle}>Local Devices</Text>
            <Text style={{ color: '#4A5568', fontSize: 12 }}>({localDevices.length})</Text>
          </View>
          
          {localDevices.length === 0 ? (
            <View style={s.emptyCard}>
              <IconSymbol name="wifi.slash" size={24} color="#4A5568" />
              <Text style={{ color: '#6B7280', fontSize: 12, marginTop: 8 }}>No local devices found</Text>
              <Text style={{ color: '#4A5568', fontSize: 10, marginTop: 2 }}>Make sure devices are on the same network</Text>
            </View>
          ) : (
            localDevices.map((dev, i) => <DeviceCard key={`local_${i}`} device={dev} type="local" />)
          )}
        </View>

        {/* Global Devices Section */}
        <View style={{ paddingHorizontal: 20 }}>
          <View style={{ flexDirection: 'row', alignItems: 'center', gap: 8, marginBottom: 12 }}>
            <View style={{ width: 10, height: 10, borderRadius: 5, backgroundColor: '#3B82F6' }} />
            <Text style={s.sectionTitle}>Global Devices</Text>
            <Text style={{ color: '#4A5568', fontSize: 12 }}>({globalDevices.length})</Text>
          </View>
          
          {globalDevices.length === 0 ? (
            <View style={s.emptyCard}>
              <IconSymbol name="globe" size={24} color="#4A5568" />
              <Text style={{ color: '#6B7280', fontSize: 12, marginTop: 8 }}>No remote devices</Text>
              <Text style={{ color: '#4A5568', fontSize: 10, marginTop: 2 }}>All devices are on your local network</Text>
            </View>
          ) : (
            <>
              {/* PCs with Cloudflare first */}
              {globalDevices.filter(d => d.connectionType === 'cloudflare' || d.connectionType === 'cloudflare-unverified').map((dev, i) => (
                <DeviceCard key={`gcf_${i}`} device={dev} type="global" />
              ))}
              {/* Sync-only devices */}
              {globalDevices.filter(d => d.connectionType === 'sync-only').map((dev, i) => (
                <DeviceCard key={`gso_${i}`} device={dev} type="global" />
              ))}
            </>
          )}
        </View>

        {/* ─── Device Groups Section ─── */}
        <View style={{ paddingHorizontal: 20, marginTop: 16, marginBottom: 20 }}>
          <View style={{ flexDirection: 'row', alignItems: 'center', justifyContent: 'space-between', marginBottom: 12 }}>
            <View style={{ flexDirection: 'row', alignItems: 'center', gap: 8 }}>
              <View style={{ width: 10, height: 10, borderRadius: 5, backgroundColor: '#F59E0B' }} />
              <Text style={s.sectionTitle}>Groups</Text>
              <Text style={{ color: '#4A5568', fontSize: 12 }}>({deviceGroups.length})</Text>
            </View>
            <TouchableOpacity onPress={() => {
              setEditingGroup(null);
              setNewGroupName('');
              setSelectedGroupDevices(new Set());
              setShowGroupModal(true);
            }} style={{ backgroundColor: '#F59E0B', paddingHorizontal: 12, paddingVertical: 5, borderRadius: 8 }}>
              <Text style={{ color: '#000', fontSize: 11, fontWeight: '800' }}>+ NEW</Text>
            </TouchableOpacity>
          </View>

          {deviceGroups.length === 0 ? (
            <View style={s.emptyCard}>
              <IconSymbol name="person.3" size={24} color="#4A5568" />
              <Text style={{ color: '#6B7280', fontSize: 12, marginTop: 8 }}>No groups created</Text>
              <Text style={{ color: '#4A5568', fontSize: 10, marginTop: 2 }}>Groups sync regardless of global sync or network</Text>
            </View>
          ) : (
            deviceGroups.map((group) => (
              <View key={group.id} style={{ backgroundColor: '#171B26', borderRadius: 14, padding: 14, marginBottom: 8, borderWidth: 1, borderColor: '#F59E0B22' }}>
                <View style={{ flexDirection: 'row', alignItems: 'center', justifyContent: 'space-between' }}>
                  <View style={{ flex: 1 }}>
                    <Text style={{ color: '#FFF', fontSize: 14, fontWeight: '700' }}>{group.name}</Text>
                    <Text style={{ color: '#8A8F98', fontSize: 11, marginTop: 2 }}>{group.deviceNames.join(', ')}</Text>
                  </View>
                  <View style={{ flexDirection: 'row', gap: 6 }}>
                    <TouchableOpacity onPress={() => {
                      setEditingGroup(group);
                      setNewGroupName(group.name);
                      setSelectedGroupDevices(new Set(group.deviceNames));
                      setShowGroupModal(true);
                    }} style={{ backgroundColor: '#F59E0B22', padding: 8, borderRadius: 8 }}>
                      <IconSymbol name="pencil" size={14} color="#F59E0B" />
                    </TouchableOpacity>
                    <TouchableOpacity onPress={() => deleteGroup(group.id)} style={{ backgroundColor: '#EF444422', padding: 8, borderRadius: 8 }}>
                      <IconSymbol name="trash" size={14} color="#EF4444" />
                    </TouchableOpacity>
                  </View>
                </View>
              </View>
            ))
          )}
        </View>

        {/* Info Card */}
        {localDevices.length === 0 && globalDevices.length === 0 && (
          <View style={{ marginHorizontal: 20, marginTop: 30, padding: 20, backgroundColor: '#141824', borderRadius: 16, borderWidth: 1, borderColor: '#1E293B' }}>
            <Text style={{ color: '#FFF', fontSize: 14, fontWeight: '700', marginBottom: 8 }}>No devices detected</Text>
            <Text style={{ color: '#8A8F98', fontSize: 12, lineHeight: 18 }}>
              • Make sure AdvanceClip.exe is running on your PC{'\n'}
              • Both devices must be logged into the same Firebase project{'\n'}
              • For local transfers, connect to the same WiFi{'\n'}
              • For global transfers, enable Cloudflare tunnel on PC
            </Text>
          </View>
        )}
      </ScrollView>

      {/* ─── Online Websites Section ─── */}
      {(() => {
        const websiteDevices = allFirebaseDevices.filter(d => d.GlobalUrl && d.GlobalUrl.includes('trycloudflare.com'));
        if (websiteDevices.length === 0) return null;
        return (
          <View style={{ paddingHorizontal: 20, paddingBottom: 20 }}>
            <View style={{ flexDirection: 'row', alignItems: 'center', gap: 8, marginBottom: 12 }}>
              <View style={{ width: 10, height: 10, borderRadius: 5, backgroundColor: '#8B5CF6' }} />
              <Text style={s.sectionTitle}>Online Websites</Text>
              <Text style={{ color: '#4A5568', fontSize: 12 }}>({websiteDevices.length})</Text>
            </View>
            {websiteDevices.map((dev, i) => {
              const cfUrl = dev.GlobalUrl.endsWith('/') ? dev.GlobalUrl.slice(0, -1) : dev.GlobalUrl;
              return (
                <View key={`web_${i}`} style={[s.deviceCard, { borderColor: '#8B5CF644' }]}>
                  <View style={[s.deviceIcon, { backgroundColor: '#8B5CF618' }]}>
                    <IconSymbol name="globe" size={22} color="#8B5CF6" />
                  </View>
                  <View style={{ flex: 1, marginLeft: 12 }}>
                    <Text style={s.deviceName}>{dev.DeviceName || 'Unknown'}</Text>
                    <Text style={{ color: '#8B5CF6', fontSize: 10, marginTop: 2 }} numberOfLines={1}>{cfUrl}</Text>
                  </View>
                  <View style={{ flexDirection: 'row', gap: 6 }}>
                    <TouchableOpacity onPress={() => Linking.openURL(cfUrl)} style={{ backgroundColor: '#8B5CF622', padding: 8, borderRadius: 8 }}>
                      <IconSymbol name="arrow.up.right" size={14} color="#8B5CF6" />
                    </TouchableOpacity>
                    <TouchableOpacity onPress={async () => {
                      const { default: Clip } = await import('expo-clipboard');
                      await Clip.setStringAsync(cfUrl);
                      if (Platform.OS === 'android') ToastAndroid.show('URL copied!', ToastAndroid.SHORT);
                    }} style={{ backgroundColor: '#8B5CF622', padding: 8, borderRadius: 8 }}>
                      <IconSymbol name="doc.on.doc" size={14} color="#8B5CF6" />
                    </TouchableOpacity>
                  </View>
                </View>
              );
            })}
          </View>
        );
      })()}

      {/* ─── URL Popup Modal ─── */}
      <Modal visible={!!urlPopup} transparent={true} animationType="fade" onRequestClose={() => setUrlPopup(null)}>
        <TouchableOpacity style={{ flex: 1, backgroundColor: 'rgba(0,0,0,0.6)', justifyContent: 'center', alignItems: 'center' }} activeOpacity={1} onPress={() => setUrlPopup(null)}>
          <View style={{ backgroundColor: '#1C1F26', borderRadius: 20, padding: 24, width: '85%', borderWidth: 1, borderColor: '#2A2F3A' }}>
            <Text style={{ color: '#FFF', fontSize: 18, fontWeight: '800', marginBottom: 4 }}>{urlPopup?.device?.DeviceName}</Text>
            <Text style={{ color: '#8A8F98', fontSize: 11, marginBottom: 20 }}>Device URLs</Text>
            
            {urlPopup?.localUrl ? (
              <View style={{ marginBottom: 16 }}>
                <Text style={{ color: '#10B981', fontSize: 11, fontWeight: '700', marginBottom: 6 }}>⚡ LOCAL URL</Text>
                <Text style={{ color: '#CCC', fontSize: 12, marginBottom: 8 }} selectable>{urlPopup.localUrl}</Text>
                <View style={{ flexDirection: 'row', gap: 8 }}>
                  <TouchableOpacity onPress={() => { Linking.openURL(urlPopup!.localUrl); setUrlPopup(null); }} style={{ flex: 1, backgroundColor: '#10B981', paddingVertical: 10, borderRadius: 10, alignItems: 'center' }}>
                    <Text style={{ color: '#FFF', fontSize: 12, fontWeight: '700' }}>Open</Text>
                  </TouchableOpacity>
                  <TouchableOpacity onPress={async () => { 
                    const { default: Clip } = await import('expo-clipboard');
                    await Clip.setStringAsync(urlPopup!.localUrl); 
                    if (Platform.OS === 'android') ToastAndroid.show('Copied!', ToastAndroid.SHORT); 
                    setUrlPopup(null);
                  }} style={{ flex: 1, backgroundColor: '#10B98133', paddingVertical: 10, borderRadius: 10, alignItems: 'center' }}>
                    <Text style={{ color: '#10B981', fontSize: 12, fontWeight: '700' }}>Copy</Text>
                  </TouchableOpacity>
                </View>
              </View>
            ) : null}
            
            {urlPopup?.globalUrl ? (
              <View>
                <Text style={{ color: '#3B82F6', fontSize: 11, fontWeight: '700', marginBottom: 6 }}>☁️ GLOBAL URL</Text>
                <Text style={{ color: '#CCC', fontSize: 12, marginBottom: 8 }} selectable>{urlPopup.globalUrl}</Text>
                <View style={{ flexDirection: 'row', gap: 8 }}>
                  <TouchableOpacity onPress={() => { Linking.openURL(urlPopup!.globalUrl); setUrlPopup(null); }} style={{ flex: 1, backgroundColor: '#3B82F6', paddingVertical: 10, borderRadius: 10, alignItems: 'center' }}>
                    <Text style={{ color: '#FFF', fontSize: 12, fontWeight: '700' }}>Open</Text>
                  </TouchableOpacity>
                  <TouchableOpacity onPress={async () => { 
                    const { default: Clip } = await import('expo-clipboard');
                    await Clip.setStringAsync(urlPopup!.globalUrl); 
                    if (Platform.OS === 'android') ToastAndroid.show('Copied!', ToastAndroid.SHORT); 
                    setUrlPopup(null);
                  }} style={{ flex: 1, backgroundColor: '#3B82F633', paddingVertical: 10, borderRadius: 10, alignItems: 'center' }}>
                    <Text style={{ color: '#3B82F6', fontSize: 12, fontWeight: '700' }}>Copy</Text>
                  </TouchableOpacity>
                </View>
              </View>
            ) : null}
          </View>
        </TouchableOpacity>
      </Modal>

      {/* ─── Group Create/Edit Modal ─── */}
      <Modal visible={showGroupModal} transparent animationType="slide" onRequestClose={() => setShowGroupModal(false)}>
        <View style={{ flex: 1, backgroundColor: 'rgba(0,0,0,0.7)', justifyContent: 'center', padding: 20 }}>
          <View style={{ backgroundColor: '#1A1F2E', borderRadius: 20, padding: 20, maxHeight: '80%' }}>
            <Text style={{ color: '#FFF', fontSize: 18, fontWeight: '800', marginBottom: 16 }}>
              {editingGroup ? 'Edit Group' : 'Create Group'}
            </Text>
            
            <Text style={{ color: '#8A8F98', fontSize: 12, marginBottom: 6 }}>Group Name</Text>
            <TextInput
              value={newGroupName}
              onChangeText={setNewGroupName}
              placeholder="e.g. Home Office, Work Setup"
              placeholderTextColor="#4A5568"
              style={{ backgroundColor: '#0F1118', borderRadius: 10, padding: 12, color: '#FFF', fontSize: 14, marginBottom: 16, borderWidth: 1, borderColor: '#2A2F3A' }}
            />

            <Text style={{ color: '#8A8F98', fontSize: 12, marginBottom: 8 }}>Select Devices</Text>
            <ScrollView style={{ maxHeight: 250 }}>
              {[...localDevices, ...globalDevices, ...allFirebaseDevices]
                .filter((dev, idx, arr) => arr.findIndex(d => d.DeviceName === dev.DeviceName) === idx)
                .map((dev, i) => {
                  const isSelected = selectedGroupDevices.has(dev.DeviceName);
                  return (
                    <TouchableOpacity
                      key={`gdev_${i}`}
                      onPress={() => {
                        const next = new Set(selectedGroupDevices);
                        if (isSelected) next.delete(dev.DeviceName); else next.add(dev.DeviceName);
                        setSelectedGroupDevices(next);
                      }}
                      style={{
                        flexDirection: 'row', alignItems: 'center', padding: 12,
                        backgroundColor: isSelected ? '#F59E0B15' : '#0F1118',
                        borderRadius: 10, marginBottom: 6, borderWidth: 1,
                        borderColor: isSelected ? '#F59E0B44' : '#1E293B'
                      }}
                    >
                      <View style={{
                        width: 22, height: 22, borderRadius: 6, marginRight: 10,
                        backgroundColor: isSelected ? '#F59E0B' : '#2A2F3A',
                        alignItems: 'center', justifyContent: 'center'
                      }}>
                        {isSelected && <Text style={{ color: '#000', fontSize: 13, fontWeight: '900' }}>✓</Text>}
                      </View>
                      <IconSymbol name={dev.DeviceType === 'PC' ? 'laptopcomputer' : 'iphone'} size={18} color="#8A8F98" />
                      <Text style={{ color: '#FFF', fontSize: 13, fontWeight: '600', marginLeft: 8 }}>{dev.DeviceName || dev.DeviceType || 'Unknown'}</Text>
                      <Text style={{ color: '#4A5568', fontSize: 10, marginLeft: 'auto' }}>
                        {dev.connectionType === 'local' ? '🟢 Local' : dev.connectionType === 'cloudflare' ? '🔵 Cloud' : '⚪ Sync'}
                      </Text>
                    </TouchableOpacity>
                  );
                })}
            </ScrollView>

            <View style={{ flexDirection: 'row', gap: 10, marginTop: 16 }}>
              <TouchableOpacity onPress={() => setShowGroupModal(false)} style={{ flex: 1, padding: 12, borderRadius: 10, backgroundColor: '#2A2F3A', alignItems: 'center' }}>
                <Text style={{ color: '#8A8F98', fontWeight: '700' }}>Cancel</Text>
              </TouchableOpacity>
              <TouchableOpacity onPress={createOrUpdateGroup} style={{ flex: 1, padding: 12, borderRadius: 10, backgroundColor: '#F59E0B', alignItems: 'center' }}>
                <Text style={{ color: '#000', fontWeight: '800' }}>{editingGroup ? 'Save' : 'Create'}</Text>
              </TouchableOpacity>
            </View>
          </View>
        </View>
      </Modal>
    </SafeAreaView>
  );
}

const s = StyleSheet.create({
  container: { flex: 1, backgroundColor: '#0F1115' },
  header: { paddingTop: 60, paddingHorizontal: 24, marginBottom: 20 },
  title: { fontSize: 34, fontWeight: '800', color: '#FFFFFF', letterSpacing: -0.5 },
  subtitle: { fontSize: 14, color: '#8A8F98', marginTop: 4, fontWeight: '500', textTransform: 'uppercase', letterSpacing: 1.5 },
  sectionTitle: { color: '#FFF', fontSize: 16, fontWeight: '700' },
  card: { backgroundColor: '#1C1F26', marginHorizontal: 20, borderRadius: 20, padding: 24, borderWidth: 1, borderColor: '#2A2F3A', marginTop: 20 },
  
  // Device cards
  deviceCard: { flexDirection: 'row', alignItems: 'center', backgroundColor: '#141824', borderRadius: 14, padding: 14, marginBottom: 10, borderWidth: 1 },
  deviceIcon: { width: 44, height: 44, borderRadius: 22, alignItems: 'center', justifyContent: 'center' },
  deviceName: { color: '#FFF', fontSize: 15, fontWeight: '700' },
  badge: { paddingHorizontal: 8, paddingVertical: 2, borderRadius: 6 },
  badgeText: { fontSize: 10, fontWeight: '700' },
  emptyCard: { padding: 24, backgroundColor: '#141824', borderRadius: 14, borderWidth: 1, borderColor: '#1E293B', alignItems: 'center' },
  
  // Date
  dateBtn: { flex: 1, backgroundColor: '#1C1F26', borderRadius: 12, padding: 10, borderWidth: 1, borderColor: '#2A2F3A' },
  dateLabel: { color: '#8A8F98', fontSize: 9, fontWeight: '700', marginBottom: 2 },
  dateValue: { color: '#FFF', fontSize: 12, fontWeight: '600' },
  
  // Source chips
  sourceChip: { backgroundColor: '#1C1F26', borderRadius: 20, paddingHorizontal: 14, paddingVertical: 8, borderWidth: 1, borderColor: '#2A2F3A' },
  sourceChipActive: { backgroundColor: '#10B98122', borderColor: '#10B981' },
  sourceChipText: { color: '#8A8F98', fontSize: 12, fontWeight: '600' },
  sourceChipTextActive: { color: '#10B981' },
  
  // Type tabs
  tabRow: { flexDirection: 'row', paddingHorizontal: 20, marginBottom: 6, gap: 4 },
  tab: { paddingVertical: 8, paddingHorizontal: 14, borderRadius: 10 },
  tabActive: { backgroundColor: '#2A2F3A' },
  tabText: { color: '#6B7280', fontSize: 12, fontWeight: '700' },
  tabTextActive: { color: '#FFF' },
  
  // Selection
  checkCircle: { position: 'absolute', top: 4, right: 4, width: 22, height: 22, borderRadius: 11, backgroundColor: 'rgba(0,0,0,0.5)', borderWidth: 2, borderColor: '#FFF', alignItems: 'center', justifyContent: 'center' },
  checkCircleActive: { backgroundColor: '#10B981' },
  
  // Send
  sendButton: { backgroundColor: '#10B981', paddingVertical: 18, borderRadius: 18, alignItems: 'center', shadowColor: '#10B981', shadowOffset: { width: 0, height: 6 }, shadowOpacity: 0.3, shadowRadius: 10, elevation: 8 },
  sendButtonText: { color: '#FFF', fontSize: 16, fontWeight: '800' },
  
  // Upload
  controlBtn: { paddingVertical: 14, borderRadius: 14, alignItems: 'center' },
  controlBtnText: { color: '#FFF', fontSize: 14, fontWeight: '700' },
  
  // Modal
  modalOverlay: { flex: 1, justifyContent: 'center', alignItems: 'center' },
});
