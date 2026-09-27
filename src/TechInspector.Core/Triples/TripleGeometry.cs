using System;
using System.Collections.Generic;

namespace TechInspector.Core.Triples
{
    /// <summary>A point in millimetres, top-down: eyes at the origin, looking along +Y.</summary>
    public struct Pt
    {
        public double X, Y;
        public Pt(double x, double y) { X = x; Y = y; }
    }

    /// <summary>One monitor seen from above: its body edges (A, B) and its visible screen edges (ScreenA, ScreenB).</summary>
    public sealed class Panel
    {
        public Pt A, B, ScreenA, ScreenB;
    }

    /// <summary>What [MonitorSetup] describes. Values in iRacing's units: mm and degrees.</summary>
    public sealed class TripleInputs
    {
        public int NumMonitors { get; set; } = 3;
        public bool Curved { get; set; }
        public double RadiusOfCurvature { get; set; } = 1800;
        /// <summary>Visible width of one screen, without bezels.</summary>
        public double ScreenWidth { get; set; }
        /// <summary>Full width of one monitor, with bezels.</summary>
        public double MonitorWidth { get; set; }
        /// <summary>Eyes to the centre screen, straight ahead.</summary>
        public double ViewingDist { get; set; }
        /// <summary>How far each side screen turns in.</summary>
        public double ScreenAngles { get; set; }
    }

    /// <summary>
    /// The top-down geometry of a single or triple screen setup, ported from the prototype.
    /// Side monitors hinge at the centre monitor's outer edges.
    /// </summary>
    public sealed class TripleGeometry
    {
        public const double MinAngle = 10, MaxAngle = 65;

        public TripleInputs In { get; private set; }
        public List<Panel> Panels { get; } = new List<Panel>();
        public Panel Right { get; private set; }
        /// <summary>The outermost visible edge on the right; the left mirrors it.</summary>
        public Pt Outer { get; private set; }
        /// <summary>Horizontal field of view across all screens, degrees.</summary>
        public double Fov { get; private set; }
        /// <summary>Horizontal field of view of the centre screen alone, degrees.</summary>
        public double CentreFov { get; private set; }
        /// <summary>The side angle that points each side screen straight at the eyes: 2·atan(M / 2D).</summary>
        public double IdealRaw { get; private set; }
        /// <summary><see cref="IdealRaw"/> rounded and clamped to what iRacing accepts (10 to 65).</summary>
        public int Ideal { get; private set; }
        public bool IdealIsClamped => IdealRaw > MaxAngle;

        public static TripleGeometry From(TripleInputs v)
        {
            var g = new TripleGeometry { In = v };
            double W = v.ScreenWidth, M = Math.Max(v.MonitorWidth, W), D = v.ViewingDist, th = Rad(v.ScreenAngles);
            double bezel = (M - W) / 2, half = M / 2;

            Panel Side(Pt a, double dx, double dy) => new Panel
            {
                A = a,
                B = new Pt(a.X + M * dx, a.Y + M * dy),
                ScreenA = new Pt(a.X + bezel * dx, a.Y + bezel * dy),
                ScreenB = new Pt(a.X + (bezel + W) * dx, a.Y + (bezel + W) * dy),
            };

            var centre = new Panel { A = new Pt(-half, D), B = new Pt(half, D), ScreenA = new Pt(-W / 2, D), ScreenB = new Pt(W / 2, D) };
            var right = Side(new Pt(half, D), Math.Cos(th), -Math.Sin(th));
            var left = Side(new Pt(-half, D), -Math.Cos(th), -Math.Sin(th));
            if (v.NumMonitors == 3) { g.Panels.Add(left); g.Panels.Add(centre); g.Panels.Add(right); }
            else g.Panels.Add(centre);

            g.Right = right;
            g.Outer = v.NumMonitors == 3 ? right.ScreenB : centre.ScreenB;
            g.Fov = 2 * Deg(Math.Atan2(g.Outer.X, g.Outer.Y));
            g.CentreFov = 2 * Deg(Math.Atan2(W / 2, D));
            g.IdealRaw = 2 * Deg(Math.Atan2(M / 2, D));
            g.Ideal = (int)Math.Min(MaxAngle, Math.Max(MinAngle, Math.Round(g.IdealRaw)));
            return g;
        }

        /// <summary>Width of a screen's visible area from its diagonal (inches) and aspect ratio, in mm.</summary>
        public static int VisibleWidthMm(double diagonalInches, double aspectW, double aspectH) =>
            (int)Math.Round(diagonalInches * 25.4 * aspectW / Math.Sqrt(aspectW * aspectW + aspectH * aspectH));

        /// <summary>The side angle for a point dragged to (x, y): the right monitor turns about its hinge.</summary>
        public double AngleTowards(Pt p)
        {
            double M = Math.Max(In.MonitorWidth, In.ScreenWidth);
            var a = Deg(Math.Atan2(-(p.Y - In.ViewingDist), p.X - M / 2));
            return Math.Max(MinAngle, Math.Min(MaxAngle, Math.Round(a)));
        }

        public static double Rad(double d) => d * Math.PI / 180;
        public static double Deg(double r) => r * 180 / Math.PI;
    }
}
