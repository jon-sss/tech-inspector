using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using MahApps.Metro.IconPacks;
using SimHub.Plugins.Styles;
using TechInspector.Core.Triples;
using TechInspector.Core.Settings;

namespace TechInspector.Plugin.UI
{
    /// <summary>
    /// The live triple-screen editor: a to-scale top view with draggable handles, the window over
    /// the screens Windows reports, the [MonitorSetup] and window controls, and the saved layout.
    /// Every edit is an ordinary pending change, written by the changes bar.
    /// </summary>
    public partial class TriplesTab : UserControl
    {
        private const string Mon = LayoutGuard.MonitorFile;
        private static readonly (string Title, string[] Keys)[] Cards =
        {
            ("Screens", new[] { "MonitorSetup|NumMonitors", "MonitorSetup|MonitorType", "MonitorSetup|RadiusOfCurvature", "MonitorSetup|ScreenWidth", "MonitorSetup|MonitorWidth" }),
            ("Position", new[] { "MonitorSetup|ViewingDist", "MonitorSetup|ScreenAngles" }),
            ("Rendering", new[] { "MonitorSetup|EnableSMPSurround", "MonitorSetup|RenderViewPerMonitor", "MonitorSetup|BezelProtectionPct" }),
            ("Window", new[] { "Display|windowedXPos", "Display|windowedYPos", "Display|windowedWidth", "Display|windowedHeight", "Display|border" }),
        };
        /// <summary>Sliders for the values people nudge: min, max, step.</summary>
        private static readonly Dictionary<string, (double Min, double Max, double Step)> Slide = new Dictionary<string, (double, double, double)>
        {
            ["ViewingDist"] = (300, 2000, 5), ["ScreenAngles"] = (10, 65, 1), ["RadiusOfCurvature"] = (800, 4000, 50), ["BezelProtectionPct"] = (0, 10, 1),
        };
        private static readonly Dictionary<string, (double W, double H)> Aspects = new Dictionary<string, (double, double)>
        {
            ["16:9"] = (16, 9), ["16:10"] = (16, 10), ["21:9"] = (43, 18), ["32:9"] = (32, 9),
        };

        private Workspace _ws;
        private List<System.Windows.Forms.Screen> _screens = System.Windows.Forms.Screen.AllScreens.OrderBy(s => s.Bounds.X).ToList();
        private TextBlock _angleHint;
        private Button _angleUse;
        private StackPanel _calcPanel;
        private TextBlock _calcResult;
        private TextBox _calcDiag, _calcBezel;
        private ComboBox _calcAspect;

        // top view mapping (mm → canvas) and dragging
        private double _k, _ox, _oy;
        private string _drag;
        private double _dragScreenY;
        private bool _dirty = true;

        public TriplesTab()
        {
            InitializeComponent();
            IsVisibleChanged += (s, e) => { if (IsVisible && _dirty) Redraw(); };
            TopView.MouseMove += Top_MouseMove;
            TopView.MouseLeftButtonUp += (s, e) => EndDrag();
            TopView.LostMouseCapture += (s, e) => EndDrag();
        }

        public void Attach(Workspace ws)
        {
            foreach (var canvas in new[] { TopView, FrontView })
                if (canvas.Parent is Viewbox vb)
                    vb.SizeChanged += (o, e) => { if (Math.Abs(e.NewSize.Width - e.PreviousSize.Width) > 24) { RenderTop(); RenderFront(); } };
            _ws = ws;
            _ws.Reloaded += (s, e) => { BuildControls(); Invalidate(); };
            _ws.DraftsChanged += (s, e) => Invalidate();
            _ws.LayoutChanged += (s, e) => Invalidate();
            BuildControls();
            Invalidate();
        }

        private SettingsSession Session => _ws?.Page?.Session;
        private bool IsMonitorFile => Session != null && string.Equals(Session.RendererFile, Mon, StringComparison.OrdinalIgnoreCase);
        private Setting S(string key) { var p = key.Split('|'); return Session?.Find(Mon, p[0], p[1]); }
        private Setting M(string key) => Session?.Find(Mon, "MonitorSetup", key);
        private Setting W(string key) => Session?.Find(Mon, "Display", key);
        private static double Num(string v) => double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : 0;

        private void Invalidate()
        {
            _dirty = true;
            if (IsVisible) Redraw();
        }

        private void Redraw()
        {
            _dirty = false;
            RenderBanner();
            if (!IsMonitorFile) { TopView.Children.Clear(); FrontView.Children.Clear(); Readouts.Children.Clear(); return; }
            if (_drag == null) RenderTop();
            RenderFront();
            UpdateHints();
            RenderGuard();
        }

        // ── banner ──

        private void RenderBanner()
        {
            BannerActions.Children.Clear();
            var lines = new List<StatusLine>();
            BannerIcon.Kind = PackIconMaterialKind.Information;
            if (Session == null) { BannerTitle.Text = "No iRacing folder found under Documents."; BannerLines.ItemsSource = null; return; }
            if (!IsMonitorFile)
            {
                BannerTitle.Text = "Triple screens edit rendererDX11Monitor.ini.";
                lines.Add(StatusLine.Warn($"The Settings tab is showing {Session.RendererFile}."));
                AddBannerButton("Switch to Monitor", (s, e) => _ws.SwitchRenderer(Mon));
                BannerLines.ItemsSource = lines;
                return;
            }

            var saved = _ws.Plugin.Settings.SavedLayout;
            var diffs = LayoutGuard.Differences(Session, saved);
            if (diffs.Count > 0)
            {
                BannerIcon.Kind = PackIconMaterialKind.Alert;
                BannerTitle.Text = "iRacing changed your triple layout.";
                lines.Add(StatusLine.Warn($"{SettingsPage.Plural(diffs.Count, "setting differs", "settings differ")} from your saved layout." +
                                          (_ws.Plugin.Settings.GuardAutoRestore ? " It's put back automatically after iRacing closes." : "")));
                AddBannerButton("Restore now", Restore_Click);
            }
            else
            {
                BannerTitle.Text = "Edits preview live and are written with your other changes.";
                if (saved.Count == 0)
                {
                    lines.Add(StatusLine.Warn("No saved layout yet. Save it so Tech Inspector can put it back if iRacing changes it."));
                    AddBannerButton("Save layout", SaveLayout_Click);
                }
                else lines.Add(StatusLine.Ok(_ws.Plugin.Settings.GuardAutoRestore
                    ? "Saved layout matches the file. It's put back if iRacing changes it."
                    : "Saved layout matches the file. You're told if iRacing changes it."));
            }

            // Fewer screens than the layout needs is explained in the "Window on your desktop" card, not repeated here.
            int wanted = (int)Num(M("NumMonitors")?.Draft);
            if (wanted == 3 && _screens.Count >= 3)
            {
                var span = Span();
                bool covers = Num(W("windowedXPos")?.Draft) == span.X && Num(W("windowedWidth")?.Draft) == span.Width &&
                              Num(W("windowedHeight")?.Draft) == span.Height && Num(W("windowedYPos")?.Draft) == span.Y;
                if (covers) lines.Add(StatusLine.Ok($"The window covers all {_screens.Count} screens."));
                else
                {
                    lines.Add(StatusLine.Warn($"The window covers {W("windowedWidth")?.Draft} of the {span.Width} px your screens span."));
                    AddBannerButton("Fit window to screens", Fit_Click);
                }
            }
            if (lines.Any(l => l.Icon == PackIconMaterialKind.Alert)) BannerIcon.Kind = PackIconMaterialKind.Alert;
            BannerLines.ItemsSource = lines;
        }

        private void AddBannerButton(string text, RoutedEventHandler click)
        {
            var b = new SHButtonSecondary { Content = text, Margin = new Thickness(8, 0, 0, 0) };
            b.Click += click;
            BannerActions.Children.Add(b);
        }

        private System.Drawing.Rectangle Span()
        {
            int x = _screens.Min(s => s.Bounds.Left), y = _screens.Min(s => s.Bounds.Top);
            return new System.Drawing.Rectangle(x, y, _screens.Max(s => s.Bounds.Right) - x, _screens.Max(s => s.Bounds.Bottom) - y);
        }

        // ── top view ──

        private TripleGeometry Geo(bool pending) => TripleGeometry.From(LayoutGuard.Inputs(Session, pending));

        private void Fit(IEnumerable<Pt> pts, double wv, double hv, double pad)
        {
            var list = pts.ToList();
            double x0 = list.Min(p => p.X), x1 = list.Max(p => p.X), y0 = list.Min(p => p.Y), y1 = list.Max(p => p.Y);
            _k = Math.Min((wv - 2 * pad) / Math.Max(1, x1 - x0), (hv - 2 * pad - 10) / Math.Max(1, y1 - y0));
            _ox = (wv - (x1 - x0) * _k) / 2 - x0 * _k;
            _oy = (hv - (y1 - y0) * _k) / 2 + y1 * _k - 4;
        }

        /// <summary>The larger side of the points' bounding box, in mm.</summary>
        private static double Extent(List<Pt> pts) =>
            Math.Max(pts.Max(p => p.X) - pts.Min(p => p.X), pts.Max(p => p.Y) - pts.Min(p => p.Y));

        private Point To(Pt p) => new Point(_ox + p.X * _k, _oy - p.Y * _k);
        private Pt From(Point p) => new Pt((p.X - _ox) / _k, (_oy - p.Y) / _k);

        /// <summary>A straight or curved panel. Curved panels bulge away from the eyes.</summary>
        private Geometry Seg(Pt p, Pt q, double radius)
        {
            var P = To(p); var Q = To(q);
            var fig = new PathFigure { StartPoint = P };
            if (radius <= 0) fig.Segments.Add(new LineSegment(Q, true));
            else
            {
                double len = Math.Sqrt((q.X - p.X) * (q.X - p.X) + (q.Y - p.Y) * (q.Y - p.Y)), half = len / 2;
                double sag = half < radius ? radius - Math.Sqrt(radius * radius - half * half) : half;
                var mid = new Pt((p.X + q.X) / 2, (p.Y + q.Y) / 2);
                double nx = -(q.Y - p.Y) / len, ny = (q.X - p.X) / len;
                if (nx * mid.X + ny * mid.Y < 0) { nx = -nx; ny = -ny; }
                fig.Segments.Add(new QuadraticBezierSegment(To(new Pt(mid.X + nx * 2 * sag, mid.Y + ny * 2 * sag)), Q, true));
            }
            var g = new PathGeometry(); g.Figures.Add(fig); return g;
        }

        private Brush Accent => (TryFindResource("AccentColorBrush") as Brush) ?? Brushes.MediumSlateBlue;
        private static readonly Brush BodyBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)));
        private static readonly Brush ScreenBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xC8, 0xC8, 0xC8)));
        private static readonly Brush GhostBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x99, 0x90, 0x90, 0x90)));
        private static readonly Brush LineBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x66, 0x90, 0x90, 0x90)));
        private static Brush Freeze(Brush b) { b.Freeze(); return b; }

        /// <summary>
        /// How much to enlarge labels on a drawing its Viewbox has shrunk, so they stay readable
        /// in a narrow pane (up to 1.6 times; never smaller than drawn).
        /// </summary>
        private static double LabelBoost(Canvas c)
        {
            if (!(c.Parent is Viewbox vb) || vb.ActualWidth <= 0 || c.Width <= 0) return 1;
            double scale = vb.ActualWidth / c.Width;
            return scale >= 1 ? 1 : Math.Min(1.6, 1 / scale);
        }

        private void RenderTop()
        {
            var c = TopView;
            c.Children.Clear();
            Readouts.Children.Clear();
            var g = Geo(true);
            var g0 = Geo(false);
            bool moved = new[] { "NumMonitors", "MonitorType", "RadiusOfCurvature", "ScreenWidth", "MonitorWidth", "ViewingDist", "ScreenAngles" }.Any(k => M(k)?.IsChanged == true);

            if (_drag == null)
            {
                // Fit the new layout. The file's layout joins the fit only when it's of a similar size;
                // after a big correction it would shrink the new one to a speck, so it runs off the edge instead.
                var pts = new List<Pt> { new Pt(0, 0) };
                foreach (var p in g.Panels) { pts.Add(p.A); pts.Add(p.B); }
                var ghost = g0.Panels.SelectMany(p => new[] { p.A, p.B }).ToList();
                if (moved && Extent(ghost.Concat(new[] { new Pt(0, 0) }).ToList()) <= Extent(pts) * 1.5) pts.AddRange(ghost);
                Fit(pts, 640, 330, 40);
            }
            double R = g.In.Curved ? g.In.RadiusOfCurvature : 0, R0 = g0.In.Curved ? g0.In.RadiusOfCurvature : 0;
            var eye = To(new Pt(0, 0));

            if (moved)
                foreach (var p in g0.Panels) c.Children.Add(new Path { Data = Seg(p.A, p.B, R0), Stroke = GhostBrush, StrokeThickness = 2, StrokeDashArray = new DoubleCollection { 3, 2 } });

            var outerL = new Pt(-g.Outer.X, g.Outer.Y);
            foreach (var pt in new[] { outerL, g.Outer })
            {
                var P = To(pt);
                c.Children.Add(new Line { X1 = eye.X, Y1 = eye.Y, X2 = P.X, Y2 = P.Y, Stroke = LineBrush, StrokeThickness = 1 });
            }
            // the field-of-view arc around the eyes
            double r = 42, a1 = Math.Atan2(outerL.X, outerL.Y), a2 = Math.Atan2(g.Outer.X, g.Outer.Y);
            var arc = new PathFigure { StartPoint = new Point(eye.X + r * Math.Sin(a1), eye.Y - r * Math.Cos(a1)) };
            arc.Segments.Add(new ArcSegment(new Point(eye.X + r * Math.Sin(a2), eye.Y - r * Math.Cos(a2)), new Size(r, r), 0, a2 - a1 > Math.PI, SweepDirection.Clockwise, true));
            var ag = new PathGeometry(); ag.Figures.Add(arc);
            c.Children.Add(new Path { Data = ag, Stroke = Accent, StrokeThickness = 2 });

            foreach (var p in g.Panels)
            {
                c.Children.Add(new Path { Data = Seg(p.A, p.B, R), Stroke = BodyBrush, StrokeThickness = 7, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });
                c.Children.Add(new Path { Data = Seg(p.ScreenA, p.ScreenB, R), Stroke = ScreenBrush, StrokeThickness = 4, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });
            }
            var centre = To(new Pt(0, g.In.ViewingDist));
            c.Children.Add(new Line { X1 = eye.X, Y1 = eye.Y, X2 = centre.X, Y2 = centre.Y, Stroke = LineBrush, StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 3, 3 } });

            // Under the eye dot, the labels step down as they're enlarged; the figure inside the arc grows least.
            double b = LabelBoost(c);
            Label(c, "you", eye.X, eye.Y + 12 + 6 * b, 12, 0.7);
            Label(c, $"{Math.Round(g.In.ViewingDist)} mm to centre", eye.X, eye.Y + 14 + 20 * Math.Min(b, 1.3), 12, 0.7, maxBoost: 1.3);
            Label(c, $"{Math.Round(g.Fov)}°", eye.X, eye.Y - 28, 13, 1, Accent, maxBoost: 1.15);
            if (g.In.NumMonitors == 3)
            {
                var hinge = To(g.Right.A);
                double dx = hinge.X - eye.X, dy = hinge.Y - eye.Y, dl = Math.Max(1, Math.Sqrt(dx * dx + dy * dy));
                Label(c, $"{Math.Round(g.In.ScreenAngles)}°", hinge.X + dx / dl * 20, hinge.Y + dy / dl * 20 - 8, 12, 0.8, maxBoost: 1.3);
            }

            Handle(c, eye, "eye", "Eyes to centre screen. Drag, or use the arrow keys.");
            if (g.In.NumMonitors == 3) Handle(c, To(g.Right.B), "angle", "Side screen angle. Drag, or use the arrow keys.");

            // readouts
            Readout("Field of view", Math.Round(g.Fov), Math.Round(g0.Fov), g.Fov > 180 ? "Over 180°. Check screen width and distance." : null);
            Readout("Centre screen", Math.Round(g.CentreFov), Math.Round(g0.CentreFov), null);
            if (g.In.NumMonitors == 3)
                Readout("Ideal side angle", g.Ideal, g0.Ideal, null, g.IdealIsClamped ? " (max)" : "");
        }

        /// <summary>A centred label. It grows with LabelBoost up to maxBoost; 1 keeps it at its drawn size.</summary>
        private static void Label(Canvas c, string text, double x, double y, double size, double opacity, Brush fg = null, double maxBoost = 1.6)
        {
            var t = new TextBlock { Text = text, FontSize = size * Math.Min(maxBoost, LabelBoost(c)), Opacity = opacity };
            if (fg != null) t.Foreground = fg;
            t.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(t, x - t.DesiredSize.Width / 2);
            // Never past the drawing's edges, which clip.
            Canvas.SetTop(t, Math.Max(0, Math.Min(y - t.DesiredSize.Height / 2, c.Height - t.DesiredSize.Height)));
            c.Children.Add(t);
        }

        private void Handle(Canvas c, Point at, string name, string tip)
        {
            var e = new Ellipse
            {
                Width = 16, Height = 16, Fill = Accent, Stroke = Brushes.White, StrokeThickness = 2, Cursor = Cursors.Hand,
                Focusable = true, Tag = name, ToolTip = tip,
            };
            AutomationProperties_SetName(e, tip);
            Canvas.SetLeft(e, at.X - 8); Canvas.SetTop(e, at.Y - 8);
            e.MouseLeftButtonDown += (s, ev) =>
            {
                _drag = name;
                _dragScreenY = _oy - Num(M("ViewingDist")?.Draft) * _k;   // the centre screen stays put while the eyes move
                e.Focus();
                TopView.CaptureMouse();
                ev.Handled = true;
            };
            e.KeyDown += Handle_KeyDown;
            e.GotKeyboardFocus += (s, ev) => e.StrokeThickness = 3.5;
            e.LostKeyboardFocus += (s, ev) => e.StrokeThickness = 2;
            c.Children.Add(e);
        }

        private static void AutomationProperties_SetName(DependencyObject o, string name) => System.Windows.Automation.AutomationProperties.SetName(o, name);

        private void Handle_KeyDown(object sender, KeyEventArgs e)
        {
            var name = (string)((FrameworkElement)sender).Tag;
            int dir = e.Key == Key.Right || e.Key == Key.Up ? 1 : e.Key == Key.Left || e.Key == Key.Down ? -1 : 0;
            if (dir == 0) return;
            if (name == "eye") SetNum("MonitorSetup|ViewingDist", Math.Max(300, Math.Min(2000, Num(M("ViewingDist").Draft) - dir * 10)));
            else SetNum("MonitorSetup|ScreenAngles", Math.Max(10, Math.Min(65, Num(M("ScreenAngles").Draft) + dir)));
            e.Handled = true;
            Dispatcher.BeginInvoke(new Action(() => TopView.Children.OfType<Ellipse>().FirstOrDefault(x => (string)x.Tag == name)?.Focus()), System.Windows.Threading.DispatcherPriority.Input);
        }

        private void Top_MouseMove(object sender, MouseEventArgs e)
        {
            if (_drag == null || e.LeftButton != MouseButtonState.Pressed) return;
            var pos = e.GetPosition(TopView);
            if (_drag == "eye")
            {
                double d = Math.Max(300, Math.Min(2000, Math.Round((pos.Y - _dragScreenY) / _k / 5) * 5));
                _oy = _dragScreenY + d * _k;
                SetNum("MonitorSetup|ViewingDist", d);
            }
            else SetNum("MonitorSetup|ScreenAngles", Geo(true).AngleTowards(From(pos)));
            RenderTop();
        }

        private void EndDrag()
        {
            if (_drag == null) return;
            _drag = null;
            if (TopView.IsMouseCaptured) TopView.ReleaseMouseCapture();
            Redraw();
        }

        private void Readout(string label, double v, double v0, string warn, string suffix = "")
        {
            var p = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
            p.Children.Add(new TextBlock { Text = label, FontSize = 12.5, Opacity = 0.62 });
            var t = new TextBlock { FontSize = 20 };
            t.Inlines.Add(new System.Windows.Documents.Run($"{v}°{suffix}"));
            if (Math.Abs(v - v0) >= 1) t.Inlines.Add(new System.Windows.Documents.Run($"  was {v0}°") { FontSize = 12.5, Foreground = Brushes.Gray });
            p.Children.Add(t);
            if (warn != null) p.Children.Add(new TextBlock { Text = warn, FontSize = 12, Foreground = HelpLine.Warn, TextWrapping = TextWrapping.Wrap });
            Readouts.Children.Add(p);
        }

        // ── front view ──

        private void RenderFront()
        {
            var c = FrontView;
            c.Children.Clear();
            var win = new Rect(Num(W("windowedXPos")?.Draft), Num(W("windowedYPos")?.Draft), Math.Max(1, Num(W("windowedWidth")?.Draft)), Math.Max(1, Num(W("windowedHeight")?.Draft)));
            var win0 = new Rect(Num(W("windowedXPos")?.Applied), Num(W("windowedYPos")?.Applied), Math.Max(1, Num(W("windowedWidth")?.Applied)), Math.Max(1, Num(W("windowedHeight")?.Applied)));
            var rects = _screens.Select(s => new Rect(s.Bounds.X, s.Bounds.Y, s.Bounds.Width, s.Bounds.Height)).ToList();
            var all = rects.Concat(new[] { win, win0 }).ToList();
            double x0 = all.Min(r => r.Left), x1 = all.Max(r => r.Right), y0 = all.Min(r => r.Top), y1 = all.Max(r => r.Bottom);
            double wv = 640, hv = 150, pad = 18, k = Math.Min((wv - 2 * pad) / (x1 - x0), (hv - 2 * pad - 16) / (y1 - y0));
            double ox = (wv - (x1 - x0) * k) / 2 - x0 * k, oy = pad + 16 - y0 * k;
            Rect T(Rect r) => new Rect(ox + r.X * k, oy + r.Y * k, r.Width * k, r.Height * k);

            for (int i = 0; i < _screens.Count; i++)
            {
                var q = T(rects[i]);
                var box = new Rectangle { Width = q.Width, Height = q.Height, Fill = new SolidColorBrush(Color.FromArgb(0x22, 0x80, 0x80, 0x80)), Stroke = LineBrush };
                Canvas.SetLeft(box, q.X); Canvas.SetTop(box, q.Y); c.Children.Add(box);
                var s = _screens[i];
                Label(c, s.Primary ? $"Screen {i + 1} (main)" : $"Screen {i + 1}", q.X + q.Width / 2, q.Y + q.Height / 2 - 8, 11.5, 0.9, maxBoost: 1);
                Label(c, $"{s.Bounds.Width} × {s.Bounds.Height}", q.X + q.Width / 2, q.Y + q.Height / 2 + 8, 11, 0.6, maxBoost: 1);
            }
            if (win != win0)
            {
                var g = T(win0);
                var ghost = new Rectangle { Width = g.Width, Height = g.Height, Stroke = GhostBrush, StrokeThickness = 1.5, StrokeDashArray = new DoubleCollection { 3, 2 } };
                Canvas.SetLeft(ghost, g.X); Canvas.SetTop(ghost, g.Y); c.Children.Add(ghost);
            }
            var w = T(win);
            var outline = new Rectangle { Width = w.Width, Height = w.Height, Stroke = Accent, StrokeThickness = 2.5 };
            Canvas.SetLeft(outline, w.X); Canvas.SetTop(outline, w.Y); c.Children.Add(outline);
            Label(c, "x " + Minus(win.X), w.X + 18, w.Y - 9, 11, 0.7, maxBoost: 1);
            Label(c, "x " + Minus(win.Right), w.Right - 22, w.Y - 9, 11, 0.7, maxBoost: 1);

            var span = Span();
            if (_screens.Count < 3 && Num(M("NumMonitors")?.Draft) == 3)
            { FrontIcon.Kind = PackIconMaterialKind.InformationOutline; FrontNote.Text = $"Windows sees {SettingsPage.Plural(_screens.Count, "screen", "screens")}. Turn the side screens on and detect to check the window span."; }
            else if (win.X == span.X && win.Width == span.Width && win.Height == span.Height && win.Y == span.Y)
            { FrontIcon.Kind = PackIconMaterialKind.CheckBold; FrontNote.Text = "The window covers every screen."; }
            else
            { FrontIcon.Kind = PackIconMaterialKind.AlertCircle; FrontNote.Text = $"Screens span {span.Width} px from x {Minus(span.X)}. The window covers {win.Width} px from x {Minus(win.X)}."; }
        }

        private static string Minus(double v) => v.ToString("0", CultureInfo.InvariantCulture).Replace("-", "−");

        // ── controls ──

        /// <summary>Whether "Rendering and window" was left open, kept across reloads.</summary>
        private bool _moreOpen;

        /// <summary>
        /// The layout itself (screens and position) as cards; rendering and the window, which people
        /// set once, wait behind one closed expander.
        /// </summary>
        private void BuildControls()
        {
            ControlCards.Items.Clear();
            if (!IsMonitorFile) return;
            StackPanel more = null;
            foreach (var (title, keys) in Cards)
            {
                var rows = keys.Select(S).Where(s => s != null).Select(s => _ws.Page.RowFor(s)).Where(r => r != null).ToList();
                if (rows.Count == 0) continue;
                bool secondary = title == "Rendering" || title == "Window";
                var stack = new StackPanel();
                stack.Children.Add(new TextBlock { Text = title, Style = (Style)FindResource(secondary ? "GroupTitle" : "CardTitle") });
                foreach (var row in rows) stack.Children.Add(Row(row));
                if (!secondary)
                {
                    ControlCards.Items.Add(new Border { Style = (Style)FindResource("Card"), Child = stack });
                    continue;
                }
                if (more == null) more = new StackPanel { Margin = new Thickness(34, 0, 14, 8) };
                else stack.Margin = new Thickness(0, 12, 0, 0);
                more.Children.Add(stack);
            }
            if (more != null)
            {
                var expander = new SimHub.Plugins.Styles.SHExpander
                {
                    Header = new TextBlock { Text = "Rendering and window", FontSize = 14 },
                    Content = more,
                    IsExpanded = _moreOpen,
                    Margin = new Thickness(0, 0, 0, 8),
                };
                expander.Expanded += (s, e) => _moreOpen = true;
                expander.Collapsed += (s, e) => _moreOpen = false;
                ControlCards.Items.Add(expander);
            }
        }

        private FrameworkElement Row(SettingRow row)
        {
            var grid = new Grid { Margin = new Thickness(0, 6, 0, 6) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var left = new StackPanel { Margin = new Thickness(0, 0, 16, 0) };
            var label = new TextBlock { FontSize = 14 };
            label.SetBinding(TextBlock.TextProperty, new Binding(nameof(SettingRow.Label)) { Source = row, Mode = BindingMode.OneWay });
            var help = new TextBlock { Opacity = 0.62, FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
            help.SetBinding(HelpLine.TextProperty, new Binding(nameof(SettingRow.HelpText)) { Source = row, Mode = BindingMode.OneWay });
            help.SetBinding(HelpLine.UnusualProperty, new Binding(nameof(SettingRow.UnusualText)) { Source = row, Mode = BindingMode.OneWay });
            help.SetBinding(HelpLine.WasProperty, new Binding(nameof(SettingRow.WasText)) { Source = row, Mode = BindingMode.OneWay });
            left.Children.Add(label);
            left.Children.Add(help);

            var key = row.Setting.Key;
            if (key == "ScreenAngles")
            {
                var hint = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };   // wraps "Use N°" under the text in a narrow pane
                _angleHint = new TextBlock { FontSize = 12.5, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
                _angleUse = new SHLinkButtonSmall { Margin = new Thickness(8, 0, 0, 0), Visibility = Visibility.Collapsed };
                _angleUse.Click += (s, e) => SetNum("MonitorSetup|ScreenAngles", Geo(true).Ideal);
                hint.Children.Add(_angleHint);
                hint.Children.Add(_angleUse);
                left.Children.Add(hint);
            }
            if (key == "MonitorWidth") left.Children.Add(Calculator());

            grid.Children.Add(left);
            var control = Control(row);
            Grid.SetColumn(control, 1);
            grid.Children.Add(control);
            return grid;
        }

        private FrameworkElement Control(SettingRow row)
        {
            FrameworkElement el;
            switch (row.Type)
            {
                case "bool":
                    var t = new SHToggleCheckbox { VerticalAlignment = VerticalAlignment.Center };
                    t.SetBinding(ToggleButton.IsCheckedProperty, new Binding(nameof(SettingRow.IsOn)) { Source = row, Mode = BindingMode.TwoWay });
                    t.SetBinding(IsEnabledProperty, new Binding(nameof(SettingRow.IsEditable)) { Source = row, Mode = BindingMode.OneWay });
                    el = t; break;
                case "enum":
                    var cb = new ComboBox { MinWidth = 120, DisplayMemberPath = "Label", ItemsSource = row.Options };
                    cb.SetBinding(Selector.SelectedItemProperty, new Binding(nameof(SettingRow.SelectedOption)) { Source = row, Mode = BindingMode.TwoWay });
                    cb.SetBinding(IsEnabledProperty, new Binding(nameof(SettingRow.IsEditable)) { Source = row, Mode = BindingMode.OneWay });
                    el = cb; break;
                default:
                    var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                    if (Slide.TryGetValue(row.Setting.Key, out var sl))
                    {
                        var slider = new Slider { Width = 120, Minimum = sl.Min, Maximum = sl.Max, SmallChange = sl.Step, LargeChange = sl.Step * 10, TickFrequency = sl.Step, IsSnapToTickEnabled = true, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
                        slider.SetBinding(RangeBase.ValueProperty, new Binding(nameof(SettingRow.NumberValue)) { Source = row, Mode = BindingMode.TwoWay });
                        slider.SetBinding(IsEnabledProperty, new Binding(nameof(SettingRow.IsEditable)) { Source = row, Mode = BindingMode.OneWay });
                        System.Windows.Automation.AutomationProperties.SetName(slider, row.Label + " slider");
                        panel.Children.Add(slider);
                    }
                    var box = new TextBox { Width = 74, TextAlignment = TextAlignment.Right };
                    box.SetBinding(TextBox.TextProperty, new Binding(nameof(SettingRow.Text)) { Source = row, Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.LostFocus });
                    box.SetBinding(IsEnabledProperty, new Binding(nameof(SettingRow.IsEditable)) { Source = row, Mode = BindingMode.OneWay });
                    CommitOnEnter.SetEnabled(box, true);
                    System.Windows.Automation.AutomationProperties.SetName(box, row.Label);
                    panel.Children.Add(box);
                    // one unit column so every unit lines up down the card
                    panel.Children.Add(new TextBlock { Text = row.Unit ?? "", MinWidth = 34, Margin = new Thickness(6, 0, 0, 0), Opacity = 0.62, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center });
                    el = panel; break;
            }
            el.VerticalAlignment = VerticalAlignment.Center;
            return el;
        }

        private FrameworkElement Calculator()
        {
            var wrap = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
            var toggle = new SHLinkButtonSmall { Content = "Calculate widths from screen size", HorizontalAlignment = HorizontalAlignment.Left };
            _calcPanel = new StackPanel { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 8, 0, 0) };
            toggle.Click += (s, e) =>
            {
                bool open = _calcPanel.Visibility != Visibility.Visible;
                _calcPanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
                toggle.Content = open ? "Hide size calculator" : "Calculate widths from screen size";
            };
            var fields = new WrapPanel();
            _calcDiag = new TextBox { Width = 56, Text = "32", TextAlignment = TextAlignment.Right };
            _calcAspect = new ComboBox { Width = 80, ItemsSource = Aspects.Keys.ToList(), SelectedIndex = 0 };
            _calcBezel = new TextBox { Width = 48, Text = "8", TextAlignment = TextAlignment.Right };
            void Field(string before, FrameworkElement input, string after)
            {
                var p = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 14, 6) };
                p.Children.Add(new TextBlock { Text = before, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0), Opacity = 0.8 });
                p.Children.Add(input);
                if (after != null) p.Children.Add(new TextBlock { Text = after, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0), Opacity = 0.62 });
                fields.Children.Add(p);
            }
            Field("Diagonal", _calcDiag, "in");
            Field("Aspect", _calcAspect, null);
            Field("Bezel", _calcBezel, "mm per side");
            _calcResult = new TextBlock { FontSize = 12.5, Margin = new Thickness(0, 2, 0, 8) };
            var use = new SHButtonSecondary { Content = "Use these", HorizontalAlignment = HorizontalAlignment.Left };
            use.Click += (s, e) =>
            {
                var (w, m) = CalcValues();
                SetNum("MonitorSetup|ScreenWidth", w);
                SetNum("MonitorSetup|MonitorWidth", m);
            };
            _calcDiag.TextChanged += (s, e) => UpdateCalc();
            _calcBezel.TextChanged += (s, e) => UpdateCalc();
            _calcAspect.SelectionChanged += (s, e) => UpdateCalc();
            _calcPanel.Children.Add(fields);
            _calcPanel.Children.Add(_calcResult);
            _calcPanel.Children.Add(use);
            wrap.Children.Add(toggle);
            wrap.Children.Add(_calcPanel);
            UpdateCalc();
            return wrap;
        }

        private (int Screen, int Monitor) CalcValues()
        {
            double.TryParse(_calcDiag.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var diag);
            double.TryParse(_calcBezel.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var bezel);
            var a = Aspects[(string)_calcAspect.SelectedItem ?? "16:9"];
            int w = TripleGeometry.VisibleWidthMm(diag, a.W, a.H);
            return (w, w + (int)Math.Round(2 * Math.Max(0, bezel)));
        }

        private void UpdateCalc()
        {
            if (_calcResult == null) return;
            var (w, m) = CalcValues();
            _calcResult.Text = $"Screen {w} mm · monitor {m} mm";
        }

        private void UpdateHints()
        {
            if (_angleHint == null || M("ScreenAngles") == null) return;
            var g = Geo(true);
            var angle = M("ScreenAngles");
            _angleUse.Visibility = Visibility.Collapsed;
            if (angle.IsInert || g.In.NumMonitors != 3) { _angleHint.Text = ""; return; }
            if (g.IdealIsClamped || M("ScreenWidth")?.IsUnusual == true || M("MonitorWidth")?.IsUnusual == true)
            {
                _angleHint.Text = "Check screen width first. The ideal angle depends on it.";
                _angleHint.Foreground = HelpLine.Warn; _angleHint.Opacity = 1;
            }
            else if (Math.Abs(g.Ideal - g.In.ScreenAngles) <= 1)
            {
                _angleHint.Text = "Matches the ideal angle for this distance and width.";
                _angleHint.ClearValue(TextBlock.ForegroundProperty); _angleHint.Opacity = 0.62;
            }
            else
            {
                _angleHint.Text = $"Ideal for this distance and width: {g.Ideal}°.";
                _angleHint.ClearValue(TextBlock.ForegroundProperty); _angleHint.Opacity = 0.62;
                _angleUse.Content = $"Use {g.Ideal}°";
                _angleUse.Visibility = Visibility.Visible;
            }
        }

        private void SetNum(string key, double value)
        {
            var s = S(key);
            var row = s == null ? null : _ws.Page.RowFor(s);
            if (row != null) _ws.Page.Change(row, Setting.Format(value));
        }

        // ── saved layout ──

        private sealed class GuardRow
        {
            public string Label { get; set; }
            public string Saved { get; set; }
            public string Now { get; set; }
            public PackIconMaterialKind Icon { get; set; }
            public Brush IconBrush { get; set; }
        }

        private void RenderGuard()
        {
            var saved = _ws.Plugin.Settings.SavedLayout;
            var diffs = LayoutGuard.Differences(Session, saved);
            AutoRestore.IsChecked = _ws.Plugin.Settings.GuardAutoRestore;
            if (saved.Count == 0)
            {
                GuardCount.Text = "Not saved yet";
                GuardNote.Text = "Save your layout so Tech Inspector can put it back when iRacing changes it (after a driver update, a crash, or launching with the side screens off).";
            }
            else
            {
                GuardCount.Text = (diffs.Count > 0 ? SettingsPage.Plural(diffs.Count, "difference", "differences") + " from the file" : "Matches the file") + " · guarded";
                var when = _ws.Plugin.Settings.SavedLayoutWhen;
                GuardNote.Text = (when.HasValue ? $"Saved {when.Value:d MMM} at {when.Value:HH:mm}. " : "") + "Checked each time iRacing closes.";
            }
            GuardRows.ItemsSource = LayoutGuard.Settings(Session).Select(s =>
            {
                bool has = saved.TryGetValue(s.Section + "|" + s.Key, out var v);
                bool same = !has || Setting.SameValue(v, s.Applied);
                return new GuardRow
                {
                    Label = s.Label,
                    Saved = has ? s.Show(v) : "—",
                    Now = s.Show(s.Applied),
                    Icon = same ? PackIconMaterialKind.Check : PackIconMaterialKind.Alert,
                    IconBrush = same ? StatusLine.OkBrush : StatusLine.WarnBrush,
                };
            }).ToList();
            RestoreButton.IsEnabled = diffs.Count > 0;
            SaveLayoutButton.Content = saved.Count == 0 ? "Save this layout" : "Save the file as the layout";
            if (diffs.Count > 0 && !GuardExpander.IsExpanded) GuardExpander.IsExpanded = true;
        }

        // ── handlers ──

        private void Detect_Click(object sender, RoutedEventArgs e)
        {
            _screens = System.Windows.Forms.Screen.AllScreens.OrderBy(s => s.Bounds.X).ToList();
            Invalidate();
        }

        private void Fit_Click(object sender, RoutedEventArgs e)
        {
            var span = Span();
            SetNum("Display|windowedXPos", span.X);
            SetNum("Display|windowedYPos", span.Y);
            SetNum("Display|windowedWidth", span.Width);
            SetNum("Display|windowedHeight", span.Height);
        }

        /// <summary>Stages the saved values as pending changes; the changes bar backs up and writes them.</summary>
        private void Restore_Click(object sender, RoutedEventArgs e)
        {
            var n = LayoutGuard.StageRestore(Session, _ws.Plugin.Settings.SavedLayout);
            if (n > 0) { _ws.Page.RefreshAll(); _ws.Page.NotifyDraftsChanged(); }
        }

        private void SaveLayout_Click(object sender, RoutedEventArgs e) => _ws.SaveLayout();

        private void AutoRestore_Click(object sender, RoutedEventArgs e)
        {
            _ws.Plugin.Settings.GuardAutoRestore = AutoRestore.IsChecked == true;
            _ws.Plugin.SaveSettings();
            Invalidate();
        }
    }
}
