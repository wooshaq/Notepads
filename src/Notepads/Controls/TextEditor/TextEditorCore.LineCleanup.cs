// ---------------------------------------------------------------------------------------------
//  Sublime Pad: whole-line delete and trailing blank line cleanup.
// ---------------------------------------------------------------------------------------------

namespace Notepads.Controls.TextEditor
{
    using System;
    using System.Collections.Generic;
    using Windows.System;
    using Windows.UI.Core;
    using Windows.UI.Text;
    using Windows.UI.Xaml;
    using Notepads.Services;

    public partial class TextEditorCore
    {
        /// <summary>
        /// Backspace / Delete on a selection that covers whole line(s) (from the start of a line
        /// to the end of a line, without the line break) removes the line(s) together with
        /// the line break, so the caret lands at the end of the line above in one key press.
        /// Returns true when the key was handled.
        /// </summary>
        private bool TryDeleteWholeLineSelection(VirtualKey key)
        {
            if (key != VirtualKey.Back && key != VirtualKey.Delete) return false;
            if (!IsEnabled || IsReadOnly) return false;

            var window = Window.Current?.CoreWindow;
            if (window != null &&
                (window.GetKeyState(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down) ||
                 window.GetKeyState(VirtualKey.Menu).HasFlag(CoreVirtualKeyStates.Down) ||
                 window.GetKeyState(VirtualKey.Shift).HasFlag(CoreVirtualKeyStates.Down)))
            {
                return false;
            }

            try
            {
                Document.GetText(TextGetOptions.None, out var raw);
                var text = TrimRichEditBoxText(raw);

                var start = Math.Min(Document.Selection.StartPosition, Document.Selection.EndPosition);
                var end = Math.Max(Document.Selection.StartPosition, Document.Selection.EndPosition);
                if (end > text.Length) end = text.Length;
                if (start < 0 || start >= end) return false;

                var startsAtLineStart = start == 0 || text[start - 1] == RichEditBoxDefaultLineEnding;
                var endsAtLineEnd = end == text.Length || text[end] == RichEditBoxDefaultLineEnding;
                if (!startsAtLineStart || !endsAtLineEnd) return false;

                int deleteStart, deleteEnd;
                if (start > 0)
                {
                    // Take the line break before the selection: caret goes to the end of the line above
                    deleteStart = start - 1;
                    deleteEnd = end;
                }
                else if (end < text.Length)
                {
                    // First line: take the line break after it instead
                    deleteStart = start;
                    deleteEnd = end + 1;
                }
                else
                {
                    return false; // Whole document selected - default behaviour
                }

                Document.Selection.SetRange(deleteStart, deleteEnd);
                Document.Selection.SetText(TextSetOptions.None, string.Empty);
                Document.Selection.SetRange(deleteStart, deleteStart);
                return true;
            }
            catch (Exception ex)
            {
                LoggingService.LogError($"[{nameof(TextEditorCore)}] Failed to delete whole line selection: {ex}");
                return false;
            }
        }

        /// <summary>
        /// Removes trailing lines at the end of the document that are empty or contain only
        /// spaces / tabs. The first line is never removed. Returns true when something changed.
        /// </summary>
        public bool TrimTrailingBlankLines()
        {
            try
            {
                Document.GetText(TextGetOptions.None, out var raw);
                var text = TrimRichEditBoxText(raw);

                var newLength = text.Length;
                var i = text.Length;
                while (true)
                {
                    var j = i;
                    while (j > 0 && (text[j - 1] == ' ' || text[j - 1] == '\t')) j--;
                    if (j > 0 && text[j - 1] == RichEditBoxDefaultLineEnding)
                    {
                        i = j - 1;
                        newLength = i;
                    }
                    else
                    {
                        break;
                    }
                }

                if (newLength >= text.Length) return false;

                var selStart = Math.Min(Document.Selection.StartPosition, newLength);
                var selEnd = Math.Min(Document.Selection.EndPosition, newLength);

                Document.GetRange(newLength, text.Length).SetText(TextSetOptions.None, string.Empty);
                Document.Selection.SetRange(selStart, selEnd);

                Document.GetText(TextGetOptions.None, out var updated);
                _document = TrimRichEditBoxText(updated);
                _isDocumentLinesCachePendingUpdate = true;
                return true;
            }
            catch (Exception ex)
            {
                LoggingService.LogError($"[{nameof(TextEditorCore)}] Failed to trim trailing blank lines: {ex}");
                AnalyticsService.TrackEvent("TextEditorCore_FailedToTrimTrailingBlankLines",
                    new Dictionary<string, string> { { "Exception", ex.ToString() } });
                return false;
            }
        }
    }
}
