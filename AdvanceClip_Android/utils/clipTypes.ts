import * as FileSystem from 'expo-file-system/legacy';

// ═══ ClipItem Type ═══
export type ClipItem = {
  id?: string;
  Title: string;
  Type: string;
  Raw: string;
  Time: string;
  SourceDeviceName?: string;
  SourceDeviceType?: string;
  IsPinned?: boolean;
  Timestamp?: number;
  CachedUri?: string;
};

// ═══ Organized Storage Paths ═══
export const DOWNLOAD_BASE = `${(FileSystem as any).documentDirectory}AdvanceClip/Downloads/`;
export const SYNC_CACHE_BASE = `${(FileSystem as any).cacheDirectory}AdvanceClip/SyncCache/`;
export const CONVERTED_BASE = `${(FileSystem as any).documentDirectory}AdvanceClip/Converted/`;
export const IMAGE_CACHE_BASE = `${(FileSystem as any).documentDirectory}AdvanceClip/Downloads/Images/`;

/** User-initiated downloads: documentDirectory/AdvanceClip/Downloads/{subfolder}/{filename} */
export const getDownloadPath = async (subfolder: string, filename: string) => {
  const dir = `${DOWNLOAD_BASE}${subfolder}/`;
  await FileSystem.makeDirectoryAsync(dir, { intermediates: true }).catch(() => {});
  return `${dir}${filename}`;
};

/** Auto-sync temp files: cacheDirectory/AdvanceClip/SyncCache/{filename} */
export const getSyncCachePath = async (filename: string) => {
  await FileSystem.makeDirectoryAsync(SYNC_CACHE_BASE, { intermediates: true }).catch(() => {});
  return `${SYNC_CACHE_BASE}${filename}`;
};

/** Conversion outputs: documentDirectory/AdvanceClip/Converted/{filename} */
export const getConvertedPath = async (filename: string) => {
  await FileSystem.makeDirectoryAsync(CONVERTED_BASE, { intermediates: true }).catch(() => {});
  return `${CONVERTED_BASE}${filename}`;
};

/** Image cache: documentDirectory/AdvanceClip/Downloads/Images/{filename} */
export const getImageCachePath = async (filename: string) => {
  await FileSystem.makeDirectoryAsync(IMAGE_CACHE_BASE, { intermediates: true }).catch(() => {});
  return `${IMAGE_CACHE_BASE}${filename}`;
};
