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
 * Returns null for broken images to prevent blank boxes.
 */
const CachedImage = React.memo(({ imgUri, onPress }: { imgUri: string; onPress: () => void }) => {
  const [localUri, setLocalUri] = React.useState<string | null>(null);
  const [failed, setFailed] = React.useState(false);

  React.useEffect(() => {
    if (!imgUri) { setFailed(true); return; }

    // Local file:// or absolute path
    if (imgUri.startsWith('file://') || imgUri.startsWith('/')) {
      const uri = imgUri.startsWith('file://') ? imgUri : `file://${imgUri}`;
      FileSystem.getInfoAsync(uri).then(info => {
        if (info.exists) setLocalUri(uri);
        else setFailed(true);
      }).catch(() => setFailed(true));
      return;
    }

    // Remote http:// — download to permanent storage
    if (imgUri.startsWith('http')) {
      const fname = `img_${safeHash(imgUri)}.jpg`;
      const permUri = IMAGE_CACHE_BASE + fname;

      (async () => {
        try {
          await FileSystem.makeDirectoryAsync(IMAGE_CACHE_BASE, { intermediates: true }).catch(() => {});
          const info = await FileSystem.getInfoAsync(permUri);
          if (info.exists && (info as any).size > 500) {
            setLocalUri(permUri);
            return;
          }
          for (let attempt = 0; attempt < 3; attempt++) {
            try {
              const dl = await FileSystem.downloadAsync(imgUri, permUri, {
                headers: { 'X-Advance-Client': 'MobileCompanion' }
              });
              if (dl.status === 200) { setLocalUri(dl.uri); return; }
            } catch {}
            if (attempt < 2) await new Promise(r => setTimeout(r, 1000 * (attempt + 1)));
          }
          setFailed(true);
        } catch {
          setFailed(true);
        }
      })();
      return;
    }

    setFailed(true);
  }, [imgUri]);

  // Hide broken images entirely instead of showing placeholder
  if (failed) return null;
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

export default CachedImage;
