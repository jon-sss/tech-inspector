using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace TechInspector.Core.Ini
{
    /// <summary>
    /// A line-preserving model of one of iRacing's .ini files.
    ///
    /// iRacing writes every entry as <c>key=value</c>, pads it with spaces to a fixed column
    /// (40 in app.ini and core.ini, 48 in the renderer files), then <c>\t; comment</c>.
    /// Long values push the tab past the column with no padding. Lines end in CRLF and the
    /// files have no BOM.
    ///
    /// Every line keeps its original text. Changing a value rebuilds only that line, so a
    /// document that is parsed and written back unchanged is byte-for-byte identical.
    /// Text is held as Latin-1, which maps each byte to one char, so no byte is ever lost.
    /// </summary>
    public sealed class IniDocument
    {
        /// <summary>Latin-1: one char per byte, both ways, for exact round trips.</summary>
        internal static readonly Encoding ByteText = Encoding.GetEncoding(28591);

        /// <summary>Used when a file has no padded entries to learn the column from.</summary>
        public const int FallbackCommentColumn = 40;

        private readonly List<IniLine> _lines;

        private IniDocument(List<IniLine> lines)
        {
            _lines = lines;
            CommentColumn = LearnCommentColumn(lines);
        }

        public IReadOnlyList<IniLine> Lines => _lines;

        /// <summary>The column this file pads its comments to (40 or 48 in iRacing's files).</summary>
        public int CommentColumn { get; }

        /// <summary>True once any value has been changed since loading.</summary>
        public bool IsDirty => _lines.Any(l => l.IsChanged);

        public IEnumerable<string> SectionNames =>
            _lines.Where(l => l.Kind == IniLineKind.Section).Select(l => l.Section).Distinct(StringComparer.OrdinalIgnoreCase);

        public IEnumerable<IniLine> Entries => _lines.Where(l => l.Kind == IniLineKind.Entry);

        public IEnumerable<IniLine> EntriesIn(string section) =>
            Entries.Where(l => string.Equals(l.Section, section, StringComparison.OrdinalIgnoreCase));

        public static IniDocument Load(string path) => Parse(File.ReadAllBytes(path));

        public static IniDocument Parse(byte[] bytes)
        {
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));
            var text = ByteText.GetString(bytes);
            var lines = new List<IniLine>();
            string section = null;
            int start = 0, number = 1;

            while (start < text.Length)
            {
                int nl = text.IndexOf('\n', start);
                string raw, terminator;
                if (nl < 0)
                {
                    raw = text.Substring(start);
                    terminator = "";
                    start = text.Length;
                }
                else
                {
                    bool crlf = nl > start && text[nl - 1] == '\r';
                    raw = text.Substring(start, (crlf ? nl - 1 : nl) - start);
                    terminator = crlf ? "\r\n" : "\n";
                    start = nl + 1;
                }

                var line = IniLine.Parse(raw, terminator, section, number++);
                if (line.Kind == IniLineKind.Section) section = line.Section;
                lines.Add(line);
            }

            return new IniDocument(lines);
        }

        public byte[] ToBytes()
        {
            var sb = new StringBuilder();
            foreach (var l in _lines) sb.Append(l.Raw).Append(l.Terminator);
            return ByteText.GetBytes(sb.ToString());
        }

        /// <summary>Finds an entry. Section and key match without regard to case; an exact-case key wins.</summary>
        public IniLine Find(string section, string key)
        {
            IniLine loose = null;
            foreach (var l in EntriesIn(section))
            {
                if (l.Key == key) return l;
                if (loose == null && string.Equals(l.Key, key, StringComparison.OrdinalIgnoreCase)) loose = l;
            }
            return loose;
        }

        /// <summary>
        /// Sets an existing entry's value, rebuilding only that line. Returns false when the entry
        /// doesn't exist: the plugin never adds keys, because iRacing drops keys it doesn't know.
        /// </summary>
        public bool TrySet(string section, string key, string value)
        {
            var line = Find(section, key);
            if (line == null) return false;
            line.SetValue(value, CommentColumn);
            return true;
        }

        /// <summary>Reverts every changed line to the text it was loaded with.</summary>
        public void RevertAll()
        {
            foreach (var l in _lines) l.Revert();
        }

        /// <summary>
        /// Rebuilds every entry iRacing wrote from its parts. Used by tests to prove the writer matches
        /// iRacing's layout. Lines padded by hand are left alone: they aren't iRacing's layout.
        /// </summary>
        internal void RebuildAllEntries()
        {
            foreach (var l in Entries) if (!l.HandPadded) l.Rebuild(CommentColumn);
        }

        /// <summary>
        /// The most common comment column among padded entries. Entries whose value overflowed the
        /// column carry no padding, so they say nothing about the width and are skipped.
        /// </summary>
        private static int LearnCommentColumn(IEnumerable<IniLine> lines)
        {
            var best = lines.Where(l => l.Kind == IniLineKind.Entry && l.HadPadding)
                            .GroupBy(l => l.CommentColumn)
                            .OrderByDescending(g => g.Count())
                            .ThenBy(g => g.Key)
                            .FirstOrDefault();
            return best?.Key ?? FallbackCommentColumn;
        }
    }
}
