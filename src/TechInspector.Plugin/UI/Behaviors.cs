using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace TechInspector.Plugin.UI
{
    /// <summary>
    /// Builds a setting's explanation line as runs: the explanation (with search matches highlighted),
    /// then an amber unusual note, "Was X." in a lighter tone, and an amber caution.
    /// </summary>
    public static class HelpLine
    {
        public static readonly Brush Warn = Frozen(Color.FromRgb(0xE8, 0xA3, 0x3A));

        public static readonly DependencyProperty TextProperty = Reg("Text");
        public static readonly DependencyProperty QueryProperty = Reg("Query");
        public static readonly DependencyProperty UnusualProperty = Reg("Unusual");
        public static readonly DependencyProperty WasProperty = Reg("Was");
        public static readonly DependencyProperty CautionProperty = Reg("Caution");

        public static void SetText(DependencyObject o, string v) => o.SetValue(TextProperty, v);
        public static string GetText(DependencyObject o) => (string)o.GetValue(TextProperty);
        public static void SetQuery(DependencyObject o, string v) => o.SetValue(QueryProperty, v);
        public static string GetQuery(DependencyObject o) => (string)o.GetValue(QueryProperty);
        public static void SetUnusual(DependencyObject o, string v) => o.SetValue(UnusualProperty, v);
        public static string GetUnusual(DependencyObject o) => (string)o.GetValue(UnusualProperty);
        public static void SetWas(DependencyObject o, string v) => o.SetValue(WasProperty, v);
        public static string GetWas(DependencyObject o) => (string)o.GetValue(WasProperty);
        public static void SetCaution(DependencyObject o, string v) => o.SetValue(CautionProperty, v);
        public static string GetCaution(DependencyObject o) => (string)o.GetValue(CautionProperty);

        private static DependencyProperty Reg(string name) =>
            DependencyProperty.RegisterAttached(name, typeof(string), typeof(HelpLine), new PropertyMetadata(null, (o, e) => Build(o as TextBlock)));

        private static void Build(TextBlock tb)
        {
            if (tb == null) return;
            tb.Inlines.Clear();
            Highlight.AddRuns(tb, GetText(tb), GetQuery(tb));
            var unusual = GetUnusual(tb);
            if (!string.IsNullOrEmpty(unusual)) tb.Inlines.Add(new Run(unusual) { Foreground = Warn });
            var was = GetWas(tb);
            if (!string.IsNullOrEmpty(was)) tb.Inlines.Add(new Run(was) { Foreground = tb.Foreground, FontStyle = FontStyles.Normal });
            var caution = GetCaution(tb);
            if (!string.IsNullOrEmpty(caution)) tb.Inlines.Add(new Run(caution) { Foreground = Warn });
        }

        private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
    }

    /// <summary>Highlights search matches in a TextBlock with the SimHub accent colour.</summary>
    public static class Highlight
    {
        public static readonly DependencyProperty TextProperty =
            DependencyProperty.RegisterAttached("Text", typeof(string), typeof(Highlight), new PropertyMetadata(null, (o, e) => Build(o as TextBlock)));
        public static readonly DependencyProperty QueryProperty =
            DependencyProperty.RegisterAttached("Query", typeof(string), typeof(Highlight), new PropertyMetadata(null, (o, e) => Build(o as TextBlock)));

        public static void SetText(DependencyObject o, string v) => o.SetValue(TextProperty, v);
        public static string GetText(DependencyObject o) => (string)o.GetValue(TextProperty);
        public static void SetQuery(DependencyObject o, string v) => o.SetValue(QueryProperty, v);
        public static string GetQuery(DependencyObject o) => (string)o.GetValue(QueryProperty);

        private static void Build(TextBlock tb)
        {
            if (tb == null) return;
            tb.Inlines.Clear();
            AddRuns(tb, GetText(tb), GetQuery(tb));
        }

        internal static void AddRuns(TextBlock tb, string text, string query)
        {
            if (string.IsNullOrEmpty(text)) return;
            if (string.IsNullOrEmpty(query)) { tb.Inlines.Add(new Run(text)); return; }
            var mark = MarkBrush(tb);
            int i = 0, j;
            while ((j = text.IndexOf(query, i, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                if (j > i) tb.Inlines.Add(new Run(text.Substring(i, j - i)));
                tb.Inlines.Add(new Run(text.Substring(j, query.Length)) { Background = mark, Foreground = Brushes.White });
                i = j + query.Length;
            }
            if (i < text.Length) tb.Inlines.Add(new Run(text.Substring(i)));
        }

        private static Brush MarkBrush(FrameworkElement fe)
        {
            var accent = (fe.TryFindResource("AccentColorBrush") as SolidColorBrush)?.Color ?? Color.FromRgb(0x64, 0x59, 0xDF);
            var b = new SolidColorBrush(Color.FromArgb(0x73, accent.R, accent.G, accent.B));
            b.Freeze();
            return b;
        }
    }

    /// <summary>Enter commits a text box's value; Escape puts back the value it had.</summary>
    public static class CommitOnEnter
    {
        public static readonly DependencyProperty EnabledProperty =
            DependencyProperty.RegisterAttached("Enabled", typeof(bool), typeof(CommitOnEnter), new PropertyMetadata(false, OnChanged));

        public static void SetEnabled(DependencyObject o, bool v) => o.SetValue(EnabledProperty, v);
        public static bool GetEnabled(DependencyObject o) => (bool)o.GetValue(EnabledProperty);

        private static void OnChanged(DependencyObject o, DependencyPropertyChangedEventArgs e)
        {
            if (!(o is TextBox tb)) return;
            tb.KeyDown -= KeyDown;
            if ((bool)e.NewValue) tb.KeyDown += KeyDown;
        }

        private static void KeyDown(object sender, KeyEventArgs e)
        {
            var tb = (TextBox)sender;
            var binding = tb.GetBindingExpression(TextBox.TextProperty);
            if (e.Key == Key.Enter) { binding?.UpdateSource(); e.Handled = true; }
            else if (e.Key == Key.Escape) { binding?.UpdateTarget(); e.Handled = true; }
        }
    }

    public sealed class BoolToVisibility : IValueConverter
    {
        public bool Invert { get; set; }
        /// <summary>Keep the space when not shown, so things beside it stay aligned.</summary>
        public bool KeepSpace { get; set; }
        public object Convert(object value, Type t, object p, CultureInfo c) =>
            (value is bool b && b) ^ Invert ? Visibility.Visible : KeepSpace ? Visibility.Hidden : Visibility.Collapsed;
        public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
    }

    public sealed class NullToVisibility : IValueConverter
    {
        public object Convert(object value, Type t, object p, CultureInfo c) => value == null || (value is string s && s.Length == 0) ? Visibility.Collapsed : Visibility.Visible;
        public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
    }

    /// <summary>Picks the control template for a setting row from its type.</summary>
    public sealed class ControlTemplateSelector : DataTemplateSelector
    {
        public DataTemplate Bool { get; set; }
        public DataTemplate Enum { get; set; }
        public DataTemplate Number { get; set; }
        public DataTemplate NumberWithUnit { get; set; }
        public DataTemplate Text { get; set; }
        public DataTemplate ReadOnly { get; set; }
        public DataTemplate Required { get; set; }

        public override DataTemplate SelectTemplate(object item, DependencyObject container)
        {
            if (!(item is SettingRow r)) return null;
            switch (r.Type)
            {
                case "bool": return Bool;
                case "enum": return Enum;
                case "num": return r.HasUnit ? NumberWithUnit : Number;
                case "readonly": return ReadOnly;
                case "required": return Required;
                default: return Text;
            }
        }
    }
}
