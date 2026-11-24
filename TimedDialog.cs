using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace PhotoSlideshow
{
    /// <summary>
    /// Small non-activating message window that closes itself after a few seconds.
    /// It is an owned (child) window of the window it is shown for: it stays above it,
    /// hides when the owner is minimised and closes when the owner closes.
    /// It also follows the owner when the owner is moved or resized.
    /// Calling ShowMessage again with a different text replaces the window (a window that is
    /// already showing does not resize reliably); with the same text it only restarts the timer.
    /// The window is only shown once the dispatcher is idle, so it is safe to call from a
    /// constructor (before the main window is on screen).
    /// </summary>
    internal sealed class TimedDialog : Window
    {
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_TRANSPARENT = 0x00000020;

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        private static TimedDialog? _instance;

        private const int FadeOutMs = 500;

        private DoubleAnimation? _fade;     // the fade-out currently running, if any
        private Window? _followedOwner;     // the owner whose events we are subscribed to
        private readonly TextBlock _label;
        private readonly DispatcherTimer _timer;
        public static int DefaultDurationMs = 3000;

        private TimedDialog()
        {
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            ShowActivated = false;      // don't take focus when shown

            // The window sizes itself to the text when it is shown. The size is never changed
            // afterwards: a different text gets a new window (see ShowCore).
            SizeToContent = SizeToContent.WidthAndHeight;
            SizeChanged += (_, _) => Reposition();      // the size is only known after layout

            _label = new TextBlock
            {
                Foreground = Brushes.White,
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 16,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 800,
            };
            Content = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(45, 45, 48)),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(20, 12, 20, 12),
                Child = _label,
            };

            _timer = new DispatcherTimer();
            _timer.Tick += (_, _) =>
            {
                _timer.Stop();
                FadeOutAndClose();
            };

            // never activate, even when clicked
            SourceInitialized += (_, _) =>
            {
                IntPtr hwnd = new WindowInteropHelper(this).Handle;
                int style = GetWindowLong(hwnd, GWL_EXSTYLE);
                SetWindowLong(hwnd, GWL_EXSTYLE, style | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT);
            };

            // if it ever gets activated, give the keyboard focus back to the slideshow
            // or we will eat all keys
            Activated += (_, _) =>
            {
                Window? owner = Owner;
                if (owner != null && owner.IsVisible)
                    owner.Activate();
            };

            Closed += (_, _) =>
            {
                _timer.Stop();
                _fade = null;   // a fade that was still running must not call Close() again
                StopFollowingOwner();
                if (ReferenceEquals(_instance, this))
                    _instance = null;
            };
        }

        /// <summary>
        /// Show a message and (re)start the close timer. The dialog is a child
        /// window of <paramref name="owner"/> and appears at the bottom-centre of it.
        /// </summary>
        public static void ShowMessage(Window owner, string text, int durationMs = 0)
        {
            Debug.WriteLine($"TimedDialog.ShowMessage: {text}");

            if (owner == null)
                throw new ArgumentNullException(nameof(owner));

            if (durationMs <= 0)
                durationMs = DefaultDurationMs;

            // Post at ApplicationIdle: by then the owner has been shown, rendered and
            // activated. Showing a window earlier can leave the main window behind others.
            owner.Dispatcher.BeginInvoke(
            DispatcherPriority.ApplicationIdle,
            new Action(() => ShowCore(owner, text, durationMs)));
        }

        private static void ShowCore(Window owner, string text, int durationMs)
        {
            Debug.WriteLine("ShowCore");

            // WPF can only make a window the child of one that has been shown (and not closed)
            if (!owner.IsVisible)
                return;

            TimedDialog? old = _instance;

            // Same owner and same text: just keep the window up for another full period.
            if (old != null && ReferenceEquals(old.Owner, owner) && old._label.Text == text)
            {
                old.Restart(durationMs);
                return;
            }

            // Otherwise show the text in a brand-new window, then close the old one. A new
            // window always gets the right size; resizing a visible one did not work reliably.
            // (The Owner of a visible window can't be changed either.)
            // The old window is closed after the new one is up, so there is no gap.
            var dialog = new TimedDialog { Owner = owner };
            _instance = dialog;
            dialog.FollowOwner(owner);

            // An owned window already stays above its owner. It only needs to be topmost
            // when the owner is (fullscreen mode).
            //dialog.Topmost = owner.Topmost;

            dialog.Display(text, durationMs);

            old?.Close();   // its Closed handler leaves _instance alone: that is the new window now
        }

        // ===== Following the owner =====
        // An owned window stays above its owner, but Windows does not move it along when the
        // owner moves, so we reposition it ourselves.

        private void FollowOwner(Window owner)
        {
            _followedOwner = owner;
            owner.LocationChanged += OnOwnerChanged;
            owner.StateChanged += OnOwnerChanged;       // maximised / restored
            owner.SizeChanged += OnOwnerSizeChanged;
        }

        private void StopFollowingOwner()
        {
            if (_followedOwner == null)
                return;
            _followedOwner.LocationChanged -= OnOwnerChanged;
            _followedOwner.StateChanged -= OnOwnerChanged;
            _followedOwner.SizeChanged -= OnOwnerSizeChanged;
            _followedOwner = null;
        }

        private void OnOwnerChanged(object? sender, EventArgs e)
        {
            if (IsVisible)
                Reposition();
        }

        private void OnOwnerSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (IsVisible)
                Reposition();
        }

        /// <summary>
        /// Bottom-centre of the owner's client area (=> same screen as the owner).
        /// </summary>
        private void Reposition()
        {
            Debug.WriteLine("Reposition");

            double w = ActualWidth, h = ActualHeight;
            double left, top;

            Window? owner = Owner;
            FrameworkElement? anchor = owner?.Content as FrameworkElement ?? owner;
            PresentationSource? source = anchor != null ? PresentationSource.FromVisual(anchor) : null;

            if (anchor != null && source?.CompositionTarget != null && anchor.IsVisible)
            {
                Point px = anchor.PointToScreen(new Point(anchor.ActualWidth / 2, anchor.ActualHeight));
                Point dip = source.CompositionTarget.TransformFromDevice.Transform(px);
                left = dip.X - w / 2;
                top = dip.Y - h - 40;
            }
            else
            {
                Rect wa = SystemParameters.WorkArea;
                left = wa.Left + (wa.Width - w) / 2;
                top = wa.Bottom - h - 40;
            }
            Left = left;
            Top = top;
        }

        private void FadeOutAndClose()
        {
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(FadeOutMs))
            {
                FillBehavior = FillBehavior.HoldEnd,
            };
            fade.Completed += (_, _) =>
            {
                // only close if this fade wasn't cancelled by a new message
                // (close rather than hide, so a leftover hidden window can't keep the app alive)
                if (ReferenceEquals(_fade, fade))
                    Close();
            };
            _fade = fade;
            BeginAnimation(OpacityProperty, fade);
        }

        private void CancelFade()
        {
            if (_fade == null)
                return;
            _fade = null;
            BeginAnimation(OpacityProperty, null);
            Opacity = 1;
        }

        /// <summary>Show this (new) window with the given text.</summary>
        private void Display(string text, int durationMs)
        {
            Debug.WriteLine("Display");

            _label.Text = text;
            Show();         // sizes the window to the text
            Reposition();   // the size is known now

            Restart(durationMs);
        }

        /// <summary>(Re)start the countdown; a fade-out in progress is cancelled.</summary>
        private void Restart(int durationMs)
        {
            _timer.Stop();
            CancelFade();
            _timer.Interval = TimeSpan.FromMilliseconds(durationMs);
            _timer.Start();
        }
    }
}
