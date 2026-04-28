using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace AdvanceClip.Classes
{
    /// <summary>
    /// Smooth scrolling via a single CompositionTarget.Rendering loop per ScrollViewer.
    /// Uses time-based exponential decay for buttery smooth, consistent deceleration
    /// regardless of frame rate. Only one handler is ever active per ScrollViewer.
    /// 
    /// Trackpad-aware: keeps the render loop alive for a grace period after reaching
    /// the target, so late-arriving fling events merge into the current animation
    /// instead of causing a "pause → jitter" restart.
    /// </summary>
    public static class SmoothScrollBehavior
    {
        public static readonly DependencyProperty IsEnabledProperty =
            DependencyProperty.RegisterAttached(
                "IsEnabled",
                typeof(bool),
                typeof(SmoothScrollBehavior),
                new PropertyMetadata(false, OnIsEnabledChanged));

        public static bool GetIsEnabled(DependencyObject obj) => (bool)obj.GetValue(IsEnabledProperty);
        public static void SetIsEnabled(DependencyObject obj, bool value) => obj.SetValue(IsEnabledProperty, value);

        private static readonly DependencyProperty ScrollDataProperty =
            DependencyProperty.RegisterAttached("ScrollData", typeof(ScrollData), typeof(SmoothScrollBehavior));

        // Pixels per wheel notch — controls how far each scroll tick moves
        private const double PIXELS_PER_NOTCH = 70.0;

        // Decay half-life in seconds — how quickly the scroll decelerates
        private const double DECAY_HALF_LIFE = 0.10;

        // Stop threshold in pixels — snap to target when closer than this
        private const double SNAP_THRESHOLD = 0.4;

        // Grace period after reaching target — absorbs late trackpad fling events
        // without restarting the animation (prevents pause-jitter at end of fling)
        private const double GRACE_PERIOD_SECONDS = 0.25;

        private class ScrollData
        {
            public double TargetOffset;
            public EventHandler? RenderHandler;
            public bool IsActive;
            public DateTime LastFrameTime;
            public DateTime LastWheelTime;    // When the last wheel event arrived
            public bool IsInGracePeriod;      // True when animation reached target but grace window is open
        }

        private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is ScrollViewer sv)
            {
                if ((bool)e.NewValue)
                {
                    sv.PreviewMouseWheel += OnMouseWheel;
                    sv.Loaded += OnLoaded;
                    sv.Unloaded += OnUnloaded;
                }
                else
                {
                    sv.PreviewMouseWheel -= OnMouseWheel;
                    sv.Loaded -= OnLoaded;
                    sv.Unloaded -= OnUnloaded;
                    StopAnimation(sv);
                }
            }
        }

        private static void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (sender is ScrollViewer sv)
            {
                var data = new ScrollData
                {
                    TargetOffset = sv.VerticalOffset,
                    IsActive = false,
                    LastFrameTime = DateTime.UtcNow,
                    LastWheelTime = DateTime.UtcNow
                };
                sv.SetValue(ScrollDataProperty, data);
            }
        }

        private static void OnUnloaded(object sender, RoutedEventArgs e)
        {
            if (sender is ScrollViewer sv)
                StopAnimation(sv);
        }

        private static void OnMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (sender is ScrollViewer sv)
            {
                e.Handled = true;

                var data = sv.GetValue(ScrollDataProperty) as ScrollData;
                if (data == null)
                {
                    data = new ScrollData { TargetOffset = sv.VerticalOffset };
                    sv.SetValue(ScrollDataProperty, data);
                }

                data.LastWheelTime = DateTime.UtcNow;

                // If we were in grace period, exit it — new input means real scrolling
                data.IsInGracePeriod = false;

                // Sync target with current position if animation was fully idle
                if (!data.IsActive)
                    data.TargetOffset = sv.VerticalOffset;

                // Accumulate: negative delta = scroll down, positive = scroll up
                double delta = -(e.Delta / 120.0) * PIXELS_PER_NOTCH;
                data.TargetOffset += delta;

                // Clamp to valid range
                data.TargetOffset = Math.Max(0, Math.Min(data.TargetOffset, sv.ScrollableHeight));

                // Start the render loop if not already running
                if (!data.IsActive)
                {
                    data.IsActive = true;
                    data.LastFrameTime = DateTime.UtcNow;
                    data.RenderHandler = (s, args) => OnRenderFrame(sv, data);
                    CompositionTarget.Rendering += data.RenderHandler;
                }
            }
        }

        private static void OnRenderFrame(ScrollViewer sv, ScrollData data)
        {
            var now = DateTime.UtcNow;
            double dtSeconds = (now - data.LastFrameTime).TotalSeconds;
            data.LastFrameTime = now;

            // Clamp delta-time to avoid huge jumps after app freeze/sleep
            if (dtSeconds > 0.1) dtSeconds = 0.016;

            double current = sv.VerticalOffset;
            double diff = data.TargetOffset - current;

            if (Math.Abs(diff) < SNAP_THRESHOLD)
            {
                // Close enough — snap to target
                if (Math.Abs(diff) > 0.01)
                    sv.ScrollToVerticalOffset(data.TargetOffset);

                // Enter grace period instead of stopping immediately.
                // This absorbs late trackpad fling events that arrive after
                // the animation visually completed.
                if (!data.IsInGracePeriod)
                {
                    data.IsInGracePeriod = true;
                }

                // Check if grace period has expired (no new wheel events)
                double sinceLastWheel = (now - data.LastWheelTime).TotalSeconds;
                if (sinceLastWheel > GRACE_PERIOD_SECONDS)
                {
                    StopAnimation(sv);
                }
                return;
            }

            // If we were in grace period but diff grew (new input arrived), exit grace
            data.IsInGracePeriod = false;

            // Exponential decay: move a fraction of the remaining distance each frame
            // fraction = 1 - 2^(-dt/halfLife) — frame-rate independent
            double fraction = 1.0 - Math.Pow(2.0, -dtSeconds / DECAY_HALF_LIFE);
            double next = current + diff * fraction;
            sv.ScrollToVerticalOffset(next);
        }

        private static void StopAnimation(ScrollViewer sv)
        {
            var data = sv.GetValue(ScrollDataProperty) as ScrollData;
            if (data != null && data.IsActive)
            {
                data.IsActive = false;
                data.IsInGracePeriod = false;
                if (data.RenderHandler != null)
                {
                    CompositionTarget.Rendering -= data.RenderHandler;
                    data.RenderHandler = null;
                }
            }
        }
    }
}
