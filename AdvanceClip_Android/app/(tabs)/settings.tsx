import React, { useState, useCallback, useEffect } from 'react';
import { StyleSheet, View, Text, TextInput, TouchableOpacity, SafeAreaView, KeyboardAvoidingView, Platform, Alert, Switch, NativeModules, ScrollView, ActivityIndicator } from 'react-native';
import { LinearGradient } from 'expo-linear-gradient';
import { useSettings } from '../../context/SettingsContext';
import { IconSymbol } from '@/components/ui/icon-symbol';
import * as FileSystem from 'expo-file-system/legacy';
import * as IntentLauncher from 'expo-intent-launcher';
import Constants from 'expo-constants';
import { colors, font, radius, shadows, space } from '../../styles/theme';
import AnimatedPressable from '../../components/AnimatedPressable';

const APP_VERSION = Constants.expoConfig?.version || '1.0.0';
const VERSION_URL = 'https://raw.githubusercontent.com/shdra06/AdvanceClip/main/version.json';

// Custom pure-JS slider row
const StepSlider = ({ value, min, max, step, onValueChange, trackColor, thumbColor, label }: { value: number; min: number; max: number; step: number; onValueChange: (v: number) => void; trackColor: string; thumbColor: string; label: string }) => {
  const pct = Math.max(0, Math.min(100, ((value - min) / (max - min)) * 100));
  return (
    <View style={{marginTop: 8}}>
      <View style={{flexDirection: 'row', alignItems: 'center', gap: 10}}>
        <TouchableOpacity onPress={() => { if (value - step >= min) onValueChange(value - step); }} style={{width: 36, height: 36, borderRadius: 10, backgroundColor: '#2A2F3A', alignItems: 'center', justifyContent: 'center'}}>
          <Text style={{color: '#FFF', fontSize: 18, fontWeight: '800'}}>−</Text>
        </TouchableOpacity>
        <View style={{flex: 1, height: 8, backgroundColor: '#2A2F3A', borderRadius: 4, overflow: 'hidden'}}>
          <View style={{width: `${pct}%`, height: '100%', backgroundColor: trackColor, borderRadius: 4}} />
        </View>
        <TouchableOpacity onPress={() => { if (value + step <= max) onValueChange(value + step); }} style={{width: 36, height: 36, borderRadius: 10, backgroundColor: '#2A2F3A', alignItems: 'center', justifyContent: 'center'}}>
          <Text style={{color: '#FFF', fontSize: 18, fontWeight: '800'}}>+</Text>
        </TouchableOpacity>
      </View>
    </View>
  );
};

export default function SettingsScreen() {
  const { pcLocalIp, setPcLocalIp, isGlobalSyncEnabled, setGlobalSyncEnabled, deviceName, setDeviceName, isFloatingBallEnabled, setFloatingBallEnabled, floatingBallSize, setFloatingBallSize, floatingBallAutoHide, setFloatingBallAutoHide, pairedDevices, removePairedDevice, pairingKey, regeneratePairingKey } = useSettings();
  const [localIpInput, setLocalIpInput] = useState(pcLocalIp);
  const [globalSyncInput, setGlobalSyncInput] = useState(isGlobalSyncEnabled);
  const [deviceNameInput, setDeviceNameInput] = useState(deviceName);
  const [floatingBallInput, setFloatingBallInput] = useState(isFloatingBallEnabled);

  // ═══ Update System State ═══
  const [updateStatus, setUpdateStatus] = useState<'idle' | 'checking' | 'available' | 'downloading' | 'ready' | 'error'>('idle');
  const [updateProgress, setUpdateProgress] = useState(0);
  const [latestVersion, setLatestVersion] = useState('');
  const [changelog, setChangelog] = useState('');
  const [downloadUrl, setDownloadUrl] = useState('');
  const [downloadedApkUri, setDownloadedApkUri] = useState('');

  const { AdvanceOverlay } = NativeModules;



  const handleSave = async () => {
    try {
      await setPcLocalIp(localIpInput);
      await setGlobalSyncEnabled(globalSyncInput);
      await setDeviceName(deviceNameInput);

      if (Platform.OS === 'android' && AdvanceOverlay) {
        if (floatingBallInput) {
          const hasPerm = await AdvanceOverlay.checkOverlayPermission();
          if (!hasPerm) {
             await AdvanceOverlay.requestOverlayPermission();
             Alert.alert('Permission Required', 'Please enable Draw Over Other Apps in settings, switch back, and press save again.');
             return;
          } else {
             AdvanceOverlay.startOverlay();
             AdvanceOverlay.setOverlayConfig(floatingBallSize, floatingBallAutoHide);
          }
        } else {
          AdvanceOverlay.stopOverlay();
        }
      }

      await setFloatingBallEnabled(floatingBallInput);
      Alert.alert('Saved', 'Configuration preserved.');
    } catch (e: any) {
      Alert.alert('Error', e?.message || 'Failed to save settings.');
    }
  };

  // ═══ Update Functions ═══
  const checkForUpdate = useCallback(async () => {
    try {
      setUpdateStatus('checking');
      const res = await fetch(`${VERSION_URL}?t=${Date.now()}`);
      const data = await res.json();
      const latest = data.android_version || '1.0.0';
      const dl = data.android_download || '';
      const log = data.changelog || 'Bug fixes and improvements';

      setLatestVersion(latest);
      setChangelog(log);
      setDownloadUrl(dl);

      // Simple semver compare
      const currentParts = APP_VERSION.split('.').map(Number);
      const latestParts = latest.split('.').map(Number);
      let isNewer = false;
      for (let i = 0; i < 3; i++) {
        if ((latestParts[i] || 0) > (currentParts[i] || 0)) { isNewer = true; break; }
        if ((latestParts[i] || 0) < (currentParts[i] || 0)) break;
      }

      if (isNewer && dl) {
        setUpdateStatus('available');
        // Auto-trigger download + install immediately
        autoDownloadAndInstall(dl, latest);
      } else {
        setUpdateStatus('idle');
        Alert.alert('✅ Up to Date', `You're on the latest version (v${APP_VERSION}).`);
      }
    } catch (e) {
      setUpdateStatus('error');
      Alert.alert('Error', 'Could not check for updates. Check your internet connection.');
    }
  }, []);

  const autoDownloadAndInstall = async (url: string, version: string) => {
    if (!url) return;
    try {
      setUpdateStatus('downloading');
      setUpdateProgress(0);

      // Clean ALL old FlyShelf APKs from cache to prevent stale installs
      try {
        const cacheDir = (FileSystem as any).cacheDirectory;
        const cacheFiles = await FileSystem.readDirectoryAsync(cacheDir);
        for (const file of cacheFiles) {
          if (file.startsWith('FlyShelf_') && file.endsWith('.apk')) {
            await FileSystem.deleteAsync(`${cacheDir}${file}`, { idempotent: true });
          }
        }
      } catch {}

      // Use timestamp in filename to guarantee a fresh file (prevents resume of stale download)
      const apkUri = `${(FileSystem as any).cacheDirectory}FlyShelf_v${version}_${Date.now()}.apk`;

      const downloadResumable = FileSystem.createDownloadResumable(
        url,
        apkUri,
        { headers: { 'Cache-Control': 'no-cache', 'Pragma': 'no-cache' } },
        (progress) => {
          const pct = progress.totalBytesExpectedToWrite > 0
            ? Math.round((progress.totalBytesWritten / progress.totalBytesExpectedToWrite) * 100)
            : 0;
          setUpdateProgress(pct);
        }
      );

      const result = await downloadResumable.downloadAsync();
      if (!result?.uri) throw new Error('Download returned no URI');

      // Verify the download is a real APK (> 1MB) — not an error page
      const fileInfo = await FileSystem.getInfoAsync(result.uri);
      if (!fileInfo.exists || (fileInfo as any).size < 1_000_000) {
        throw new Error(`Download too small (${(fileInfo as any).size || 0} bytes) — likely a redirect or error page`);
      }

      setDownloadedApkUri(result.uri);
      setUpdateStatus('ready');
      await installApk(result.uri);
    } catch (e: any) {
      setUpdateStatus('error');
      Alert.alert('Download Failed', e?.message || 'Could not download the update APK.');
    }
  };

  const downloadAndInstall = useCallback(async () => {
    if (!downloadUrl) return;
    try {
      setUpdateStatus('downloading');
      setUpdateProgress(0);

      // Clean old APKs
      try {
        const cacheDir = (FileSystem as any).cacheDirectory;
        const cacheFiles = await FileSystem.readDirectoryAsync(cacheDir);
        for (const file of cacheFiles) {
          if (file.startsWith('FlyShelf_') && file.endsWith('.apk')) {
            await FileSystem.deleteAsync(`${cacheDir}${file}`, { idempotent: true });
          }
        }
      } catch {}

      const apkUri = `${(FileSystem as any).cacheDirectory}FlyShelf_v${latestVersion}_${Date.now()}.apk`;

      const downloadResumable = FileSystem.createDownloadResumable(
        downloadUrl,
        apkUri,
        { headers: { 'Cache-Control': 'no-cache', 'Pragma': 'no-cache' } },
        (progress) => {
          const pct = progress.totalBytesExpectedToWrite > 0
            ? Math.round((progress.totalBytesWritten / progress.totalBytesExpectedToWrite) * 100)
            : 0;
          setUpdateProgress(pct);
        }
      );

      const result = await downloadResumable.downloadAsync();
      if (!result?.uri) throw new Error('Download returned no URI');

      // Verify APK size
      const fileInfo = await FileSystem.getInfoAsync(result.uri);
      if (!fileInfo.exists || (fileInfo as any).size < 1_000_000) {
        throw new Error(`Download too small (${(fileInfo as any).size || 0} bytes) — likely a redirect or error page`);
      }

      setDownloadedApkUri(result.uri);
      setUpdateStatus('ready');
      await installApk(result.uri);

    } catch (e: any) {
      setUpdateStatus('error');
      Alert.alert('Download Failed', e?.message || 'Could not download the update APK.');
    }
  }, [downloadUrl, latestVersion]);

  const installApk = useCallback(async (uri?: string) => {
    const apkPath = uri || downloadedApkUri;
    if (!apkPath) return;

    try {
      // Get content:// URI for the APK (required for Android 7+)
      const contentUri = await (FileSystem as any).getContentUriAsync(apkPath);

      await IntentLauncher.startActivityAsync('android.intent.action.VIEW', {
        data: contentUri,
        flags: 1, // FLAG_GRANT_READ_URI_PERMISSION
        type: 'application/vnd.android.package-archive',
      });
    } catch (e: any) {
      // Fallback: try opening via Linking
      try {
        const { Linking } = require('react-native');
        await Linking.openURL(downloadUrl);
      } catch {
        Alert.alert('Install Error', 'Could not open the APK. Please install it manually from your Downloads folder.');
      }
    }
  }, [downloadedApkUri, downloadUrl]);

  const getUpdateButtonContent = () => {
    switch (updateStatus) {
      case 'checking':
        return (
          <View style={{ flexDirection: 'row', alignItems: 'center', gap: 8 }}>
            <ActivityIndicator size="small" color="#FFF" />
            <Text style={{ color: '#FFF', fontWeight: '700', fontSize: 13 }}>Checking...</Text>
          </View>
        );
      case 'available':
        return <Text style={{ color: '#FFF', fontWeight: '700', fontSize: 13 }}>Download v{latestVersion}</Text>;
      case 'downloading':
        return <Text style={{ color: '#FFF', fontWeight: '700', fontSize: 13 }}>Downloading... {updateProgress}%</Text>;
      case 'ready':
        return <Text style={{ color: '#FFF', fontWeight: '700', fontSize: 13 }}>Install Now</Text>;
      case 'error':
        return <Text style={{ color: '#FFF', fontWeight: '700', fontSize: 13 }}>Retry Check</Text>;
      default:
        return <Text style={{ color: '#FFF', fontWeight: '700', fontSize: 13 }}>Check Updates</Text>;
    }
  };

  const handleUpdatePress = () => {
    switch (updateStatus) {
      case 'idle':
      case 'error':
        checkForUpdate();
        break;
      case 'available':
        downloadAndInstall();
        break;
      case 'ready':
        installApk();
        break;
    }
  };

  const getUpdateButtonColor = () => {
    switch (updateStatus) {
      case 'available': return '#F59E0B';
      case 'downloading': return '#6366F1';
      case 'ready': return '#10B981';
      case 'error': return '#EF4444';
      default: return '#10B981';
    }
  };

  return (
    <LinearGradient colors={[colors.bg.base, colors.bg.baseEnd]} style={{ flex: 1 }}>
    <SafeAreaView style={[styles.container, { backgroundColor: 'transparent' }]}>
      <KeyboardAvoidingView behavior={Platform.OS === 'ios' ? 'padding' : 'height'} style={{flex: 1}}>
        <ScrollView contentContainerStyle={styles.scrollContent} keyboardShouldPersistTaps="handled" showsVerticalScrollIndicator={false}>
          
          <View style={styles.header}>
            <Text style={styles.title}>Settings</Text>
            <Text style={styles.subtitle}>Configure Sync Variables</Text>
          </View>

          {/* Save Button at the top */}
          <TouchableOpacity style={styles.saveButton} onPress={handleSave}>
            <Text style={styles.saveButtonText}>Save Configuration</Text>
          </TouchableOpacity>

          {/* Networking Card */}
          <View style={styles.card}>
            <Text style={styles.sectionHeader}>Networking</Text>
            
            <View style={styles.inputContainer}>
              <View style={styles.inputHeaderRow}>
                <IconSymbol name="network" size={20} color="#4A62EB" />
                <Text style={styles.inputLabel}>FlyShelf PC API Address</Text>
              </View>
              <TextInput
                style={styles.input}
                value={localIpInput}
                onChangeText={setLocalIpInput}
                placeholder="e.g. 192.168.1.5:8999"
                placeholderTextColor="#4C5361"
                keyboardType="numbers-and-punctuation"
              />
              <Text style={styles.helperText}>Fallback IP for direct LAN transfers when your PC isn't auto-detected in Firebase. If your PC shows up in Active Devices, this can be left blank. Format: 192.168.x.x:8999</Text>
            </View>

            <View style={[styles.inputContainer, { marginTop: 20 }]}>
              <View style={styles.inputHeaderRow}>
                <IconSymbol name="iphone" size={20} color="#4A62EB" />
                <Text style={styles.inputLabel}>Device Profile Name</Text>
              </View>
              <TextInput
                style={styles.input}
                value={deviceNameInput}
                onChangeText={setDeviceNameInput}
                placeholder="e.g. John's Mobile Profile"
                placeholderTextColor="#4C5361"
              />
              <Text style={styles.helperText}>This name identifies you on the clipboard feed.</Text>
            </View>



            <View style={[styles.inputContainer, { marginTop: 20 }]}>
              <View style={{flexDirection: 'row', justifyContent: 'space-between', alignItems: 'center'}}>
                  <View style={styles.inputHeaderRow}>
                    <IconSymbol name="cloud" size={20} color="#4A62EB" />
                    <Text style={styles.inputLabel}>Global Cloud Transfer</Text>
                  </View>
                  <Switch 
                    value={globalSyncInput} 
                    onValueChange={setGlobalSyncInput} 
                    trackColor={{ false: "#2A2F3A", true: "#4A62EB" }} 
                    thumbColor="#FFF"
                  />
              </View>
              <Text style={styles.helperText}>If disabled, your clipboard and files will ONLY synchronize when connected locally. Used to save Firebase active quotas on Free Tiers.</Text>
            </View>
          </View>

          {/* Paired Devices Card */}
          <View style={[styles.card, { marginTop: 16 }]}>
            <Text style={styles.sectionHeader}>Paired Devices</Text>

            {/* Pairing Key Display */}
            <View style={styles.inputContainer}>
              <View style={styles.inputHeaderRow}>
                <IconSymbol name="key" size={20} color="#F59E0B" />
                <Text style={styles.inputLabel}>Pairing Key</Text>
              </View>
              {pairingKey ? (
                <View style={{ flexDirection: 'row', alignItems: 'center', gap: 10 }}>
                  <View style={{ flex: 1, backgroundColor: '#0F1115', borderRadius: 12, padding: 14, borderWidth: 1, borderColor: '#2A2F3A' }}>
                    <Text style={{ color: '#8A8F98', fontSize: 14, fontFamily: Platform.OS === 'ios' ? 'Menlo' : 'monospace', letterSpacing: 1 }}>
                      {pairingKey.substring(0, 6)}••••••{pairingKey.substring(pairingKey.length - 4)}
                    </Text>
                  </View>
                  <TouchableOpacity
                    onPress={() => {
                      Alert.alert(
                        'Regenerate Key?',
                        'This will disconnect ALL paired devices. They will need to re-pair using a new QR code or pairing code.',
                        [
                          { text: 'Cancel', style: 'cancel' },
                          {
                            text: 'Regenerate', style: 'destructive',
                            onPress: async () => {
                              await regeneratePairingKey();
                              Alert.alert('Done', 'New pairing key generated. Re-pair your devices.');
                            }
                          }
                        ]
                      );
                    }}
                    style={{ backgroundColor: '#F59E0B22', padding: 10, borderRadius: 10 }}
                  >
                    <IconSymbol name="arrow.clockwise" size={18} color="#F59E0B" />
                  </TouchableOpacity>
                </View>
              ) : (
                <View style={{ backgroundColor: '#0F1115', borderRadius: 12, padding: 16, borderWidth: 1, borderColor: '#EF444433', alignItems: 'center' }}>
                  <Text style={{ color: '#EF4444', fontSize: 14, fontWeight: '600', marginBottom: 4 }}>Not Paired</Text>
                  <Text style={{ color: '#8A8F98', fontSize: 12, textAlign: 'center' }}>Scan a QR code or enter a pairing code on the main screen to connect your devices.</Text>
                </View>
              )}
              <Text style={styles.helperText}>This key scopes your clipboard feed. Only devices sharing this key can see each other's items.</Text>
            </View>

            {/* Device List */}
            <View style={[styles.inputContainer, { marginTop: 20 }]}>
              <View style={styles.inputHeaderRow}>
                <IconSymbol name="laptopcomputer.and.iphone" size={20} color="#6366F1" />
                <Text style={styles.inputLabel}>Connected Devices ({pairedDevices.length}/5)</Text>
              </View>
              {pairedDevices.length > 0 ? (
                <View style={{ gap: 8 }}>
                  {pairedDevices.map((device) => (
                    <View
                      key={device.deviceId}
                      style={{
                        flexDirection: 'row', alignItems: 'center',
                        backgroundColor: '#0F1115', borderRadius: 14, padding: 14,
                        borderWidth: 1, borderColor: '#2A2F3A',
                      }}
                    >
                      <Text style={{ fontSize: 22, marginRight: 12 }}>
                        {device.deviceType === 'PC' ? '💻' : device.deviceType === 'Mobile' ? '📱' : '🌐'}
                      </Text>
                      <View style={{ flex: 1 }}>
                        <Text style={{ color: '#FFF', fontSize: 15, fontWeight: '600' }}>{device.deviceName}</Text>
                        <Text style={{ color: '#8A8F98', fontSize: 11, marginTop: 2 }}>
                          {device.deviceType} • Paired {new Date(device.pairedAt).toLocaleDateString()}
                        </Text>
                      </View>
                      <TouchableOpacity
                        onPress={() => {
                          Alert.alert(
                            'Remove Device?',
                            `Remove "${device.deviceName}" from your paired devices?`,
                            [
                              { text: 'Cancel', style: 'cancel' },
                              {
                                text: 'Remove', style: 'destructive',
                                onPress: () => removePairedDevice(device.deviceId),
                              }
                            ]
                          );
                        }}
                        style={{ backgroundColor: '#EF444422', padding: 8, borderRadius: 8 }}
                      >
                        <IconSymbol name="xmark" size={16} color="#EF4444" />
                      </TouchableOpacity>
                    </View>
                  ))}
                </View>
              ) : (
                <View style={{ backgroundColor: '#0F1115', borderRadius: 12, padding: 16, borderWidth: 1, borderColor: '#2A2F3A', alignItems: 'center' }}>
                  <Text style={{ color: '#4C5361', fontSize: 13, fontStyle: 'italic' }}>No devices paired yet</Text>
                </View>
              )}
              <Text style={styles.helperText}>Devices in this list share a secure clipboard channel. Up to 5 devices can be paired simultaneously.</Text>
            </View>
          </View>

          {/* Floating Clipboard Card */}
          <View style={[styles.card, { marginTop: 16 }]}>
            <Text style={styles.sectionHeader}>Floating Clipboard</Text>

            <View style={styles.inputContainer}>
              <View style={{flexDirection: 'row', justifyContent: 'space-between', alignItems: 'center'}}>
                  <View style={styles.inputHeaderRow}>
                    <IconSymbol name="eye" size={20} color="#8B5CF6" />
                    <Text style={styles.inputLabel}>Enable Floating Ball</Text>
                  </View>
                  <Switch 
                    value={floatingBallInput} 
                    onValueChange={setFloatingBallInput} 
                    trackColor={{ false: "#2A2F3A", true: "#8B5CF6" }} 
                    thumbColor="#FFF"
                  />
              </View>
              <Text style={styles.helperText}>Enable the persistent floating ball on your screen for instant overlay clipboard access anywhere.</Text>
            </View>

            <View style={[styles.inputContainer, { marginTop: 16 }]}>
              <View style={styles.inputHeaderRow}>
                <IconSymbol name="arrow.up.left.and.arrow.down.right" size={20} color="#10B981" />
                <Text style={styles.inputLabel}>Ball Size: {floatingBallSize}dp</Text>
              </View>
              <StepSlider
                value={floatingBallSize}
                min={32}
                max={72}
                step={4}
                onValueChange={(val) => setFloatingBallSize(val)}
                trackColor="#10B981"
                thumbColor="#10B981"
                label="size"
              />
              <Text style={styles.helperText}>Controls how large the floating ball appears on screen. Default: 48dp.</Text>
            </View>

            <View style={[styles.inputContainer, { marginTop: 16 }]}>
              <View style={styles.inputHeaderRow}>
                <IconSymbol name="timer" size={20} color="#F59E0B" />
                <Text style={styles.inputLabel}>Auto-Hide Delay: {(floatingBallAutoHide / 1000).toFixed(1)}s</Text>
              </View>
              <StepSlider
                value={floatingBallAutoHide}
                min={1000}
                max={10000}
                step={500}
                onValueChange={(val) => setFloatingBallAutoHide(val)}
                trackColor="#F59E0B"
                thumbColor="#F59E0B"
                label="delay"
              />
              <Text style={styles.helperText}>Time before the ball auto-hides to the edge. Default: 3 seconds.</Text>
            </View>

            <View style={[styles.inputContainer, { marginTop: 16 }]}>
              <Text style={[styles.helperText, { color: '#6366F1', fontWeight: '500' }]}>
                • Tap a clip item to copy it instantly{'\n'}
                • Long-press to drag & drop into any text field{'\n'}
                • Tap the floating ball to toggle the clipboard panel{'\n'}
                • The ball fades to the edge when not in use
              </Text>
            </View>
          </View>

          {/* App Info & Updates Card */}
          <View style={[styles.card, { marginTop: 16 }]}>
            <Text style={styles.sectionHeader}>App Info & Updates</Text>

            <View style={styles.inputContainer}>
              <View style={{ flexDirection: 'row', justifyContent: 'space-between', alignItems: 'center' }}>
                <View style={{ flex: 1 }}>
                  <Text style={styles.inputLabel}>FlyShelf Mobile</Text>
                  <Text style={[styles.helperText, { marginTop: 2 }]}>
                    Installed: <Text style={{ color: '#8B5CF6', fontWeight: '700' }}>v{APP_VERSION}</Text>
                    {latestVersion && updateStatus === 'available' ? (
                      <Text style={{ color: '#F59E0B' }}>  →  v{latestVersion}</Text>
                    ) : null}
                  </Text>
                </View>
                <TouchableOpacity
                  style={{
                    backgroundColor: getUpdateButtonColor(),
                    paddingHorizontal: 16,
                    paddingVertical: 10,
                    borderRadius: 12,
                    minWidth: 130,
                    alignItems: 'center',
                  }}
                  onPress={handleUpdatePress}
                  disabled={updateStatus === 'checking' || updateStatus === 'downloading'}
                >
                  {getUpdateButtonContent()}
                </TouchableOpacity>
              </View>

              {/* Download Progress Bar */}
              {updateStatus === 'downloading' && (
                <View style={{ marginTop: 14 }}>
                  <View style={{ height: 6, backgroundColor: '#2A2F3A', borderRadius: 3, overflow: 'hidden' }}>
                    <View style={{
                      width: `${updateProgress}%`,
                      height: '100%',
                      backgroundColor: '#6366F1',
                      borderRadius: 3,
                    }} />
                  </View>
                  <Text style={[styles.helperText, { textAlign: 'center', marginTop: 6, color: '#6366F1' }]}>
                    Downloading APK... {updateProgress}%
                  </Text>
                </View>
              )}

              {/* Update Available Info */}
              {updateStatus === 'available' && (
                <View style={{
                  marginTop: 12,
                  backgroundColor: '#1A1D24',
                  borderRadius: 12,
                  padding: 14,
                  borderWidth: 1,
                  borderColor: '#F59E0B33',
                }}>
                  <Text style={{ color: '#F59E0B', fontWeight: '700', fontSize: 14, marginBottom: 4 }}>
                    🎉 Update v{latestVersion} Available
                  </Text>
                  <Text style={{ color: '#8A8F98', fontSize: 12, lineHeight: 18 }}>
                    {changelog}
                  </Text>
                </View>
              )}

              {/* Ready to Install */}
              {updateStatus === 'ready' && (
                <View style={{
                  marginTop: 12,
                  backgroundColor: '#1A1D24',
                  borderRadius: 12,
                  padding: 14,
                  borderWidth: 1,
                  borderColor: '#10B98133',
                }}>
                  <Text style={{ color: '#10B981', fontWeight: '700', fontSize: 14 }}>
                    ✅ APK Downloaded — Tap "Install Now" to update
                  </Text>
                </View>
              )}

              <Text style={[styles.helperText, { marginTop: 12 }]}>
                Downloads the latest APK from GitHub and opens the Android installer. Make sure "Install from Unknown Sources" is enabled for this app.
              </Text>
            </View>
          </View>

          {/* Bottom padding so scroll doesn't cut off */}
          <View style={{ height: 60 }} />

        </ScrollView>
      </KeyboardAvoidingView>
    </SafeAreaView>
    </LinearGradient>
  );
}

const styles = StyleSheet.create({
  container: {
    flex: 1,
    backgroundColor: 'transparent',
  },
  scrollContent: {
    paddingBottom: 40,
  },
  header: {
    paddingTop: 60,
    paddingHorizontal: space['2xl'],
    marginBottom: space.xl,
  },
  title: {
    fontSize: 30,
    fontFamily: font.extrabold,
    color: colors.text.primary,
    letterSpacing: -0.8,
  },
  subtitle: {
    fontSize: 13,
    fontFamily: font.medium,
    color: colors.text.tertiary,
    marginTop: 4,
    textTransform: 'uppercase',
    letterSpacing: 1.5,
  },
  saveButton: {
    backgroundColor: colors.accent.primary,
    paddingVertical: 16,
    borderRadius: radius.lg,
    alignItems: 'center',
    marginHorizontal: space.xl,
    marginBottom: space.xl,
    ...shadows.glow(colors.accent.primary),
  },
  saveButtonText: {
    color: '#FFFFFF',
    fontSize: 15,
    fontFamily: font.bold,
    letterSpacing: 0.3,
  },
  card: {
    backgroundColor: colors.bg.card,
    marginHorizontal: space.xl,
    borderRadius: radius.xl,
    padding: space['2xl'],
    borderWidth: 1,
    borderColor: colors.border.subtle,
    borderTopColor: colors.innerHighlight,
    ...shadows.card,
  },
  sectionHeader: {
    color: colors.text.primary,
    fontSize: 17,
    fontFamily: font.semibold,
    marginBottom: space.xl,
    letterSpacing: -0.2,
  },
  inputContainer: {
    marginBottom: 10,
  },
  inputHeaderRow: {
    flexDirection: 'row',
    alignItems: 'center',
    marginBottom: space.md,
  },
  inputLabel: {
    color: colors.text.primary,
    fontSize: 14,
    fontFamily: font.semibold,
    marginLeft: space.sm,
  },
  input: {
    backgroundColor: colors.bg.input,
    color: colors.text.primary,
    fontSize: 16,
    fontFamily: font.medium,
    borderRadius: radius.md,
    paddingHorizontal: space.lg,
    paddingVertical: 14,
    borderWidth: 1,
    borderColor: colors.border.subtle,
  },
  helperText: {
    color: colors.text.tertiary,
    fontSize: 12,
    fontFamily: font.regular,
    marginTop: 10,
    lineHeight: 18,
  },
});

