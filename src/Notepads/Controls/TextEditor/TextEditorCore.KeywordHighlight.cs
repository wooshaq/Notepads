// ---------------------------------------------------------------------------------------------
//  Sublime Pad (personal build of Notepads): whole-line backgrounds for lines with keywords.
//  See LICENSE file in the project root for license information.
// ---------------------------------------------------------------------------------------------

namespace Notepads.Controls.TextEditor
{
    using System;
    using System.Collections.Generic;
    using Notepads.Services;
    using Windows.Foundation;
    using Windows.UI.Text;
    using Windows.UI.Xaml;
    using Windows.UI.Xaml.Controls;
    using Windows.UI.Xaml.Media;

    public partial class TextEditorCore
    {
        // Drawn in the line highlighter canvas (behind the text, scrolled with it), reused between
        // updates; only lines inside the viewport get a rectangle.
        private readonly List<Border> _keywordHighlightBlocks = new List<Border>();

        private void InitializeKeywordHighlight()
        {
            KeywordHighlightService.OnRulesChanged += OnKeywordHighlightRulesChanged;
        }

        private void DisposeKeywordHighlight()
        {
            KeywordHighlightService.OnRulesChanged -= OnKeywordHighlightRulesChanged;
        }

        private async void OnKeywordHighlightRulesChanged(object sender, EventArgs e)
        {
            await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, UpdateKeywordHighlights);
        }

        private void UpdateKeywordHighlights()
        {
            if (!_loaded || _contentScrollViewer == null || _lineHighlighterAndIndicatorCanvas == null) return;

            var used = 0;
            if (KeywordHighlightService.HasRules)
            {
                var startRange = Document.GetRangeFromPoint(
                    new Point(_contentScrollViewer.HorizontalOffset, _contentScrollViewer.VerticalOffset),
                    PointOptions.ClientCoordinates);
                var endRange = Document.GetRangeFromPoint(
                    new Point(_contentScrollViewer.HorizontalOffset + _contentScrollViewer.ViewportWidth,
                        _contentScrollViewer.VerticalOffset + _contentScrollViewer.ViewportHeight),
                    PointOptions.ClientCoordinates);

                var width = Math.Max(0, _rootGrid.ActualWidth - GetRightSidePanelsWidth());
                var lines = GetDocumentLinesCache();
                var offset = 0;

                for (int i = 0; i < lines.Length - 1; i++)
                {
                    var line = lines[i];
                    if (offset > endRange.EndPosition) break;

                    if (offset + line.Length + 1 >= startRange.StartPosition &&
                        KeywordHighlightService.TryMatch(line, out var color))
                    {
                        Document.GetRange(offset, offset + line.Length)
                            .GetRect(PointOptions.ClientCoordinates, out var rect, out _);

                        Border block;
                        if (used < _keywordHighlightBlocks.Count)
                        {
                            block = _keywordHighlightBlocks[used];
                        }
                        else
                        {
                            block = new Border { IsHitTestVisible = false };
                            _keywordHighlightBlocks.Add(block);
                            // Index 0: behind the current line highlighter and indicator
                            _lineHighlighterAndIndicatorCanvas.Children.Insert(0, block);
                        }

                        var height = Math.Max(rect.Height, GetSingleLineHeight());
                        block.Background = new SolidColorBrush(color);
                        block.Width = width;
                        block.Height = height;
                        block.Margin = new Thickness(0, rect.Y + Padding.Top, 0, 0);
                        block.Visibility = Visibility.Visible;
                        used++;
                    }

                    offset += line.Length + 1;
                }
            }

            for (int i = used; i < _keywordHighlightBlocks.Count; i++)
            {
                _keywordHighlightBlocks[i].Visibility = Visibility.Collapsed;
            }
        }
    }
}
