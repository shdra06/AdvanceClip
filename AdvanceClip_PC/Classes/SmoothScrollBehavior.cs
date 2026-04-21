using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;

namespace AdvanceClip.Classes
{
    /// <summary>
    /// Attached behavior that provides smooth, fluid scrolling for any ScrollViewer.
    /// Replaces WPF's default discrete line-by-line scrolling with momentum-based animation.
    /// </summary>
    public static class SmoothScrollBehavior
    {
        // Attached property to enable smooth scrolling
        public static readonly DependencyProperty IsEnabledProperty =
            DependencyProperty.RegisterAttached(
                "IsEnabled",
                typeof(bool),
                typeof(SmoothScrollBehavior),
                new PropertyMetadata(false, OnIsEnabledChanged));

        public static bool GetIsEnabled(DependencyObject obj) => (bool)obj.GetValue(IsEnabledProperty);
        public static void SetIsEnabled(DependencyObject obj, bool value) => obj.SetValue(IsEnabledProperty, value);

        // Track target offset for cumulative scrolling
        private static readonly DependencyProperty TargetVerticalOffsetProperty =
            DependencyProperty.RegisterAttached(
                "TargetVerticalOffset",
                typeof(double),
                typeof(SmoothScrollBehavior),
                new PropertyMetadata(0.0));

        // Animatable proxy property for ScrollViewer.VerticalOffset (which isn't directly animatable)
        private static readonly DependencyProperty AnimatedVerticalOffsetProperty =
            DependencyProperty.RegisterAttached(
                "AnimatedVerticalOffset",
                typeof(double),
                typeof(SmoothScrollBehavior),
                new PropertyMetadata(0.0, OnAnimatedVerticalOffsetChanged));

        private static void OnAnimatedVerticalOffsetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is ScrollViewer sv)
            {
                sv.ScrollToVerticalOffset((double)e.NewValue);
            }
        }

        // Scroll sensitivity: pixels per mouse wheel notch (lower = smoother, less per scroll)
        private const double SCROLL_AMOUNT = 80.0;
        // Animation duration in milliseconds
        private const int ANIMATION_DURATION_MS = 300;

        private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is ScrollViewer scrollViewer)
            {
                if ((bool)e.NewValue)
                {
                    scrollViewer.PreviewMouseWheel += OnPreviewMouseWheel;
                    scrollViewer.ScrollChanged += OnScrollChanged;
                }
                else
                {
                    scrollViewer.PreviewMouseWheel -= OnPreviewMouseWheel;
                    scrollViewer.ScrollChanged -= OnScrollChanged;
                }
            }
        }

        private static void OnScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            // Keep target in sync when scroll changes externally (e.g. content resize)
            if (sender is ScrollViewer sv && e.ExtentHeightChange != 0)
            {
                sv.SetValue(TargetVerticalOffsetProperty, sv.VerticalOffset);
            }
        }

        private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (sender is ScrollViewer scrollViewer)
            {
                e.Handled = true;

                double currentTarget = (double)scrollViewer.GetValue(TargetVerticalOffsetProperty);

                // If target is way off from actual (e.g. first scroll), sync it
                if (Math.Abs(currentTarget - scrollViewer.VerticalOffset) > scrollViewer.ViewportHeight)
                {
                    currentTarget = scrollViewer.VerticalOffset;
                }

                // Calculate new target: accumulate delta for responsive feel
                double delta = -e.Delta / 120.0 * SCROLL_AMOUNT;
                double newTarget = currentTarget + delta;

                // Clamp to valid range
                newTarget = Math.Max(0, Math.Min(newTarget, scrollViewer.ScrollableHeight));

                scrollViewer.SetValue(TargetVerticalOffsetProperty, newTarget);

                // Animate from current position to target
                var animation = new DoubleAnimation
                {
                    From = scrollViewer.VerticalOffset,
                    To = newTarget,
                    Duration = TimeSpan.FromMilliseconds(ANIMATION_DURATION_MS),
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                    FillBehavior = FillBehavior.Stop
                };

                // When animation completes, set the final position
                animation.Completed += (s, _) =>
                {
                    scrollViewer.ScrollToVerticalOffset(newTarget);
                    scrollViewer.SetValue(AnimatedVerticalOffsetProperty, newTarget);
                };

                scrollViewer.BeginAnimation(AnimatedVerticalOffsetProperty, animation, HandoffBehavior.SnapshotAndReplace);
            }
        }

        /// <summary>
        /// Apply smooth scrolling globally to all ScrollViewers in the visual tree of a window.
        /// Call this once from a window's Loaded event.
        /// </summary>
        public static void ApplyToAll(DependencyObject root)
        {
            ApplyToVisualTree(root);
        }

        private static void ApplyToVisualTree(DependencyObject parent)
        {
            int childCount = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < childCount; i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
                if (child is ScrollViewer sv)
                {
                    SetIsEnabled(sv, true);
                }
                ApplyToVisualTree(child);
            }
        }
    }
}
