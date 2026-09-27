using System;
using System.Linq;
using System.Text;

namespace TechInspector.Core.Ini
{
    public enum IniLineKind { Blank, Section, Entry, Comment, Other }

    /// <summary>
    /// One line of an iRacing .ini file. <see cref="Raw"/> is the exact text, without its line
    /// ending. Entries also expose their key, value and comment.
    /// </summary>
    public sealed class IniLine
    {
        private const string CommentMark = "\t;";
        private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

        private readonly string _originalRaw;
        private readonly string _originalValue;
        private readonly int _originalCommentColumn;

        private IniLine(IniLineKind kind, string raw, string terminator, string section, int number,
                        string key = null, string value = null, string comment = null, int commentColumn = -1, bool hadPadding = false, bool handPadded = false)
        {
            Kind = kind;
            Raw = _originalRaw = raw;
            Terminator = terminator;
            Section = section;
            LineNumber = number;
            Key = key;
            Value = _originalValue = value;
            Comment = comment;
            CommentColumn = _originalCommentColumn = commentColumn;
            HadPadding = hadPadding;
            HandPadded = handPadded;
        }

        public IniLineKind Kind { get; }

        /// <summary>The line's text as it will be written, without the line ending.</summary>
        public string Raw { get; private set; }

        /// <summary>"\r\n", "\n", or "" for a last line with no ending.</summary>
        public string Terminator { get; }

        /// <summary>The section this line sits in; for a section header, its own name. Null before the first header.</summary>
        public string Section { get; }

        public int LineNumber { get; }

        /// <summary>The key exactly as written. Entries only.</summary>
        public string Key { get; }

        /// <summary>The value with padding removed, as stored bytes (Latin-1). Entries only.</summary>
        public string Value { get; private set; }

        /// <summary>The value decoded for display: UTF-8 when the bytes are valid UTF-8, otherwise as stored.</summary>
        public string DisplayValue => ToDisplay(Value);

        /// <summary>iRacing's inline note: the text after "\t;", including its leading space. Null when the line has none.</summary>
        public string Comment { get; }

        /// <summary>The index of "\t;" in <see cref="Raw"/>, or -1.</summary>
        public int CommentColumn { get; private set; }

        /// <summary>True when the loaded line had spaces between its value and its comment.</summary>
        public bool HadPadding { get; }

        /// <summary>True when the padding before the comment has tabs: a line edited by hand, not written by iRacing.</summary>
        public bool HandPadded { get; }

        public bool IsChanged => Raw != _originalRaw;

        public string OriginalValue => _originalValue;

        internal static IniLine Parse(string raw, string terminator, string currentSection, int number)
        {
            var trimmed = raw.Trim();
            if (trimmed.Length == 0)
                return new IniLine(IniLineKind.Blank, raw, terminator, currentSection, number);

            if (trimmed[0] == '[')
            {
                int close = trimmed.LastIndexOf(']');
                if (close > 0)
                    return new IniLine(IniLineKind.Section, raw, terminator, trimmed.Substring(1, close - 1), number);
            }

            if (trimmed[0] == ';' || trimmed[0] == '#')
                return new IniLine(IniLineKind.Comment, raw, terminator, currentSection, number);

            int eq = raw.IndexOf('=');
            if (eq <= 0)
                return new IniLine(IniLineKind.Other, raw, terminator, currentSection, number);

            var key = raw.Substring(0, eq);
            var rest = raw.Substring(eq + 1);
            int mark = rest.IndexOf(CommentMark, StringComparison.Ordinal);
            if (mark < 0)
                return new IniLine(IniLineKind.Entry, raw, terminator, currentSection, number, key, rest);

            var valuePart = rest.Substring(0, mark);
            var value = valuePart.TrimEnd(' ', '\t');   // iRacing pads with spaces; hand edits often use tabs
            return new IniLine(IniLineKind.Entry, raw, terminator, currentSection, number, key, value,
                               comment: rest.Substring(mark + CommentMark.Length),
                               commentColumn: eq + 1 + mark,
                               hadPadding: value.Length < valuePart.Length,
                               handPadded: valuePart.Substring(value.Length).IndexOf('\t') >= 0);
        }

        /// <summary>Sets the value from display text, rebuilding the line in iRacing's layout.</summary>
        internal void SetValue(string displayText, int documentColumn)
        {
            if (Kind != IniLineKind.Entry) throw new InvalidOperationException("Only entries have values.");
            if (displayText == null) throw new ArgumentNullException(nameof(displayText));
            if (displayText.IndexOfAny(new[] { '\r', '\n' }) >= 0 || displayText.Contains(CommentMark))
                throw new ArgumentException("A value can't contain a line break or a tab followed by ';'.", nameof(displayText));

            var stored = FromDisplay(displayText);
            if (stored == Value) return;
            if (stored == _originalValue) { Revert(); return; }
            Value = stored;
            Rebuild(documentColumn);
        }

        /// <summary>
        /// Writes the line from its parts: key=value, padded to the comment column when there is a
        /// comment, then "\t;" and the comment. A line that was padded keeps its own column; one that
        /// overflowed uses the file's column.
        /// </summary>
        internal void Rebuild(int documentColumn)
        {
            if (Kind != IniLineKind.Entry) return;
            var prefix = Key + "=" + Value;
            if (Comment == null)
            {
                Raw = prefix;
                return;
            }
            int column = HadPadding ? _originalCommentColumn : documentColumn;
            if (prefix.Length < column) prefix = prefix.PadRight(column);
            Raw = prefix + CommentMark + Comment;
            CommentColumn = prefix.Length;
        }

        internal void Revert()
        {
            Raw = _originalRaw;
            Value = _originalValue;
            CommentColumn = _originalCommentColumn;
        }

        public override string ToString() => Raw;

        internal static string ToDisplay(string stored)
        {
            if (stored == null || stored.All(c => c < 0x80)) return stored;
            try { return StrictUtf8.GetString(IniDocument.ByteText.GetBytes(stored)); }
            catch (DecoderFallbackException) { return stored; }
        }

        internal static string FromDisplay(string text)
        {
            if (text.All(c => c < 0x80)) return text;
            return IniDocument.ByteText.GetString(Encoding.UTF8.GetBytes(text));
        }
    }
}
