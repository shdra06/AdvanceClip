using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace AdvanceClip.Classes
{
    /// <summary>
    /// Smooth scrolling via frame-based interpolation. No WPF animations — 
    /// just lerps the scroll offset toward the target each render frame.
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

        private const double SCROLL_SPEED = 90.0;   // Pixels per wheel notch
        private const double LERP_FACTOR = 0.15;     // 0-1: lower = smoother/slower, higher = snappier

        private class ScrollData
        {
            public double TargetOffset;
            public bool IsScrolling;
        }

        private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is ScrollViewer sv)
            {
                if ((bool)e.NewValue)
                {
                    sv.PreviewMouseWheel += OnMouseWheel;
                    sv.Loaded += OnLoaded;
                }
                else
                {
                    sv.PreviewMouseWheel -= OnMouseWheel;
                    sv.Loaded -= OnLoaded;
                }
            }
        }

        private static void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (sender is ScrollViewer sv)
            {
                var data = new ScrollData { TargetOffset = sv.VerticalOffset, IsScrolling = false };
                sv.SetValue(ScrollDataProperty, data);
            }
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

                // Accumulate scroll delta
                double delta = -(e.Delta / 120.0) * SCROLL_SPEED;
                data.TargetOffset += delta;

                // Clamp
                data.TargetOffset = Math.Max(0, Math.Min(data.TargetOffset, sv.ScrollableHeight));

                // Start render loop if not already running
                if (!data.IsScrolling)
                {
                    data.IsScrolling = true;
                    CompositionTarget.Rendering += CreateHandler(sv, data);
                }
            }
        }

        private static EventHandler CreateHandler(ScrollViewer sv, ScrollData data)
        {
            EventHandler handler = null;
            handler = (s, e) =>
            {
                double current = sv.VerticalOffset;
                double diff = data.TargetOffset - current;

                if (Math.Abs(diff) < 0.5)
                {
                    // Close enough — snap and stop
                    sv.ScrollToVerticalOffset(data.TargetOffset);
                    data.IsScrolling = false;
                    CompositionTarget.Rendering -= handler;
                    return;
                }

                // Lerp toward target
                double next = current + diff * LERP_FACTOR;
                sv.ScrollToVerticalOffset(next);
            };
            return handler;
        }
    }
}
