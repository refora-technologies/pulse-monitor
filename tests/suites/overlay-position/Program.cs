// Exercises Pulse's overlay position pipeline.
//
// This mirrors the real classes rather than referencing them: OverlayWindow needs the app,
// the settings service and administrator rights to stand up. So it reproduces the same
// structure — anchor stored as a fraction, live moves that defer the disk write, a debounce
// that commits, a size-driven re-anchor, and a Slider bound two-way — and asserts the
// behaviour we care about.
//
// Every test runs twice: once with the current logic and once in LEGACY mode, which restores
// the pre-fix behaviour (a live move skips recording the anchor, and size changes re-anchor
// even mid-drag). A test that does not fail under LEGACY is not testing anything, so the
// legacy column is the proof the assertions have teeth.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace PosTest
{
    static class Native
    {
        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint f);
        public const uint SWP_NOSIZE = 0x0001, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;
    }

    /// Stand-in for AppSettings + SettingsService.
    class Store
    {
        public double AnchorFx = -1, AnchorFy = -1;
        public string Position = "TopRight";
        public int DiskWrites;
        public void Save() => DiskWrites++;
    }

    class Overlay : Window
    {
        public static bool Legacy;

        /// Mirrors the real fix: the guard lives inside ApplyPosition, not only at the event
        /// source. Turning this off reproduces a guard placed only in SizeChanged.
        public static bool GuardInApplyPosition = true;

        /// Pre-fix this was 8, which held the overlay away from every screen edge.
        public static int EdgeInset = 0;

        /// True while the user is physically dragging the overlay.
        public bool UserDragging;

        bool IsUserPositioning => UserDragging || (Owner?.IsAdjustingPosition ?? false);

        /// Everything that repositions the overlay goes through here, as it does in Pulse.
        public void ApplyPosition()
        {
            if (GuardInApplyPosition && IsUserPositioning) return;
            PlaceWindow();
        }

        /// Stands in for a real DragMove: the anchor is kept current as the window moves, and
        /// nothing is written to disk until the drag ends.
        public void DragTo(int x, int y)
        {
            var work = Work;
            Native.SetWindowPos(Handle, IntPtr.Zero, work.Left + x, work.Top + y, 0, 0,
                Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);

            // Pre-fix, a drag recorded nothing until the button came up.
            if (!Legacy) SavePosition(writeToDisk: false);
        }

        public readonly Store Store = new();
        public Vm Owner;
        readonly TextBlock _text;

        public Overlay()
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            ShowActivated = false;
            ShowInTaskbar = false;
            Background = new SolidColorBrush(Color.FromRgb(0x11, 0x10, 0x20));
            SizeToContent = SizeToContent.WidthAndHeight;
            _text = new TextBlock { Text = "CPU 25 %", Foreground = Brushes.White, Margin = new Thickness(10) };
            Content = _text;
            Left = 300; Top = 300;
            SizeChanged += OnSizeChanged;
        }

        public IntPtr Handle => new WindowInteropHelper(this).Handle;

        System.Drawing.Rectangle Work =>
            System.Windows.Forms.Screen.FromHandle(Handle).WorkingArea;

        /// Forces the auto-size that a changing sensor reading causes in the real overlay.
        public void SimulateReadingWidthChange(string reading) => _text.Text = reading;

        public (int X, int Y, int MaxX, int MaxY) GetPositionPixels()
        {
            if (!Native.GetWindowRect(Handle, out var b)) return (0, 0, 0, 0);
            var work = Work;
            int maxX = Math.Max(0, work.Width - (b.Right - b.Left));
            int maxY = Math.Max(0, work.Height - (b.Bottom - b.Top));
            return (Math.Clamp(b.Left - work.Left, 0, maxX),
                    Math.Clamp(b.Top - work.Top, 0, maxY), maxX, maxY);
        }

        public void SetPositionPixels(int x, int y, bool persist = true)
        {
            if (!Native.GetWindowRect(Handle, out var b)) return;
            var work = Work;
            int maxX = Math.Max(0, work.Width - (b.Right - b.Left));
            int maxY = Math.Max(0, work.Height - (b.Bottom - b.Top));

            Native.SetWindowPos(Handle, IntPtr.Zero,
                work.Left + Math.Clamp(x, 0, maxX),
                work.Top + Math.Clamp(y, 0, maxY),
                0, 0, Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);

            if (Legacy)
            {
                // Pre-fix: a live move recorded nothing at all, so the anchor went stale.
                if (persist) SavePosition(writeToDisk: true);
            }
            else
            {
                SavePosition(writeToDisk: persist);
            }
        }

        public void SavePosition(bool writeToDisk = true)
        {
            if (!Native.GetWindowRect(Handle, out var b)) return;
            var work = Work;
            int width = b.Right - b.Left, height = b.Bottom - b.Top;
            int roomX = work.Width - width, roomY = work.Height - height;

            Store.Position = "Custom";
            Store.AnchorFx = roomX > 0 ? Math.Clamp((b.Left - work.Left) / (double)roomX, 0, 1) : 0;
            Store.AnchorFy = roomY > 0 ? Math.Clamp((b.Top - work.Top) / (double)roomY, 0, 1) : 0;

            if (writeToDisk) Store.Save();
            Owner?.NotifyPositionChanged();
        }

        public void PlaceWindow()
        {
            UpdateLayout();
            if (!Native.GetWindowRect(Handle, out var b)) return;
            int width = b.Right - b.Left, height = b.Bottom - b.Top;
            if (width <= 0 || height <= 0) return;
            if (Store.Position != "Custom" || Store.AnchorFx < 0) return;

            var work = Work;
            int roomX = Math.Max(0, work.Width - width);
            int roomY = Math.Max(0, work.Height - height);
            int x = work.Left + (int)Math.Round(Math.Clamp(Store.AnchorFx, 0, 1) * roomX);
            int y = work.Top + (int)Math.Round(Math.Clamp(Store.AnchorFy, 0, 1) * roomY);

            int inset = Legacy ? 8 : EdgeInset;
            x = Math.Clamp(x, work.Left + inset, Math.Max(work.Left + inset, work.Left + work.Width  - width  - inset));
            y = Math.Clamp(y, work.Top  + inset, Math.Max(work.Top  + inset, work.Top  + work.Height - height - inset));

            Native.SetWindowPos(Handle, IntPtr.Zero, x, y, 0, 0,
                Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
            Owner?.NotifyPositionChanged();
        }

        void OnSizeChanged(object s, SizeChangedEventArgs e)
        {
            if (!IsVisible) return;
            // Pre-fix: re-anchored even while a slider was being dragged.
            if (!Legacy && IsUserPositioning) return;
            Dispatcher.InvokeAsync(ApplyPosition, DispatcherPriority.Loaded);
        }
    }

    class Vm : INotifyPropertyChanged
    {
        public Overlay Overlay;
        public int SetterReentries;

        public int OverlayX { get => Overlay.GetPositionPixels().X; set => MoveAxis(value, null); }
        public int OverlayY { get => Overlay.GetPositionPixels().Y; set => MoveAxis(null, value); }
        public int OverlayMaxX => Overlay.GetPositionPixels().MaxX;
        public int OverlayMaxY => Overlay.GetPositionPixels().MaxY;

        int _depth;
        void MoveAxis(int? x, int? y)
        {
            _depth++;
            if (_depth > 1) SetterReentries++;
            try
            {
                var (curX, curY, _, _) = Overlay.GetPositionPixels();
                int nx = x ?? curX, ny = y ?? curY;
                if (nx == curX && ny == curY) return;

                Overlay.SetPositionPixels(nx, ny, persist: false);
                SchedulePositionSave();
            }
            finally { _depth--; }
        }

        DispatcherTimer _positionSaveTimer;
        public bool IsAdjustingPosition => _positionSaveTimer is { IsEnabled: true };

        void SchedulePositionSave()
        {
            if (_positionSaveTimer == null)
            {
                _positionSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
                _positionSaveTimer.Tick += (_, _) => { _positionSaveTimer.Stop(); CommitOverlayPosition(); };
            }
            _positionSaveTimer.Stop();
            _positionSaveTimer.Start();
        }

        public void CommitOverlayPosition()
        {
            var (x, y, _, _) = Overlay.GetPositionPixels();
            Overlay.SetPositionPixels(x, y);
        }

        public void FlushPendingSave()
        {
            // Pre-fix this only knew about the opacity timer, so a position nudged moments
            // before closing was never written.
            if (Overlay.Legacy) return;

            if (_positionSaveTimer is { IsEnabled: true })
            {
                _positionSaveTimer.Stop();
                CommitOverlayPosition();
            }
        }

        public void NotifyPositionChanged()
        {
            Raise(nameof(OverlayX)); Raise(nameof(OverlayY));
            Raise(nameof(OverlayMaxX)); Raise(nameof(OverlayMaxY));
        }
        void Raise(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
        public event PropertyChangedEventHandler PropertyChanged;
    }

    public static class Program
    {
        static string _log;
        static void W(string s = "") { File.AppendAllText(_log, s + "\r\n"); }

        static readonly List<(string Name, bool Now, bool Legacy)> Results = new();

        static Overlay _ov;
        static Vm _vm;
        static Slider _slider;
        static Window _panel;

        static async Task Settle(int ms = 120) => await Task.Delay(ms);

        static void Build()
        {
            _ov = new Overlay();
            _vm = new Vm { Overlay = _ov };
            _ov.Owner = _vm;
            _ov.Show();

            _slider = new Slider { Minimum = 0, Width = 300 };
            BindingOperations.SetBinding(_slider, RangeBase_Max(), new Binding("OverlayMaxX"));
            BindingOperations.SetBinding(_slider, Slider.ValueProperty,
                new Binding("OverlayX") { Mode = BindingMode.TwoWay });

            _panel = new Window { Width = 360, Height = 120, Left = 20, Top = 20, DataContext = _vm, Content = _slider };
            _panel.Show();
        }

        static DependencyProperty RangeBase_Max() => System.Windows.Controls.Primitives.RangeBase.MaximumProperty;

        static void Teardown() { _panel?.Close(); _ov?.Close(); }

        // ── the tests ───────────────────────────────────────────────────────────────

        /// The reported glitch: drag, then a reading changes width, and the overlay is yanked
        /// back to where it sat before the drag.
        static async Task<bool> TestNoSnapBackDuringDrag()
        {
            _vm.CommitOverlayPosition();
            _ov.SetPositionPixels(200, 200);            // committed starting point
            await Settle();

            _vm.OverlayX = 600;                         // a live drag, nothing written yet
            await Settle();

            _ov.SimulateReadingWidthChange("CPU 100 % / 12345 MHz");   // forces the auto-size
            await Settle(400);

            int x = _ov.GetPositionPixels().X;
            bool ok = Math.Abs(x - 600) <= 8;           // a couple of px of anchor rounding is fine
            W($"     ended at X={x}, wanted ~600");
            return ok;
        }

        /// A drag must not write the settings file per pixel, but must write exactly once
        /// after it settles.
        static async Task<bool> TestDiskWrites()
        {
            _ov.SetPositionPixels(300, 300);
            await Settle(500);
            _ov.Store.DiskWrites = 0;

            for (int i = 0; i < 60; i++) { _vm.OverlayX = 300 + i * 5; await Task.Delay(4); }
            int during = _ov.Store.DiskWrites;

            await Settle(700);                          // let the debounce fire
            int after = _ov.Store.DiskWrites;

            W($"     writes during drag={during} (want 0), after settle={after} (want 1)");
            return during == 0 && after == 1;
        }

        /// Closing right after a drag must not lose the position.
        static async Task<bool> TestFlushSavesPosition()
        {
            _ov.SetPositionPixels(250, 250);
            await Settle(500);
            _ov.Store.DiskWrites = 0;

            _vm.OverlayX = 700;
            await Settle(30);                            // still inside the debounce window
            _vm.FlushPendingSave();
            await Settle();

            int writes = _ov.Store.DiskWrites;
            int x = _ov.GetPositionPixels().X;
            W($"     writes={writes} (want >=1), X={x} (want ~700)");
            return writes >= 1 && Math.Abs(x - 700) <= 8;
        }

        /// The two-way binding must settle rather than ping-pong between slider and overlay.
        static async Task<bool> TestNoBindingPingPong()
        {
            _ov.SetPositionPixels(400, 400);
            await Settle(500);
            _vm.SetterReentries = 0;

            for (int i = 0; i < 25; i++) { _slider.Value = 400 + i * 10; await Task.Delay(6); }
            await Settle(700);

            W($"     re-entrant setter calls={_vm.SetterReentries} (want 0)");
            return _vm.SetterReentries == 0;
        }

        /// Re-anchoring resumes once the drag is over, so normal behaviour is not lost.
        static async Task<bool> TestReanchorResumesAfterDrag()
        {
            _ov.SetPositionPixels(350, 350);
            await Settle(500);

            _vm.OverlayX = 500;
            bool duringDrag = _vm.IsAdjustingPosition;

            await Settle(900);                           // debounce fires, adjustment ends
            bool afterDrag = _vm.IsAdjustingPosition;

            W($"     adjusting during drag={duringDrag} (want True), after={afterDrag} (want False)");
            return duringDrag && !afterDrag;
        }

        /// Asking for a position past the edge must clamp, and the slider must show the
        /// clamped value rather than the one that was asked for.
        static async Task<bool> TestClampsToScreen()
        {
            _ov.SetPositionPixels(300, 300);
            await Settle(500);

            int max = _vm.OverlayMaxX;
            _vm.OverlayX = max + 5000;
            await Settle(600);

            int x = _ov.GetPositionPixels().X;
            bool ok = x <= max && x >= max - 8;
            W($"     asked for {max + 5000}, landed at {x}, max is {max}");
            return ok;
        }

        /// The stored anchor must bring the overlay back to the same place next launch.
        static async Task<bool> TestAnchorSurvivesRestart()
        {
            _ov.SetPositionPixels(640, 360);
            _vm.CommitOverlayPosition();
            await Settle(500);

            var (wantX, wantY, _, _) = _ov.GetPositionPixels();
            double fx = _ov.Store.AnchorFx, fy = _ov.Store.AnchorFy;

            // Stand up a fresh overlay carrying the saved anchor, as a relaunch would.
            var fresh = new Overlay();
            var freshVm = new Vm { Overlay = fresh };
            fresh.Owner = freshVm;
            fresh.Store.Position = "Custom";
            fresh.Store.AnchorFx = fx;
            fresh.Store.AnchorFy = fy;
            fresh.Show();
            await Settle(300);
            fresh.PlaceWindow();
            await Settle(200);

            var (gotX, gotY, _, _) = fresh.GetPositionPixels();
            fresh.Close();

            bool ok = Math.Abs(gotX - wantX) <= 8 && Math.Abs(gotY - wantY) <= 8;
            W($"     saved ({wantX},{wantY}) -> restored ({gotX},{gotY})");
            return ok;
        }

        /// The reporter's bug: free-drag the overlay and a reading changes width mid-drag.
        static async Task<bool> TestFreeDragNoSnapBack()
        {
            _ov.SetPositionPixels(200, 200);
            _vm.CommitOverlayPosition();
            await Settle(400);

            _ov.UserDragging = true;
            _ov.DragTo(650, 500);
            await Settle(80);

            // A sensor reading changes width while the button is still down.
            _ov.SimulateReadingWidthChange("CPU 100 % / 12345 MHz");
            await Settle(350);

            _ov.UserDragging = false;
            _ov.SavePosition();       // the one write, on release
            _ov.ApplyPosition();
            await Settle(200);

            var (x, y, _, _) = _ov.GetPositionPixels();
            W($"     dropped at (650,500), ended at ({x},{y})");
            return Math.Abs(x - 650) <= 10 && Math.Abs(y - 500) <= 10;
        }

        /// DragMove runs a nested message loop that pumps this dispatcher, so an ApplyPosition
        /// queued just before the drag is delivered *during* it. A guard only at the event
        /// source would not catch this one.
        static async Task<bool> TestQueuedRepositionDuringDrag()
        {
            _ov.SetPositionPixels(200, 200);
            _vm.CommitOverlayPosition();
            await Settle(400);

            // Queued before the drag begins, exactly as a size change a moment earlier would.
            _ov.Dispatcher.InvokeAsync(_ov.ApplyPosition, DispatcherPriority.Loaded);

            _ov.UserDragging = true;
            _ov.DragTo(700, 520);

            await Settle(300);        // the queued callback is delivered in here

            int during = _ov.GetPositionPixels().X;

            _ov.UserDragging = false;
            _ov.SavePosition();
            await Settle(150);

            W($"     during drag X={during}, wanted ~700");
            return Math.Abs(during - 700) <= 10;
        }

        /// Dragging into the corner must leave the overlay actually in the corner. An inset in
        /// the placement clamp used to push it back out, so it could never sit flush against
        /// the tray edge - the placement people most often want.
        static async Task<bool> TestCanSitFlushInCorner()
        {
            _ov.SetPositionPixels(300, 300);
            await Settle(300);

            var (_, _, maxX, maxY) = _ov.GetPositionPixels();

            // Drag it hard into the bottom-right and let go, as the user did.
            _ov.UserDragging = true;
            _ov.DragTo(maxX, maxY);
            await Settle(80);
            _ov.UserDragging = false;
            _ov.SavePosition();
            _ov.ApplyPosition();
            await Settle(250);

            var (x, y, nowMaxX, nowMaxY) = _ov.GetPositionPixels();

            // Flush means sitting at the far edge, whatever the panel's current size is.
            bool flush = x >= nowMaxX - 1 && y >= nowMaxY - 1;
            W($"     dropped in the corner ({maxX},{maxY}); ended at ({x},{y}) of ({nowMaxX},{nowMaxY})");
            return flush;
        }

        static async Task<bool> Run(string name, Func<Task<bool>> test)
        {
            W($"  {name}");
            bool ok;
            try { ok = await test(); }
            catch (Exception ex) { W("     threw: " + ex.Message); ok = false; }
            W($"     -> {(ok ? "PASS" : "FAIL")}");
            W();
            return ok;
        }

        static async Task RunAll(Application app)
        {
            // ShouldFailUnderLegacy marks the tests that exist because of this fix. The rest
            // are invariant guards that were already true and must stay true.
            var tests = new (string Name, Func<Task<bool>> Fn, bool ShouldFailUnderLegacy)[]
            {
                ("no snap-back while dragging",            TestNoSnapBackDuringDrag,    true),
                ("flush on close keeps the position",      TestFlushSavesPosition,      true),
                ("no settings write per pixel",            TestDiskWrites,              false),
                ("slider binding does not ping-pong",      TestNoBindingPingPong,       false),
                ("re-anchoring resumes after the drag",    TestReanchorResumesAfterDrag,false),
                ("position clamps to the screen",          TestClampsToScreen,          false),
                ("anchor survives a restart",              TestAnchorSurvivesRestart,   false),
                ("free drag does not snap back",           TestFreeDragNoSnapBack,      true),
                ("queued reposition ignored mid-drag",     TestQueuedRepositionDuringDrag, true),
                ("can sit flush in the corner",            TestCanSitFlushInCorner,     true),
            };

            foreach (var legacy in new[] { false, true })
            {
                Overlay.Legacy = legacy;
                // Legacy also puts the guard only at the event source, which is the mistake
                // the queued-reposition test exists to catch.
                Overlay.GuardInApplyPosition = !legacy;
                W(new string('=', 68));
                W(legacy ? "LEGACY MODE (pre-fix behaviour - these SHOULD fail)"
                         : "CURRENT CODE (these should all pass)");
                W(new string('=', 68));
                W();

                foreach (var (name, fn, _) in tests)
                {
                    Build();
                    await Settle(300);
                    bool ok = await Run(name, fn);
                    Teardown();
                    await Settle(80);

                    int i = Results.FindIndex(r => r.Name == name);
                    if (i < 0) Results.Add((name, ok, false));
                    else Results[i] = (name, Results[i].Now, ok);
                }
            }

            W(new string('=', 68));
            W("SUMMARY");
            W(new string('=', 68));
            W($"{"test",-42}{"now",-8}{"legacy",-8}verdict");
            bool allGood = true;
            foreach (var (name, now, legacy) in Results)
            {
                bool shouldFail = Array.Find(tests, t => t.Name == name).ShouldFailUnderLegacy;
                string verdict;
                if (!now)                      { verdict = "BROKEN"; allGood = false; }
                else if (shouldFail && legacy) { verdict = "NO TEETH (passes pre-fix too)"; allGood = false; }
                else if (shouldFail)           verdict = "regression test, has teeth";
                else                           verdict = "invariant guard";
                W($"{name,-42}{(now ? "PASS" : "FAIL"),-8}{(legacy ? "PASS" : "FAIL"),-8}{verdict}");
            }
            W();
            W(allGood
                ? "All tests pass now and all of them fail against the pre-fix code."
                : "Look at the rows above that are not 'good'.");

            app.Shutdown();
        }

        [STAThread]
        public static void Main()
        {
            _log = Path.Combine(Path.GetDirectoryName(
                System.Reflection.Assembly.GetExecutingAssembly().Location), "position-tests.txt");
            File.WriteAllText(_log, "Pulse overlay position pipeline tests\r\n\r\n");

            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.Startup += async (_, _) => await RunAll(app);
            app.Run();
        }
    }
}
