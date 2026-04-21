// Fallback for using MaterialIcons on Android and web.

import MaterialIcons from '@expo/vector-icons/MaterialIcons';
import { SymbolWeight, SymbolViewProps } from 'expo-symbols';
import { ComponentProps } from 'react';
import { OpaqueColorValue, type StyleProp, type TextStyle } from 'react-native';

type IconMapping = Record<SymbolViewProps['name'], ComponentProps<typeof MaterialIcons>['name']>;
type IconSymbolName = keyof typeof MAPPING;

/**
 * Add your SF Symbols to Material Icons mappings here.
 * - see Material Icons in the [Icons Directory](https://icons.expo.fyi).
 * - see SF Symbols in the [SF Symbols](https://developer.apple.com/sf-symbols/) app.
 */
const MAPPING = {
  'house.fill': 'home',
  'paperplane.fill': 'send',
  'chevron.left.forwardslash.chevron.right': 'code',
  'chevron.right': 'chevron-right',
  'chevron.down': 'keyboard-arrow-down',
  'chevron.up': 'keyboard-arrow-up',
  'photo': 'image',
  'photo.fill': 'image',
  'link': 'link',
  'globe': 'public',
  'doc.richtext': 'description',
  'doc.text.fill': 'article',
  'qrcode': 'qr-code',
  'doc.text': 'article',
  'doc.on.doc': 'content-copy',
  'arrow.up.right': 'open-in-new',
  'arrow.down': 'file-download',
  'arrow.down.circle': 'download-for-offline',
  'desktopcomputer': 'computer',
  'iphone': 'smartphone',
  'photo.on.rectangle.angled': 'photo-library',
  'paperclip': 'attach-file',
  'arrow.up.circle.fill': 'send',
  'pin': 'push-pin',
  'pin.fill': 'push-pin',
  'trash': 'delete',
  'cloud.fill': 'cloud',
  'cloud': 'cloud-queue',
  'repeat': 'sync',
  'tray.full': 'inbox',
  'gear': 'settings',
  'laptopcomputer': 'laptop-mac',
  'camera.fill': 'camera-alt',
  'doc.fill': 'insert-drive-file',
  'curlybraces': 'code',
  'network': 'wifi',
  'xmark': 'close',
  'xmark.circle.fill': 'cancel',
  'square.and.arrow.up': 'share',
  'play.rectangle.fill': 'play-arrow',
  'checkmark': 'check',
  'folder': 'folder',
  'folder.fill': 'folder',
  'magnifyingglass': 'search',
  'pencil': 'edit',
  'hammer.fill': 'build',
  'antenna.radiowaves.left.and.right': 'cell-tower',
  'chevron.left': 'chevron-left',
  'play.fill': 'play-arrow',
  'bolt.fill': 'flash-on',
  'macwindow': 'picture-in-picture-alt',
} as IconMapping;

/**
 * An icon component that uses native SF Symbols on iOS, and Material Icons on Android and web.
 * This ensures a consistent look across platforms, and optimal resource usage.
 * Icon `name`s are based on SF Symbols and require manual mapping to Material Icons.
 */
export function IconSymbol({
  name,
  size = 24,
  color,
  style,
}: {
  name: IconSymbolName;
  size?: number;
  color: string | OpaqueColorValue;
  style?: StyleProp<TextStyle>;
  weight?: SymbolWeight;
}) {
  return <MaterialIcons color={color} size={size} name={MAPPING[name]} style={style} />;
}
