using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace PhotoSlideshow
{
    // ---------------------------------------------------------------------------
    // Usage:
    //  Commandline options:
    //        see CommandlineOptions.cs
    //  Keys: Q/Esc = quit
    //        Space = pause/unpause
    //        Left/Right = prev/next image
    //        Up/Down = increase/decrease display time 
    //        F/F11 = fullscreen toggle
    // ---------------------------------------------------------------------------

    internal static class PhotoSlideshow
    {
        private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".jpg", ".jpeg", ".png", ".bmp", ".gif"
        };

        [STAThread]
        private static int Main(string[] args)
        {
            var app = new Application();

            // get commandline arguments
            try
            {
                bool ret = CommandlineOptions.Parse(args);
                if (!ret)
                {
                    string version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "?";
                    string s = $"PhotoSlideshow - version {version}\n"
                            + CommandlineOptions.UsageString;
                    MessageBox.Show(s, "PhotoSlideshow");
                    return 0;
                }
            }
            catch (Exception ex) 
            {
                string s = $"Command line error: {ex.Message}\n"
                        + "\n"
                        + CommandlineOptions.UsageString;
                MessageBox.Show(s, "PhotoSlideshow");
                return 1;
            }

            // use application exe folder if no -f argument was given
            if (string.IsNullOrWhiteSpace(CommandlineOptions.Folder))
            {
                CommandlineOptions.Folder = AppDomain.CurrentDomain.BaseDirectory;
            }

            // default duration for messages, can be changed by the caller
            TimedDialog.DefaultDurationMs = 2000;

            // get the list of files
            // support recursive
            var files = new List<string>();
            try
            {
                SearchOption sopt = CommandlineOptions.IncludeSubfolders ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
                foreach (string f in Directory.EnumerateFiles(CommandlineOptions.Folder, "*", sopt))
                {
                    if (Extensions.Contains(Path.GetExtension(f)))
                    {
                        files.Add(f);
                    }
                }
            }
            catch (Exception ex)
            {
                string s = $"Error while searching for images: {ex.Message}\n";
                MessageBox.Show(s, "PhotoSlideshow");
                return 1;
            }

            // no files?
            if (files.Count == 0)
            {
                string s = $"No images found in:\n{CommandlineOptions.Folder}";
                MessageBox.Show(s, "PhotoSlideshow");
                return 1;
            }

            // run
            return app.Run(new SlideshowWindow(files));
        }
    }

    internal sealed class SlideshowWindow : Window
    {
        private double DisplaySeconds = 7.0;            // how long a photo stays fully shown
        private const double TransitionSeconds = 1.4;   // length of each transition
        private const double ZoomAmount = 0.07;         // 0.07 = zoom between 100% and 107%
    
        private enum TransitionKind { Crossfade, Push, SlideFade, ZoomThrough, DipToBlack, SoftWipe }

        /// <summary>
        /// One full-screen "page": black background + image. Two of these are swapped.
        /// </summary>
        private class Layer
        {
            public Grid Root { get; } = new()
            {
                Background = Brushes.Black,
                Visibility = Visibility.Collapsed,
                RenderTransformOrigin = new Point(0.5, 0.5),
                UseLayoutRounding = false,
            };
            public Image Img { get; } = new()
            {
                Stretch = Stretch.Uniform,
                RenderTransformOrigin = new Point(0.5, 0.5),
                SnapsToDevicePixels = false,
                UseLayoutRounding = false,
            };
            public ScaleTransform Zoom { get; } = new(1, 1);        // Ken Burns zoom (on the image)
            public ScaleTransform LayerScale { get; } = new(1, 1);  // transition scale (on the page)
            public TranslateTransform Shift { get; } = new(0, 0);   // transition slide (on the page)

            public Layer()
            {
                Img.RenderTransform = Zoom;
                Root.RenderTransform = new TransformGroup { Children = { LayerScale, Shift } };
                RenderOptions.SetBitmapScalingMode(Img, BitmapScalingMode.HighQuality);
                Root.Children.Add(Img);
            }

            public void Reset()
            {
                Zoom.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                Zoom.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                LayerScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                LayerScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                Shift.BeginAnimation(TranslateTransform.XProperty, null);
                Shift.BeginAnimation(TranslateTransform.YProperty, null);
                Root.BeginAnimation(OpacityProperty, null);

                Zoom.ScaleX = Zoom.ScaleY = 1;
                LayerScale.ScaleX = LayerScale.ScaleY = 1;
                Shift.X = Shift.Y = 0;
                Root.Opacity = 1;
                Root.OpacityMask = null;
            }

            public void Hide()
            {
                Reset();
                Root.Visibility = Visibility.Collapsed;
                Img.Source = null; // release the bitmap
            }
        }

        private readonly List<string> _order;
        private readonly Grid _stage = new() { Background = Brushes.Black };
        private readonly Layer[] _layers = { new Layer(), new Layer() };
        private readonly Random _rng = new();

        private int _index = -1;
        private int _pendingNav;      // +1 next, -1 previous, set by keys
        private int _paused;   // 0=not paused, 1=paused, 2=end-pause (skip to next photo)
        private bool _closing;
        private int _maxW = 1920, _maxH = 1080;   // decode size limit (screen pixels)
        private TransitionKind? _lastTransition;
        private (string Path, Task<BitmapSource?> Work)? _prefetch;

        public SlideshowWindow(List<string> files)
        {
            Title = "PhotoSlideshow";

            _order = new List<string>(files);
            if (CommandlineOptions.RandomOrder)
                Shuffle(_order);

            // set interval
            DisplaySeconds = CommandlineOptions.IntervalSeconds;

            Background = Brushes.Black;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;

            foreach (var layer in _layers) _stage.Children.Add(layer.Root);
            Content = _stage;

            if (CommandlineOptions.MaximizeWindow)
                SetFullscreen(true);

            KeyDown += OnKeyDown;
            ContentRendered += OnContentRendered;
            Closing += (_, _) => _closing = true;

            Loaded += async (_, _) =>
            {
                var dpi = VisualTreeHelper.GetDpi(this);
                _maxW = Math.Max(800, (int)(ActualWidth * dpi.DpiScaleX * 1.1));
                _maxH = Math.Max(600, (int)(ActualHeight * dpi.DpiScaleY * 1.1));
                try
                {
                    await RunAsync();
                }
                catch (Exception ex) when (!_closing)
                {
                    MessageBox.Show(this, ex.Message, "Slideshow error");
                    Close();
                }
            };
        }

        // ===== Main loop =====

        private async Task RunAsync()
        {
            Layer current = _layers[0], other = _layers[1];

            var bitmap = await LoadNextAsync(+1);
            if (bitmap == null)
            {
                MessageBox.Show(this, "None of the images could be loaded.", "Photo Slideshow");
                Close();
                return;
            }

            await ShowAsync(current, null, bitmap, +1, TransitionKind.Crossfade); // fade in from black
            PrefetchNext();

            while (!_closing)
            {
                int dir = await WaitForNextAsync();
                if (_closing || dir == 0) 
                    break;

                bitmap = await LoadNextAsync(dir);
                if (bitmap == null) 
                    break;

                await ShowAsync(other, current, bitmap, dir, PickTransition());
                (current, other) = (other, current);
                PrefetchNext();
            }
        }

        /// <summary>
        /// Waits for the display time (pausable) or a user navigation request.
        /// </summary>
        private async Task<int> WaitForNextAsync()
        {
            var clock = Stopwatch.StartNew();
            var last = clock.Elapsed;
            var shown = TimeSpan.Zero;
            
            while (!_closing)
            {
                //Debug.WriteLine($"WaitForNextAsync: while not closing {++count1}");

                if (_pendingNav != 0)
                {
                    int d = _pendingNav;
                    _pendingNav = 0;
                    return d;
                }

                var now = clock.Elapsed;
                if (_paused == 0) 
                    shown += now - last;
                if (_paused == 2)
                {
                    _paused = 0;
                    return +1;
                }
                last = now;
                if (shown.TotalSeconds >= DisplaySeconds) 
                    return +1;

                await Task.Delay(50);
            }
            return 0;
        }

        private string Step(int dir)
        {
            _index += dir;
            if (_index >= _order.Count)
            {
                if (CommandlineOptions.RandomOrder && CommandlineOptions.ReshuffleEachCycle)
                    Reshuffle();
                _index = 0;
            }
            else if (_index < 0)
            {
                _index = _order.Count - 1;
            }
            return _order[_index];
        }

        private void Reshuffle()
        {
            string? last = _order.Count > 0 ? _order[_order.Count - 1] : null;
            Shuffle(_order);

            // never show the same photo twice in a row across the reshuffle
            if (_order.Count > 1 && _order[0] == last)
            {
                int j = _rng.Next(1, _order.Count);
                (_order[0], _order[j]) = (_order[j], _order[0]);
            }
        }

        private static readonly Random ShuffleRng = new Random();

        private static void Shuffle<T>(IList<T> list)
        {
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = ShuffleRng.Next(i + 1);
                (list[i], list[j]) = (list[j], list[i]);
            }
        }

        private async Task<BitmapSource?> LoadNextAsync(int dir)
        {
            for (int attempt = 0; attempt < _order.Count; attempt++) // skip unreadable files
            {
                string path = Step(dir);
                var bitmap = await LoadAsync(path);
                if (bitmap != null) return bitmap;
            }
            return null;
        }

        private Task<BitmapSource?> LoadAsync(string path)
        {
            if (_prefetch is { } p && p.Path == path)
            {
                _prefetch = null;
                return p.Work;
            }
            return Task.Run(() => LoadImage(path));
        }

        private void PrefetchNext()
        {
            int next = _index + 1;
            if (next >= _order.Count) return;
            string path = _order[next];
            if (_prefetch?.Path == path) return;
            _prefetch = (path, Task.Run(() => LoadImage(path)));
        }

        /// <summary>
        /// Decodes at (roughly) screen size, applies EXIF rotation, returns a frozen bitmap.
        /// </summary>
        private BitmapSource? LoadImage(string path)
        {
            try
            {
                int rawW, rawH;
                ushort orientation;
                using (var probe = File.OpenRead(path))
                {
                    var frame = BitmapFrame.Create(probe, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                    rawW = frame.PixelWidth;
                    rawH = frame.PixelHeight;
                    orientation = ReadOrientation(frame);
                }

                bool rotated = orientation >= 5 && orientation <= 8;
                double dispW = rotated ? rawH : rawW;
                double dispH = rotated ? rawW : rawH;
                double scale = Math.Min(1.0, Math.Min(_maxW / dispW, _maxH / dispH));

                var bmp = new BitmapImage();
                using (var stream = File.OpenRead(path))
                {
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    if (scale < 1.0) bmp.DecodePixelWidth = Math.Max(1, (int)Math.Round(rawW * scale));
                    bmp.StreamSource = stream;
                    bmp.EndInit();
                }
                bmp.Freeze();

                var transform = OrientationTransform(orientation);
                if (transform == null) return bmp;

                transform.Freeze();
                var transformed = new TransformedBitmap(bmp, transform);
                var result = new WriteableBitmap(transformed); // forces the work onto this background thread
                result.Freeze();
                return result;
            }
            catch
            {
                return null;
            }
        }

        private static ushort ReadOrientation(BitmapFrame frame)
        {
            try
            {
                if (frame.Metadata is BitmapMetadata md && md.ContainsQuery("System.Photo.Orientation")
                    && md.GetQuery("System.Photo.Orientation") is ushort o)
                    return o;
            }
            catch { /* format without metadata */ }
            return 1;
        }

        private static Transform? OrientationTransform(ushort orientation) => orientation switch
        {
            2 => new ScaleTransform(-1, 1),
            3 => new RotateTransform(180),
            4 => new ScaleTransform(1, -1),
            5 => new TransformGroup { Children = { new ScaleTransform(-1, 1), new RotateTransform(270) } },
            6 => new RotateTransform(90),
            7 => new TransformGroup { Children = { new ScaleTransform(-1, 1), new RotateTransform(90) } },
            8 => new RotateTransform(270),
            _ => null,
        };

        // ===== Showing a photo =====

        private TransitionKind PickTransition()
        {
            var all = (TransitionKind[])Enum.GetValues(typeof(TransitionKind));
            TransitionKind kind;
            do { kind = all[_rng.Next(all.Length)]; } while (kind == _lastTransition && all.Length > 1);
            _lastTransition = kind;
            return kind;
        }

        private async Task ShowAsync(Layer incoming, Layer? outgoing, BitmapSource bitmap, int dir, TransitionKind kind)
        {
            incoming.Reset();
            incoming.Img.Source = bitmap;
            incoming.Root.Visibility = Visibility.Visible;
            Panel.SetZIndex(incoming.Root, 1);
            if (outgoing != null) Panel.SetZIndex(outgoing.Root, 0);

            StartKenBurns(incoming);

            double T = TransitionSeconds;
            double w = Math.Max(1, _stage.ActualWidth);
            double sign = dir >= 0 ? 1 : -1;
            var smooth = new CubicEase { EasingMode = EasingMode.EaseInOut };
            var sine = new SineEase { EasingMode = EasingMode.EaseInOut };
            var easeIn = new QuadraticEase { EasingMode = EasingMode.EaseIn };
            var easeOut = new QuadraticEase { EasingMode = EasingMode.EaseOut };

            switch (kind)
            {
                case TransitionKind.Crossfade:
                    await Animate(incoming.Root, OpacityProperty, 0, 1, T, sine);
                    break;

                case TransitionKind.Push: // new photo pushes the old one out
                    await Task.WhenAll(
                        Animate(incoming.Shift, TranslateTransform.XProperty, sign * w, 0, T, smooth),
                        outgoing is null ? Task.CompletedTask
                            : Animate(outgoing.Shift, TranslateTransform.XProperty, 0, -sign * w, T, smooth));
                    break;

                case TransitionKind.SlideFade: // gentle drift + fade
                    await Task.WhenAll(
                        Animate(incoming.Shift, TranslateTransform.XProperty, sign * w * 0.10, 0, T, smooth),
                        Animate(incoming.Root, OpacityProperty, 0, 1, T, sine),
                        outgoing is null ? Task.CompletedTask
                            : Animate(outgoing.Shift, TranslateTransform.XProperty, 0, -sign * w * 0.06, T, smooth));
                    break;

                case TransitionKind.ZoomThrough: // old grows away, new settles in
                    await Task.WhenAll(
                        AnimateScale(incoming.LayerScale, 0.90, 1.0, T, smooth),
                        Animate(incoming.Root, OpacityProperty, 0, 1, T, sine),
                        outgoing is null ? Task.CompletedTask
                            : AnimateScale(outgoing.LayerScale, 1.0, 1.12, T, smooth));
                    break;

                case TransitionKind.DipToBlack:
                    incoming.Root.Opacity = 0; // stay hidden while the old photo fades out
                    if (outgoing != null)
                        await Animate(outgoing.Root, OpacityProperty, 1, 0, T * 0.45, easeIn);
                    await Animate(incoming.Root, OpacityProperty, 0, 1, T * 0.55, easeOut);
                    break;

                case TransitionKind.SoftWipe: // soft-edged wipe in the direction of travel
                    {
                        var edge = new GradientStop(Color.FromArgb(255, 0, 0, 0), -0.25);
                        var clear = new GradientStop(Color.FromArgb(0, 0, 0, 0), 0.0);
                        incoming.Root.OpacityMask = new LinearGradientBrush
                        {
                            StartPoint = new Point(dir >= 0 ? 0 : 1, 0),
                            EndPoint = new Point(dir >= 0 ? 1 : 0, 0),
                            GradientStops = { edge, clear },
                        };
                        await Task.WhenAll(
                            Animate(edge, GradientStop.OffsetProperty, -0.25, 1.0, T, sine),
                            Animate(clear, GradientStop.OffsetProperty, 0.0, 1.25, T, sine));
                        incoming.Root.OpacityMask = null;
                        break;
                    }
            }

            outgoing?.Hide();
        }

        /// <summary>
        /// Slow zoom in or out for as long as the photo is on screen.
        /// </summary>
        private void StartKenBurns(Layer layer)
        {
            bool zoomIn = _rng.Next(2) == 0;
            double from = zoomIn ? 1.0 : 1.0 + ZoomAmount;
            double to = zoomIn ? 1.0 + ZoomAmount : 1.0;

            // slightly different focal point each time, so it doesn't feel mechanical
            layer.Img.RenderTransformOrigin = new Point(0.35 + _rng.NextDouble() * 0.3, 0.35 + _rng.NextDouble() * 0.3);

            double duration = 2 * TransitionSeconds + DisplaySeconds + 0.5;
            _ = AnimateScale(layer.Zoom, from, to, duration);
        }

        // ===== Animation helpers =====

        /// <summary>
        /// Animates a double property and completes when finished. The final value is set as the base value
        /// first and the animation uses FillBehavior.Stop, so nothing needs cleaning up afterwards.
        /// </summary>
        private static Task Animate(DependencyObject target, DependencyProperty property,
            double from, double to, double seconds, IEasingFunction? ease = null)
        {
            var tcs = new TaskCompletionSource<bool>();
            target.SetValue(property, to);
            var animation = new DoubleAnimation(from, to, TimeSpan.FromSeconds(seconds))
            {
                EasingFunction = ease,
                FillBehavior = FillBehavior.Stop,
            };
            animation.Completed += (_, _) => tcs.TrySetResult(true);
            ((IAnimatable)target).BeginAnimation(property, animation);
            return tcs.Task;
        }

        private static Task AnimateScale(ScaleTransform t, double from, double to, double seconds, IEasingFunction? ease = null)
            => Task.WhenAll(
                Animate(t, ScaleTransform.ScaleXProperty, from, to, seconds, ease),
                Animate(t, ScaleTransform.ScaleYProperty, from, to, seconds, ease));

        // ===== Input / window =====

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            Debug.WriteLine("OnKeyDown: enter");
            string s;

            switch (e.Key)
            {
                case Key.Escape:
                case Key.Q:
                    Close();
                    break;
                case Key.F11:
                case Key.F:
                    Debug.WriteLine("OnKeyDown: F11/F");
                    // toggle Maximize
                    bool doMaximize = WindowStyle != WindowStyle.None;
                    SetFullscreen(doMaximize);
                    if (doMaximize)
                        TimedDialog.ShowMessage(this, "Maximized");
                    else
                        TimedDialog.ShowMessage(this, "Windowed");
                    break;
                case Key.I:
                    ShowInfo();
                    break;
                case Key.Space:
                    if (_paused == 0) _paused = 1;
                    else if (_paused == 1) _paused = 2;
                    if (_paused == 1)
                        TimedDialog.ShowMessage(this, "Paused");
                    else
                        TimedDialog.ShowMessage(this, "Run");
                    break;
                case Key.Right:
                    Debug.WriteLine("OnKeyDown: RIGHT");
                    _pendingNav = 1;
                    TimedDialog.ShowMessage(this, "Next", 100);
                    break;
                case Key.Left:
                    Debug.WriteLine("OnKeyDown: LEFT");
                    _pendingNav = -1;
                    TimedDialog.ShowMessage(this, "Previous", 100);
                    break;
                case Key.Down:
                    Debug.WriteLine("OnKeyDown: DOWN");
                    if (DisplaySeconds >= 2) DisplaySeconds -= 1;
                    s = $"Interval {DisplaySeconds}s";
                    TimedDialog.ShowMessage(this, s);
                    break;
                case Key.Up:
                    Debug.WriteLine("OnKeyDown: UP");
                    if (DisplaySeconds < 1000) DisplaySeconds += 1;
                    s = $"Interval {DisplaySeconds}s";
                    TimedDialog.ShowMessage(this, s);
                    break;
            }
            Debug.WriteLine("OnKeyDown: exit");
        }

        private void OnContentRendered(object sender, EventArgs e)
        {
            ShowInfo();
        }

        private void ShowInfo()
        {
            string incsubStr = CommandlineOptions.IncludeSubfolders ? "Yes" : "No";
            string randomStr = CommandlineOptions.RandomOrder ? "Yes" : "No";
            string reshuffleStr = CommandlineOptions.ReshuffleEachCycle ? "Yes" : "No";
            string maximizeStr = CommandlineOptions.MaximizeWindow ? "Yes" : "No";
            string version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "?";
            string s = $"*** PhotoSlideshow - version {version} ***\n"
                + $"Folder: {CommandlineOptions.Folder}\n"
                + $"Include subfolders: {incsubStr}\n"
                + $"{_order.Count} images\n"
                + $"Random order: {randomStr}\n"
                + $"Reshuffle on each cycle: {reshuffleStr}\n"
                + $"Maximize window: {maximizeStr}\n"
                + $"Interval: {(int)DisplaySeconds}s";

            TimedDialog.ShowMessage(this, s, 8000);
        }
            
        private void SetFullscreen(bool on)
        {
            WindowState = WindowState.Normal;
            if (on)
            {
                WindowStyle = WindowStyle.None;
                ResizeMode = ResizeMode.NoResize;
                Topmost = true;
                //Cursor = Cursors.None;
                WindowState = WindowState.Maximized;
            }
            else
            {
                WindowStyle = WindowStyle.SingleBorderWindow;
                ResizeMode = ResizeMode.CanResize;
                Topmost = false;
                Cursor = Cursors.Arrow;
                Width = 1200;
                Height = 750;
            }
        }
    }
}
