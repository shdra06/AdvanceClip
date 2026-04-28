using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace AdvanceClip.Classes
{
    /// <summary>
    /// Velocity-based smooth scrolling (like iOS/Android physics).
    /// Each wheel event adds velocity. Each frame moves by velocity × deltaTime,
    /// then decays velocity with friction. No "long tail" — stops cleanly.
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

        // Velocity added per wheel notch (pixels/second)
        private const double VELOCITY_PER_NOTCH = 800.0;

        // Friction coefficient per second — velocity is multiplied by this^dt each frame
        // 0.01 means velocity drops to 1% after 1 second (stops in ~0.3s)
        private const double FRICTION_PER_SECOND = 0.005;

        // Maximum velocity cap — prevents insane accumulation from rapid flinging
        private const double MAX_VELOCITY = 5000.0;

        // Stop when velocity drops below this (pixels/second)
        private const double STOP_VELOCITY = 15.0;

        private class ScrollData
        {
            public double Velocity;       // Current scroll velocity in pixels/second
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
                    Velocity = 0,
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
                    data = new ScrollData { Velocity = 0 };
                    sv.SetValue(ScrollDataProperty, data);
                }

                // Each notch = 120 delta units. Convert to velocity.
                // Negative delta = scroll down (positive velocity moves content up)
                double notches = -(e.Delta / 120.0);
                double addedVelocity = notches * VELOCITY_PER_NOTCH;

                // If scrolling in the same direction, add velocity. If reversing, reset.
                if (Math.Sign(addedVelocity) == Math.Sign(data.Velocity))
                    data.Velocity += addedVelocity;
                else
                    data.Velocity = addedVelocity;

                // Cap velocity
                data.Velocity = Math.Max(-MAX_VELOCITY, Math.Min(data.Velocity, MAX_VELOCITY));

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
            double dt = (now - data.LastFrameTime).TotalSeconds;
            data.LastFrameTime = now;

            // Clamp dt to avoid huge jumps after app freeze/sleep
            if (dt > 0.1) dt = 0.016;
            if (dt <= 0) return;

            // Apply friction: velocity decays exponentially over time
            // v' = v * friction^dt
            data.Velocity *= Math.Pow(FRICTION_PER_SECOND, dt);

            // Stop when velocity is negligible
            if (Math.Abs(data.Velocity) < STOP_VELOCITY)
            {
                StopAnimation(sv);
                return;
            }

            // Move: position += velocity * dt
            double displacement = data.Velocity * dt;
            double newOffset = sv.VerticalOffset + displacement;

            // Clamp to valid range
            newOffset = Math.Max(0, Math.Min(newOffset, sv.ScrollableHeight));

            // If we hit the edge, kill velocity (no bounce)
            if (newOffset <= 0 || newOffset >= sv.ScrollableHeight)
                data.Velocity = 0;

            sv.ScrollToVerticalOffset(newOffset);
        }

        private static void StopAnimation(ScrollViewer sv)
        {
            var data = sv.GetValue(ScrollDataProperty) as ScrollData;
            if (data != null)
            {
                data.IsActive = false;
                data.Velocity = 0;
                if (data.RenderHandler != null)
                {
                    CompositionTarget.Rendering -= data.RenderHandler;
                    data.RenderHandler = null;
                }
            }
        }
    }
}
