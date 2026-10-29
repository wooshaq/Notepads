// ---------------------------------------------------------------------------------------------
//  Sublime Pad (personal build of Notepads): highlight whole lines that contain keywords.
//  See LICENSE file in the project root for license information.
// ---------------------------------------------------------------------------------------------

namespace Notepads.Services
{
    using System;
    using System.Linq;
    using Microsoft.Toolkit.Uwp.Helpers;
    using Notepads.Settings;
    using Windows.UI;

    /// <summary>
    /// Five rules, each a comma separated list of keywords and a colour. A line containing any
    /// keyword of a rule (case-insensitive) gets that rule's background; the first rule wins.
    /// </summary>
    public static class KeywordHighlightService
    {
        public const int RuleCount = 5;

        public static event EventHandler OnRulesChanged;

        private static readonly Color[] DefaultColors =
        {
            Color.FromArgb(0x66, 0xE5, 0x48, 0x4D), // red
            Color.FromArgb(0x66, 0xF5, 0xA5, 0x24), // orange
            Color.FromArgb(0x66, 0x46, 0xC2, 0x6B), // green
            Color.FromArgb(0x66, 0x4C, 0x8D, 0xFF), // blue
            Color.FromArgb(0x66, 0xB0, 0x7C, 0xFF), // purple
        };

        private static readonly string[] _keywords = new string[RuleCount];
        private static readonly Color[] _colors = new Color[RuleCount];
        private static readonly string[][] _parsed = new string[RuleCount][];

        private static string KeywordsKey(int rule) => $"KeywordHighlightKeywordsStr{rule}";
        private static string ColorKey(int rule) => $"KeywordHighlightColorHexStr{rule}";

        public static void Initialize()
        {
            for (int i = 0; i < RuleCount; i++)
            {
                _keywords[i] = ApplicationSettingsStore.Read(KeywordsKey(i)) as string ?? string.Empty;
                _colors[i] = ApplicationSettingsStore.Read(ColorKey(i)) is string hex ? hex.ToColor() : DefaultColors[i];
                _parsed[i] = Parse(_keywords[i]);
            }
        }

        private static string[] Parse(string keywords)
        {
            return (keywords ?? string.Empty)
                .Split(',')
                .Select(k => k.Trim())
                .Where(k => k.Length > 0)
                .ToArray();
        }

        public static string GetKeywords(int rule) => _keywords[rule];

        public static void SetKeywords(int rule, string keywords)
        {
            keywords = keywords ?? string.Empty;
            if (_keywords[rule] == keywords) return;
            _keywords[rule] = keywords;
            _parsed[rule] = Parse(keywords);
            ApplicationSettingsStore.Write(KeywordsKey(rule), keywords);
            OnRulesChanged?.Invoke(null, EventArgs.Empty);
        }

        public static Color GetColor(int rule) => _colors[rule];

        public static void SetColor(int rule, Color color)
        {
            if (_colors[rule] == color) return;
            _colors[rule] = color;
            ApplicationSettingsStore.Write(ColorKey(rule), color.ToHex());
            OnRulesChanged?.Invoke(null, EventArgs.Empty);
        }

        public static bool HasRules => _parsed.Any(p => p != null && p.Length > 0);

        /// <summary>The colour of the first rule with a keyword in the line, if any.</summary>
        public static bool TryMatch(string line, out Color color)
        {
            if (!string.IsNullOrEmpty(line))
            {
                for (int i = 0; i < RuleCount; i++)
                {
                    var keywords = _parsed[i];
                    if (keywords == null) continue;
                    foreach (var keyword in keywords)
                    {
                        if (line.IndexOf(keyword, StringComparison.CurrentCultureIgnoreCase) >= 0)
                        {
                            color = _colors[i];
                            return true;
                        }
                    }
                }
            }

            color = default;
            return false;
        }
    }
}
