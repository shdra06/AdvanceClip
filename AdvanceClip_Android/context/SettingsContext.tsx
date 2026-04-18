import React, { createContext, useState, useEffect, useContext } from 'react';
import AsyncStorage from '@react-native-async-storage/async-storage';

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
});

export const useSettings = () => useContext(SettingsContext);

export const SettingsProvider: React.FC<{ children: React.ReactNode }> = ({ children }) => {
  const [pcLocalIp, setPcLocalIpState] = useState('192.168.1.5:3000');
  const [deviceName, setDeviceNameState] = useState('');
  const [deviceId, setDeviceIdState] = useState('');
  const [isGlobalSyncEnabled, setGlobalSyncEnabledState] = useState(true);
  const [isFloatingBallEnabled, setFloatingBallEnabledState] = useState(false);
  const [defaultTargetDeviceName, setDefaultTargetDeviceNameState] = useState('');
  const [floatingBallSize, setFloatingBallSizeState] = useState(48);
  const [floatingBallAutoHide, setFloatingBallAutoHideState] = useState(3000);

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

  return (
    <SettingsContext.Provider value={{ pcLocalIp, setPcLocalIp, deviceName, setDeviceName, deviceId, isGlobalSyncEnabled, setGlobalSyncEnabled, isFloatingBallEnabled, setFloatingBallEnabled, defaultTargetDeviceName, setDefaultTargetDeviceName, floatingBallSize, setFloatingBallSize, floatingBallAutoHide, setFloatingBallAutoHide }}>
      {children}
    </SettingsContext.Provider>
  );
};
