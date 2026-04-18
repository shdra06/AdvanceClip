import { DarkTheme, DefaultTheme, ThemeProvider } from '@react-navigation/native';
import { Stack } from 'expo-router';
import { StatusBar } from 'expo-status-bar';
import { LogBox } from 'react-native';
import 'react-native-reanimated';
import { useEffect } from 'react';
import * as BackgroundFetch from 'expo-background-fetch';
import * as TaskManager from 'expo-task-manager';
// import * as Notifications from 'expo-notifications';
import { database } from '../firebaseConfig';
import { ref, get, query, limitToLast } from 'firebase/database';

// Ignore non-fatal warnings
LogBox.ignoreLogs([
  'Due to changes in Androids permission requirements',
  '@firebase/database: FIREBASE WARNING'
]);

import { useColorScheme } from '@/hooks/use-color-scheme';
import { SettingsProvider } from '../context/SettingsContext';

export const unstable_settings = {
  anchor: '(tabs)',
};

// Notifications.setNotificationHandler({
//   handleNotification: async () => ({
//     shouldShowAlert: true,
//     shouldPlaySound: true,
//     shouldSetBadge: false,
//   }),
// });

import { Platform } from 'react-native';

const BACKGROUND_FETCH_TASK = 'background-clipboard-sync';

if (Platform.OS !== 'web') {
  TaskManager.defineTask(BACKGROUND_FETCH_TASK, async () => {
    try {
       const snaps = await get(query(ref(database, 'clipboard'), limitToLast(1)));
       // if (snaps.exists()) {
       //     await Notifications.scheduleNotificationAsync({
       //        content: {
       //           title: "AdvanceClip Payload Detected",
       //           body: "A new payload hit the mesh. Tap to inject instantly!",
       //        },
       //        trigger: null,
       //     });
       // }
       return BackgroundFetch.BackgroundFetchResult.NewData;
    } catch (err) {
       return BackgroundFetch.BackgroundFetchResult.Failed;
    }
  });
}

export default function RootLayout() {
  const colorScheme = useColorScheme();

  useEffect(() => {
     if (Platform.OS !== 'web') {
         // Notifications.requestPermissionsAsync();
         BackgroundFetch.registerTaskAsync(BACKGROUND_FETCH_TASK, {
            minimumInterval: 15 * 60,
            stopOnTerminate: false,
            startOnBoot: true,
         }).catch(console.warn);
     }
  }, []);

  return (
    <ThemeProvider value={colorScheme === 'dark' ? DarkTheme : DefaultTheme}>
      <SettingsProvider>
        <Stack>
          <Stack.Screen name="(tabs)" options={{ headerShown: false }} />
        </Stack>
        <StatusBar style="auto" />
      </SettingsProvider>
    </ThemeProvider>
  );
}
