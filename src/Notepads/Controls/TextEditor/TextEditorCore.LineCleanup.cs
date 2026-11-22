// ---------------------------------------------------------------------------------------------
//  Sublime Pad: whole-line delete and trailing blank line cleanup.
// ---------------------------------------------------------------------------------------------

namespace Notepads.Controls.TextEditor
{
    using System;
    using System.Collections.Generic;
    using Windows.Devices.Input;
    using Windows.Foundation;
    using Windows.System;
    using Windows.UI.Core;
    using Windows.UI.Text;
    using Windows.UI.Xaml;
    using Windows.UI.Xaml.Input;
    using Windows.UI.Xaml.Media;
    using Windows.UI.Xaml.Shapes;
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
        // ---------------------------------------------------------------------------------
        // Mouse drag over whole line(s): selection is extended to the end of the line above
        // and a caret is shown there, so Backspace removes the line in one press.
        // ---------------------------------------------------------------------------------

        private bool _isMouseSelecting;
        private int _lineSelectCaretPosition = -1;
        private int _lineSelectEndPosition = -1;
        private Rectangle _lineSelectCaret;
        private DispatcherTimer _lineSelectCaretTimer;

        private void InitializeLineSelect()
        {
            AddHandler(PointerPressedEvent, new PointerEventHandler(OnPointerPressedForLineSelect), true);
            AddHandler(PointerReleasedEvent, new PointerEventHandler(OnPointerReleasedForLineSelect), true);
            AddHandler(PointerCaptureLostEvent, new PointerEventHandler(OnPointerReleasedForLineSelect), true);
            GotFocus += OnFocusChangedForLineSelect;
            LostFocus += OnFocusChangedForLineSelect;
        }

        private void DisposeLineSelect()
        {
            RemoveHandler(PointerPressedEvent, new PointerEventHandler(OnPointerPressedForLineSelect));
            RemoveHandler(PointerReleasedEvent, new PointerEventHandler(OnPointerReleasedForLineSelect));
            RemoveHandler(PointerCaptureLostEvent, new PointerEventHandler(OnPointerReleasedForLineSelect));
            GotFocus -= OnFocusChangedForLineSelect;
            LostFocus -= OnFocusChangedForLineSelect;

            if (_lineSelectCaretTimer != null)
            {
                _lineSelectCaretTimer.Stop();
                _lineSelectCaretTimer.Tick -= OnLineSelectCaretTick;
                _lineSelectCaretTimer = null;
            }

            if (_lineSelectCaret != null)
            {
                _lineHighlighterAndIndicatorCanvas?.Children.Remove(_lineSelectCaret);
                _lineSelectCaret = null;
            }
        }

        private void OnPointerPressedForLineSelect(object sender, PointerRoutedEventArgs args)
        {
            var point = args.GetCurrentPoint(this);
            _isMouseSelecting = args.Pointer.PointerDeviceType == PointerDeviceType.Mouse &&
                                point.Properties.IsLeftButtonPressed;
        }

        private void OnPointerReleasedForLineSelect(object sender, PointerRoutedEventArgs args)
        {
            if (!_isMouseSelecting) return;
            _isMouseSelecting = false;

            try
            {
                ExtendWholeLineSelectionToLineAbove();
            }
            catch (Exception ex)
            {
                LoggingService.LogError($"[{nameof(TextEditorCore)}] Failed to extend whole line selection: {ex}");
            }
        }

        private void ExtendWholeLineSelectionToLineAbove()
        {
            Document.GetText(TextGetOptions.None, out var raw);
            var text = TrimRichEditBoxText(raw);

            var start = Math.Min(Document.Selection.StartPosition, Document.Selection.EndPosition);
            var end = Math.Max(Document.Selection.StartPosition, Document.Selection.EndPosition);
            if (end > text.Length) end = text.Length;
            if (start <= 0 || start >= end) return; // first line or empty selection: nothing to do

            var startsAtLineStart = text[start - 1] == RichEditBoxDefaultLineEnding;
            var endsAtLineEnd = end == text.Length || text[end] == RichEditBoxDefaultLineEnding;
            if (!startsAtLineStart || !endsAtLineEnd) return;

            // Anchor at the end of the selected line(s), active end (caret) at the end of the line above
            _lineSelectCaretPosition = start - 1;
            _lineSelectEndPosition = end;
            Document.Selection.SetRange(end, start - 1);
            UpdateLineSelectCaret();
        }

        private void OnFocusChangedForLineSelect(object sender, RoutedEventArgs _)
        {
            UpdateLineSelectCaret();
        }

        /// <summary>
        /// Shows a caret at the end of the line above while the extended whole-line selection is active
        /// (RichEditBox does not draw its own caret while text is selected).
        /// </summary>
        private void UpdateLineSelectCaret()
        {
            if (!_loaded || _lineHighlighterAndIndicatorCanvas == null) return;

            var start = Math.Min(Document.Selection.StartPosition, Document.Selection.EndPosition);
            var end = Math.Max(Document.Selection.StartPosition, Document.Selection.EndPosition);

            var active = _lineSelectCaretPosition >= 0 &&
                         start == _lineSelectCaretPosition &&
                         end == _lineSelectEndPosition &&
                         FocusState != FocusState.Unfocused;

            if (!active)
            {
                if (start != _lineSelectCaretPosition || end != _lineSelectEndPosition)
                {
                    _lineSelectCaretPosition = -1;
                    _lineSelectEndPosition = -1;
                }

                if (_lineSelectCaret != null) _lineSelectCaret.Visibility = Visibility.Collapsed;
                _lineSelectCaretTimer?.Stop();
                return;
            }

            if (_lineSelectCaret == null)
            {
                _lineSelectCaret = new Rectangle { IsHitTestVisible = false, Width = 2 };
                _lineHighlighterAndIndicatorCanvas.Children.Add(_lineSelectCaret);
            }

            if (_lineSelectCaretTimer == null)
            {
                _lineSelectCaretTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(530) };
                _lineSelectCaretTimer.Tick += OnLineSelectCaretTick;
            }

            Document.GetRange(_lineSelectCaretPosition, _lineSelectCaretPosition)
                .GetRect(PointOptions.ClientCoordinates, out var rect, out _);

            var contentOrigin = _contentScrollViewer
                .TransformToVisual(_lineHighlighterAndIndicatorCanvas)
                .TransformPoint(new Point(0, 0));

            _lineSelectCaret.Fill = Foreground ?? new SolidColorBrush(Windows.UI.Colors.Gray);
            _lineSelectCaret.Height = Math.Max(rect.Height, GetSingleLineHeight());
            _lineSelectCaret.Margin = new Thickness(
                // 2 px to the left: the selection highlight of the line break would cover it otherwise
                contentOrigin.X + Padding.Left + rect.X - _contentScrollViewer.HorizontalOffset - 2,
                rect.Y + Padding.Top,
                0,
                0);
            _lineSelectCaret.Visibility = Visibility.Visible;
            _lineSelectCaretTimer.Stop();
            _lineSelectCaretTimer.Start();
        }

        private void OnLineSelectCaretTick(object sender, object _)
        {
            if (_lineSelectCaret == null) return;
            _lineSelectCaret.Visibility = _lineSelectCaret.Visibility == Visibility.Visible
                ? Visibility.Collapsed
                : Visibility.Visible;
        }
    }
}
