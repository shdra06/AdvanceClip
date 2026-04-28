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
        // Higher = longer glide, Lower = snappier stop
        private const double DECAY_HALF_LIFE = 0.12;

        // Stop threshold in pixels — snap to target when closer than this
        private const double SNAP_THRESHOLD = 0.5;

        private class ScrollData
        {
            public double TargetOffset;
            public EventHandler? RenderHandler;
            public bool IsActive;
            public DateTime LastFrameTime;
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
                    LastFrameTime = DateTime.UtcNow
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

                // Sync target with current position if animation was idle
                // (prevents jumps when the list size changed or items were added/removed)
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

                    // Create a single handler — reused for the lifetime of this animation
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
                // Close enough — snap and stop
                sv.ScrollToVerticalOffset(data.TargetOffset);
                StopAnimation(sv);
                return;
            }

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
                if (data.RenderHandler != null)
                {
                    CompositionTarget.Rendering -= data.RenderHandler;
                    data.RenderHandler = null;
                }
            }
        }
    }
}
