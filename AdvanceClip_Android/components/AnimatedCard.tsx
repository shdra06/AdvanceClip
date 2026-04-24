/**
 * AnimatedCard — Premium card wrapper with staggered entrance + press feedback
 * 
 * Usage:
 *   <AnimatedCard index={i} onPress={() => ...}>
 *     <YourCardContent />
 *   </AnimatedCard>
 */
import React, { useEffect } from 'react';
import { ViewStyle, StyleProp } from 'react-native';
import Animated, {
  useSharedValue,
  useAnimatedStyle,
  withSpring,
  withTiming,
  withDelay,
  Easing,
  interpolate,
  runOnJS,
} from 'react-native-reanimated';
import { Gesture, GestureDetector } from 'react-native-gesture-handler';
import { spring as springConfig, timing, colors, shadows } from '../styles/theme';

interface AnimatedCardProps {
  children: React.ReactNode;
  index?: number;
  onPress?: () => void;
  onLongPress?: () => void;
  style?: StyleProp<ViewStyle>;
  /** Skip entrance animation (e.g., for items already visible) */
  skipEntrance?: boolean;
}

export default function AnimatedCard({
  children,
  index = 0,
  onPress,
  onLongPress,
  style,
  skipEntrance = false,
}: AnimatedCardProps) {
  // ─── Entrance Animation ───
  const entrance = useSharedValue(skipEntrance ? 1 : 0);

  useEffect(() => {
    if (!skipEntrance) {
      const delay = Math.min(index * timing.staggerDelay, 300); // cap at 300ms
      entrance.value = withDelay(
        delay,
        withTiming(1, {
          duration: timing.entranceDuration,
          easing: Easing.bezier(0.22, 1, 0.36, 1),
        })
      );
    }
  }, []);

  // ─── Press Animation ───
  const pressed = useSharedValue(0);

  const gesture = Gesture.Tap()
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
    .minDuration(400)
    .onStart(() => {
      if (onLongPress) {
        runOnJS(onLongPress)();
      }
    });

  const composed = onLongPress
    ? Gesture.Race(gesture, longPressGesture)
    : gesture;

  // ─── Animated Styles ───
  const animatedStyle = useAnimatedStyle(() => {
    const scale = interpolate(pressed.value, [0, 1], [1, 0.975]);
    const translateY = interpolate(entrance.value, [0, 1], [16, 0]);
    const opacity = interpolate(entrance.value, [0, 0.3, 1], [0, 0.4, 1]);
    // Shadow depth reduces on press for "pushing down" effect
    const elevation = interpolate(pressed.value, [0, 1], [4, 1]);

    return {
      transform: [{ scale }, { translateY }],
      opacity,
      elevation,
    };
  });

  return (
    <GestureDetector gesture={composed}>
      <Animated.View style={[animatedStyle, style]}>
        {children}
      </Animated.View>
    </GestureDetector>
  );
}
