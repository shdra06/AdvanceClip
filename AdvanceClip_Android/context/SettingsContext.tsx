import React, { createContext, useState, useEffect, useContext } from 'react';
import AsyncStorage from '@react-native-async-storage/async-storage';

export type PairedDevice = {
  deviceId: string;
  deviceName: string;
  deviceType: 'PC' | 'Mobile' | 'Browser';
  pairedAt: number; // timestamp
};

type SettingsContextType = {
  pcLocalIp: string;
  setPcLocalIp: (ip: string) => Promise<void>;
  deviceName: string;
  setDeviceName: (name: string) => Promise<void>;
  deviceId: string;
  isGlobalSyncEnabled: boolean;
  setGlobalSyncEnabled: (val: boolean) => Promise<void>;
  isFloatingBallEnabled: boolean;
  setFloatingBallEnabled: (val: boolean) => Promise<void>;
  defaultTargetDeviceName: string;
  setDefaultTargetDeviceName: (name: string) => Promise<void>;
  floatingBallSize: number;
  setFloatingBallSize: (val: number) => Promise<void>;
  floatingBallAutoHide: number;
  setFloatingBallAutoHide: (val: number) => Promise<void>;
  // ── Paired Devices ──
  pairedDevices: PairedDevice[];
  addPairedDevice: (device: PairedDevice) => Promise<void>;
  removePairedDevice: (deviceId: string) => Promise<void>;
  pairingKey: string;
  regeneratePairingKey: () => Promise<string>;
};

const SettingsContext = createContext<SettingsContextType>({
  pcLocalIp: '192.168.1.5:3000',
  setPcLocalIp: async () => {},
  deviceName: '',
  setDeviceName: async () => {},
  deviceId: '',
  isGlobalSyncEnabled: true,
  setGlobalSyncEnabled: async () => {},
  isFloatingBallEnabled: false,
  setFloatingBallEnabled: async () => {},
  defaultTargetDeviceName: '',
  setDefaultTargetDeviceName: async () => {},
  floatingBallSize: 48,
  setFloatingBallSize: async () => {},
  floatingBallAutoHide: 3000,
  setFloatingBallAutoHide: async () => {},
  pairedDevices: [],
  addPairedDevice: async () => {},
  removePairedDevice: async () => {},
  pairingKey: '',
  regeneratePairingKey: async () => '',
});

export const useSettings = () => useContext(SettingsContext);

/** Generate a 32-char hex key (same format as PC's Guid.ToString("N")) */
const generatePairingKey = (): string => {
  const hex = '0123456789abcdef';
  let key = '';
  for (let i = 0; i < 32; i++) key += hex[Math.floor(Math.random() * 16)];
  return key;
};

export const SettingsProvider: React.FC<{ children: React.ReactNode }> = ({ children }) => {
  const [pcLocalIp, setPcLocalIpState] = useState('192.168.1.5:3000');
  const [deviceName, setDeviceNameState] = useState('');
  const [deviceId, setDeviceIdState] = useState('');
  const [isGlobalSyncEnabled, setGlobalSyncEnabledState] = useState(true);
  const [isFloatingBallEnabled, setFloatingBallEnabledState] = useState(false);
  const [defaultTargetDeviceName, setDefaultTargetDeviceNameState] = useState('');
  const [floatingBallSize, setFloatingBallSizeState] = useState(48);
  const [floatingBallAutoHide, setFloatingBallAutoHideState] = useState(3000);
  const [pairedDevices, setPairedDevicesState] = useState<PairedDevice[]>([]);
  const [pairingKey, setPairingKeyState] = useState('');

  useEffect(() => {
    const initStorage = async () => {
      const storedIp = await AsyncStorage.getItem('@pcLocalIp');
      if (storedIp) setPcLocalIpState(storedIp);

      const storedName = await AsyncStorage.getItem('@deviceName');
      if (storedName) setDeviceNameState(storedName);

      const storedGlobalSync = await AsyncStorage.getItem('@isGlobalSyncEnabled');
      if (storedGlobalSync !== null) setGlobalSyncEnabledState(storedGlobalSync === 'true');

      const storedFloatingBall = await AsyncStorage.getItem('@isFloatingBallEnabled');
      if (storedFloatingBall !== null) setFloatingBallEnabledState(storedFloatingBall === 'true');

      const storedDefaultTarget = await AsyncStorage.getItem('@defaultTargetDeviceName');
      if (storedDefaultTarget) setDefaultTargetDeviceNameState(storedDefaultTarget);

      const storedBallSize = await AsyncStorage.getItem('@floatingBallSize');
      if (storedBallSize) setFloatingBallSizeState(parseInt(storedBallSize, 10));

      const storedAutoHide = await AsyncStorage.getItem('@floatingBallAutoHide');
      if (storedAutoHide) setFloatingBallAutoHideState(parseInt(storedAutoHide, 10));

      let storedId = await AsyncStorage.getItem('@deviceId');
      if (!storedId) {
        storedId = 'MOB-' + Date.now().toString(36) + Math.random().toString(36).substring(2, 7);
        await AsyncStorage.setItem('@deviceId', storedId);
      }
      setDeviceIdState(storedId);

      // ── Paired Devices ──
      const storedDevices = await AsyncStorage.getItem('@pairedDevices');
      if (storedDevices) {
        try { setPairedDevicesState(JSON.parse(storedDevices)); } catch {}
      }

      // ── Pairing Key (also stored as 'pairingKey' for backward compat with index.tsx) ──
      let storedKey = await AsyncStorage.getItem('pairingKey');
      if (storedKey) {
        setPairingKeyState(storedKey);
      }
      // Note: pairingKey may remain '' until user pairs via QR/code — that's intentional
    };
    initStorage();
  }, []);

  const setPcLocalIp = async (ip: string) => {
    setPcLocalIpState(ip);
    await AsyncStorage.setItem('@pcLocalIp', ip);
  };

  const setDeviceName = async (name: string) => {
    setDeviceNameState(name);
    await AsyncStorage.setItem('@deviceName', name);
  };

  const setGlobalSyncEnabled = async (val: boolean) => {
    setGlobalSyncEnabledState(val);
    await AsyncStorage.setItem('@isGlobalSyncEnabled', val.toString());
  };

  const setFloatingBallEnabled = async (val: boolean) => {
    setFloatingBallEnabledState(val);
    await AsyncStorage.setItem('@isFloatingBallEnabled', val.toString());
  };

  const setDefaultTargetDeviceName = async (name: string) => {
    setDefaultTargetDeviceNameState(name);
    await AsyncStorage.setItem('@defaultTargetDeviceName', name);
  };

  const setFloatingBallSize = async (val: number) => {
    setFloatingBallSizeState(val);
    await AsyncStorage.setItem('@floatingBallSize', val.toString());
  };

  const setFloatingBallAutoHide = async (val: number) => {
    setFloatingBallAutoHideState(val);
    await AsyncStorage.setItem('@floatingBallAutoHide', val.toString());
  };

  // ── Paired Devices ──
  const addPairedDevice = async (device: PairedDevice) => {
    setPairedDevicesState(prev => {
      // Dedup: update if already exists, otherwise add (max 5)
      const existing = prev.findIndex(d => d.deviceId === device.deviceId);
      let updated: PairedDevice[];
      if (existing >= 0) {
        updated = [...prev];
        updated[existing] = { ...device, pairedAt: prev[existing].pairedAt }; // keep original pairedAt
      } else {
        updated = [...prev, device];
        if (updated.length > 5) updated = updated.slice(-5); // keep latest 5
      }
      AsyncStorage.setItem('@pairedDevices', JSON.stringify(updated)).catch(() => {});
      return updated;
    });
  };

  const removePairedDevice = async (deviceId: string) => {
    setPairedDevicesState(prev => {
      const updated = prev.filter(d => d.deviceId !== deviceId);
      AsyncStorage.setItem('@pairedDevices', JSON.stringify(updated)).catch(() => {});
      return updated;
    });
  };

  const regeneratePairingKey = async (): Promise<string> => {
    const newKey = generatePairingKey();
    setPairingKeyState(newKey);
    await AsyncStorage.setItem('pairingKey', newKey);
    return newKey;
  };

  return (
    <SettingsContext.Provider value={{ pcLocalIp, setPcLocalIp, deviceName, setDeviceName, deviceId, isGlobalSyncEnabled, setGlobalSyncEnabled, isFloatingBallEnabled, setFloatingBallEnabled, defaultTargetDeviceName, setDefaultTargetDeviceName, floatingBallSize, setFloatingBallSize, floatingBallAutoHide, setFloatingBallAutoHide, pairedDevices, addPairedDevice, removePairedDevice, pairingKey, regeneratePairingKey }}>
      {children}
    </SettingsContext.Provider>
  );
};
