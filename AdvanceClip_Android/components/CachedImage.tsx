import React from 'react';
import { View, Text, TouchableOpacity, ActivityIndicator } from 'react-native';
import { Image } from 'expo-image';
import * as FileSystem from 'expo-file-system/legacy';
import { IMAGE_CACHE_BASE } from '../utils/clipTypes';

const safeHash = (s: string): string => {
  let h = 0;
  for (let i = 0; i < s.length; i++) {
    h = ((h * 31) + s.charCodeAt(i)) & 0x7fffffff;
  }
  return h.toString(16);
};

/**
 * CachedImage: Downloads remote images to local cache for reliable rendering.
 * Handles local file paths, content URIs, and remote URLs.
 */
const CachedImage = React.memo(({ imgUri, onPress }: { imgUri: string; onPress: () => void }) => {
  const [localUri, setLocalUri] = React.useState<string | null>(null);
  const [failed, setFailed] = React.useState(false);

  React.useEffect(() => {
    if (!imgUri) { setFailed(true); return; }

    let cancelled = false;

    const loadImage = async () => {
      try {
        // Local file path (app storage, cache, etc.)
        if (imgUri.startsWith('file://') || imgUri.startsWith('/')) {
          const uri = imgUri.startsWith('file://') ? imgUri : `file://${imgUri}`;
          
          // Check if the file is in our app's storage (always accessible)
          const isAppLocal = imgUri.includes('AdvanceClip') || imgUri.includes('expo') || imgUri.includes('cache');
          
          if (isAppLocal) {
            const info = await FileSystem.getInfoAsync(uri);
            if (info.exists && (info as any).size > 100) {
              if (!cancelled) setLocalUri(uri);
              return;
            }
          }

          // If it's an external path (like /storage/emulated/0/...), copy to app storage
          const fname = `img_${safeHash(imgUri)}.jpg`;
          const permUri = IMAGE_CACHE_BASE + fname;
          await FileSystem.makeDirectoryAsync(IMAGE_CACHE_BASE, { intermediates: true }).catch(() => {});

          // Check if we already cached it
          const cached = await FileSystem.getInfoAsync(permUri);
          if (cached.exists && (cached as any).size > 100) {
            if (!cancelled) setLocalUri(permUri);
            return;
          }

          // Try to copy the file to app storage
          try {
            await FileSystem.copyAsync({ from: uri, to: permUri });
            const verify = await FileSystem.getInfoAsync(permUri);
            if (verify.exists && (verify as any).size > 100) {
              if (!cancelled) setLocalUri(permUri);
              return;
            }
          } catch (copyErr) {}

          // Try content URI
          try {
            const contentUri = await FileSystem.getContentUriAsync(uri);
            await FileSystem.copyAsync({ from: contentUri, to: permUri });
            if (!cancelled) setLocalUri(permUri);
            return;
          } catch (contentErr) {}

          // If all copies fail, try rendering the original URI directly
          if (!cancelled) setLocalUri(uri);
          return;
        }

        // Remote http:// URL — download to permanent storage
        if (imgUri.startsWith('http')) {
          const fname = `img_${safeHash(imgUri)}.jpg`;
          const permUri = IMAGE_CACHE_BASE + fname;
          await FileSystem.makeDirectoryAsync(IMAGE_CACHE_BASE, { intermediates: true }).catch(() => {});

          // Check cache first
          const info = await FileSystem.getInfoAsync(permUri);
          if (info.exists && (info as any).size > 500) {
            if (!cancelled) setLocalUri(permUri);
            return;
          }

          // Download with retries
          for (let attempt = 0; attempt < 3; attempt++) {
            try {
              const dl = await FileSystem.downloadAsync(imgUri, permUri, {
                headers: { 'X-Advance-Client': 'MobileCompanion' }
              });
              if (dl.status === 200) {
                if (!cancelled) setLocalUri(dl.uri);
                return;
              }
            } catch {}
            if (attempt < 2) await new Promise(r => setTimeout(r, 1000 * (attempt + 1)));
          }

          if (!cancelled) setFailed(true);
          return;
        }

        // Unknown scheme
        if (!cancelled) setFailed(true);
      } catch (err) {
        if (!cancelled) setFailed(true);
      }
    };

    loadImage();
    return () => { cancelled = true; };
  }, [imgUri]);

  // Hide broken images entirely
  if (failed) return null;

  if (!localUri) return (
    <View style={{ marginBottom: 8, height: 120, borderRadius: 12, backgroundColor: '#1C202B', justifyContent: 'center', alignItems: 'center' }}>
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
        onError={() => setFailed(true)}
      />
    </TouchableOpacity>
  );
});

export default CachedImage;
