/**
 * AnimatedPressable — Universal press-feedback wrapper
 * Scale down 0.96 on press, spring back. Used for buttons, chips, action icons.
 */
import React from 'react';
import { ViewStyle, StyleProp } from 'react-native';
import Animated, {
  useSharedValue,
  useAnimatedStyle,
  withSpring,
  interpolate,
  runOnJS,
} from 'react-native-reanimated';
import { Gesture, GestureDetector } from 'react-native-gesture-handler';
import { spring as springConfig } from '../styles/theme';

interface AnimatedPressableProps {
  children: React.ReactNode;
  onPress?: () => void;
  onLongPress?: () => void;
  style?: StyleProp<ViewStyle>;
  scaleDown?: number;
  disabled?: boolean;
}

export default function AnimatedPressable({
  children,
  onPress,
  onLongPress,
  style,
  scaleDown = 0.96,
  disabled = false,
}: AnimatedPressableProps) {
  const pressed = useSharedValue(0);

  const tapGesture = Gesture.Tap()
    .enabled(!disabled)
    .onBegin(() => {
      pressed.value = withSpring(1, springConfig.press);
    })
    .onFinalize((_e, success) => {
      pressed.value = withSpring(0, springConfig.bounce);
      if (success && onPress) {
        runOnJS(onPress)();
      }
    });

  const longPressGesture = Gesture.LongPress()
    .enabled(!disabled && !!onLongPress)
    .minDuration(400)
    .onStart(() => {
      if (onLongPress) runOnJS(onLongPress)();
    });

  const composed = onLongPress
    ? Gesture.Race(tapGesture, longPressGesture)
    : tapGesture;

  const animatedStyle = useAnimatedStyle(() => {
    const scale = interpolate(pressed.value, [0, 1], [1, scaleDown]);
    const opacity = interpolate(pressed.value, [0, 1], [1, 0.8]);
    return { transform: [{ scale }], opacity };
  });

  return (
    <GestureDetector gesture={composed}>
      <Animated.View style={[animatedStyle, style]}>
        {children}
      </Animated.View>
    </GestureDetector>
  );
}
