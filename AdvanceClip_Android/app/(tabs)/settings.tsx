import React, { useState } from 'react';
import { StyleSheet, View, Text, TextInput, TouchableOpacity, SafeAreaView, KeyboardAvoidingView, Platform, Alert, Switch, NativeModules, ScrollView } from 'react-native';
import { useSettings } from '../../context/SettingsContext';
import { IconSymbol } from '@/components/ui/icon-symbol';

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
  const { pcLocalIp, setPcLocalIp, isGlobalSyncEnabled, setGlobalSyncEnabled, deviceName, setDeviceName, isFloatingBallEnabled, setFloatingBallEnabled, defaultTargetDeviceName, setDefaultTargetDeviceName, floatingBallSize, setFloatingBallSize, floatingBallAutoHide, setFloatingBallAutoHide } = useSettings();
  const [localIpInput, setLocalIpInput] = useState(pcLocalIp);
  const [globalSyncInput, setGlobalSyncInput] = useState(isGlobalSyncEnabled);
  const [deviceNameInput, setDeviceNameInput] = useState(deviceName);
  const [floatingBallInput, setFloatingBallInput] = useState(isFloatingBallEnabled);
  const [defaultTargetInput, setDefaultTargetInput] = useState(defaultTargetDeviceName);

  const { AdvanceOverlay } = NativeModules;

  const handleSave = async () => {
    try {
      await setPcLocalIp(localIpInput);
      await setGlobalSyncEnabled(globalSyncInput);
      await setDeviceName(deviceNameInput);
      await setDefaultTargetDeviceName(defaultTargetInput);

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

  return (
    <SafeAreaView style={styles.container}>
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
                <Text style={styles.inputLabel}>AdvanceClip PC API Address</Text>
              </View>
              <TextInput
                style={styles.input}
                value={localIpInput}
                onChangeText={setLocalIpInput}
                placeholder="e.g. 192.168.1.5:8999"
                placeholderTextColor="#4C5361"
                keyboardType="numbers-and-punctuation"
              />
              <Text style={styles.helperText}>This IP is used for direct, high-speed physical transfers over Wi-Fi, bypassing Firebase data limits.</Text>
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
              <View style={styles.inputHeaderRow}>
                <IconSymbol name="car" size={20} color="#F59E0B" />
                <Text style={styles.inputLabel}>Auto-Route Destination</Text>
              </View>
              <TextInput
                style={styles.input}
                value={defaultTargetInput}
                onChangeText={setDefaultTargetInput}
                placeholder="e.g. John's PC"
                placeholderTextColor="#4C5361"
              />
              <Text style={styles.helperText}>Type a specific device name to automatically skip target selection and stealth-route payloads during Extractions.</Text>
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
                <View>
                  <Text style={styles.inputLabel}>AdvanceClip Mobile</Text>
                  <Text style={[styles.helperText, { marginTop: 2 }]}>Current version: <Text style={{ color: '#8B5CF6', fontWeight: '700' }}>v1.0.0</Text></Text>
                </View>
                <TouchableOpacity
                  style={{ backgroundColor: '#10B981', paddingHorizontal: 16, paddingVertical: 8, borderRadius: 12 }}
                  onPress={async () => {
                    try {
                      const res = await fetch(`https://raw.githubusercontent.com/shdra06/AdvanceClip/main/version.json?t=${Date.now()}`);
                      const data = await res.json();
                      const latest = data.android_version || '1.0.0';
                      const current = '1.0.0';
                      if (latest > current) {
                        Alert.alert(
                          `Update v${latest} Available`,
                          `What's new:\n${data.changelog || 'Bug fixes and improvements'}\n\nDownload now?`,
                          [
                            { text: 'Cancel', style: 'cancel' },
                            {
                              text: 'Download & Install',
                              onPress: async () => {
                                try {
                                  const { Linking } = require('react-native');
                                  await Linking.openURL(data.android_download || 'https://github.com/shdra06/AdvanceClip/releases/latest');
                                } catch (e) { Alert.alert('Error', 'Could not open download link.'); }
                              }
                            }
                          ]
                        );
                      } else {
                        Alert.alert('Up to Date', `You have the latest version (v${current}).`);
                      }
                    } catch (e) {
                      Alert.alert('Error', 'Could not check for updates. Make sure you have internet.');
                    }
                  }}
                >
                  <Text style={{ color: '#FFF', fontWeight: '700', fontSize: 13 }}>Check Updates</Text>
                </TouchableOpacity>
              </View>
              <Text style={[styles.helperText, { marginTop: 10 }]}>
                Tap to check for a newer APK on GitHub. If an update is available, tap "Download &amp; Install" to get it.
              </Text>
            </View>
          </View>

          {/* Bottom padding so scroll doesn't cut off */}
          <View style={{ height: 60 }} />

        </ScrollView>
      </KeyboardAvoidingView>
    </SafeAreaView>
  );
}

const styles = StyleSheet.create({
  container: {
    flex: 1,
    backgroundColor: '#0F1115',
  },
  scrollContent: {
    paddingBottom: 40,
  },
  header: {
    paddingTop: 60,
    paddingHorizontal: 24,
    marginBottom: 20,
  },
  title: {
    fontSize: 34,
    fontWeight: '800',
    color: '#FFFFFF',
    letterSpacing: -0.5,
  },
  subtitle: {
    fontSize: 16,
    color: '#8A8F98',
    marginTop: 4,
    fontWeight: '500',
    textTransform: 'uppercase',
    letterSpacing: 1.5,
  },
  saveButton: {
    backgroundColor: '#4A62EB',
    paddingVertical: 16,
    borderRadius: 16,
    alignItems: 'center',
    marginHorizontal: 20,
    marginBottom: 20,
    shadowColor: '#4A62EB',
    shadowOffset: { width: 0, height: 8 },
    shadowOpacity: 0.4,
    shadowRadius: 15,
    elevation: 6,
  },
  saveButtonText: {
    color: '#FFFFFF',
    fontSize: 16,
    fontWeight: '700',
    letterSpacing: 0.5,
  },
  card: {
    backgroundColor: '#1C1F26',
    marginHorizontal: 20,
    borderRadius: 24,
    padding: 24,
    borderWidth: 1,
    borderColor: '#2A2F3A',
  },
  sectionHeader: {
    color: '#FFFFFF',
    fontSize: 18,
    fontWeight: '600',
    marginBottom: 20,
  },
  inputContainer: {
    marginBottom: 10,
  },
  inputHeaderRow: {
    flexDirection: 'row',
    alignItems: 'center',
    marginBottom: 12,
  },
  inputLabel: {
    color: '#FFFFFF',
    fontSize: 15,
    fontWeight: '600',
    marginLeft: 8,
  },
  input: {
    backgroundColor: '#0F1115',
    color: '#FFFFFF',
    fontSize: 16,
    fontWeight: '500',
    borderRadius: 12,
    paddingHorizontal: 16,
    paddingVertical: 14,
    borderWidth: 1,
    borderColor: '#2A2F3A',
  },
  helperText: {
    color: '#8A8F98',
    fontSize: 12,
    marginTop: 10,
    lineHeight: 18,
  },
});
