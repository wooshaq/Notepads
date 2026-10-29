// ---------------------------------------------------------------------------------------------
//  Sublime Pad (personal build of Notepads): whole-line backgrounds for lines with keywords,
//  in the editor and as marks on the minimap.
//  See LICENSE file in the project root for license information.
// ---------------------------------------------------------------------------------------------

namespace Notepads.Controls.TextEditor
{
    using System;
    using System.Collections.Generic;
    using Notepads.Services;
    using Windows.Foundation;
    using Windows.UI;
    using Windows.UI.Text;
    using Windows.UI.Xaml;
    using Windows.UI.Xaml.Controls;
    using Windows.UI.Xaml.Media;

    public partial class TextEditorCore
    {
        // Drawn in the line highlighter canvas (behind the text, scrolled with it), reused between
        // updates; only lines inside the viewport get a rectangle.
        private readonly List<Border> _keywordHighlightBlocks = new List<Border>();

        // Whole-document scan: line index (0-based) -> colour. Used by the minimap. The visible
        // lines in the editor are always matched directly, so they are exact even while a rescan
        // after typing is still pending.
        private readonly Dictionary<int, Color> _keywordLineColors = new Dictionary<int, Color>();
        private string[] _keywordScannedLines;
        private bool _keywordScanDirty = true;
        private DispatcherTimer _keywordRescanTimer;

        private void InitializeKeywordHighlight()
        {
            KeywordHighlightService.OnRulesChanged += OnKeywordHighlightRulesChanged;
            _keywordRescanTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _keywordRescanTimer.Tick += OnKeywordRescanTimerTick;
        }

        private void DisposeKeywordHighlight()
        {
            KeywordHighlightService.OnRulesChanged -= OnKeywordHighlightRulesChanged;
            if (_keywordRescanTimer != null)
            {
                _keywordRescanTimer.Stop();
                _keywordRescanTimer.Tick -= OnKeywordRescanTimerTick;
                _keywordRescanTimer = null;
            }
        }

        private async void OnKeywordHighlightRulesChanged(object sender, EventArgs e)
        {
            await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
            {
                _keywordScanDirty = true;
                RescanKeywordHighlights();
                UpdateKeywordHighlights();
            });
        }

        private void OnKeywordRescanTimerTick(object sender, object e)
        {
            _keywordRescanTimer.Stop();
            RescanKeywordHighlights();
        }

        /// <summary>Scan the whole document now (if it changed) and redraw the minimap.</summary>
        private void RescanKeywordHighlights()
        {
            if (!_loaded) return;

            var lines = GetDocumentLinesCache();
            if (!_keywordScanDirty && ReferenceEquals(lines, _keywordScannedLines)) return;

            _keywordLineColors.Clear();
            if (KeywordHighlightService.HasRules)
            {
                for (int i = 0; i < lines.Length - 1; i++)
                {
                    if (KeywordHighlightService.TryMatch(lines[i], out var color))
                        _keywordLineColors[i] = color;
                }
            }

            _keywordScannedLines = lines;
            _keywordScanDirty = false;
            _minimapCanvas?.Invalidate();
        }

        /// <summary>The document changed: the first scan runs at once, later ones after a short pause.</summary>
        private void ScheduleKeywordRescan()
        {
            if (_keywordScannedLines == null)
            {
                RescanKeywordHighlights();
                return;
            }

            if (_keywordRescanTimer == null || !KeywordHighlightService.HasRules && _keywordLineColors.Count == 0) return;
            _keywordRescanTimer.Stop();
            _keywordRescanTimer.Start();
        }

        private void UpdateKeywordHighlights()
        {
            if (!_loaded || _contentScrollViewer == null || _lineHighlighterAndIndicatorCanvas == null) return;

            var lines = GetDocumentLinesCache();
            if (!ReferenceEquals(lines, _keywordScannedLines) || _keywordScanDirty) ScheduleKeywordRescan();

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

                        if (!(block.Background is SolidColorBrush brush) || brush.Color != color)
                            block.Background = new SolidColorBrush(color);
                        block.Width = width;
                        block.Height = Math.Max(rect.Height, GetSingleLineHeight());
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

        /// <summary>Minimap marks for keyword lines between first and last (0-based, inclusive).</summary>
        private void DrawKeywordMarksOnMinimap(Microsoft.Graphics.Canvas.CanvasDrawingSession ds,
            int first, int last, double topPadding, double rowHeight, double minimapOffset, double width)
        {
            if (_keywordLineColors.Count == 0) return;

            // Visible even for a single line at minimap scale, and a bit stronger than in the editor
            var markHeight = (float)Math.Max(rowHeight, 2);
            foreach (var (line, color) in _keywordLineColors)
            {
                if (line < first || line > last) continue;
                var y = (float)(topPadding + line * rowHeight - minimapOffset);
                var mark = Color.FromArgb((byte)Math.Max((int)color.A, 0xB0), color.R, color.G, color.B);
                ds.FillRectangle(0, y, (float)width, markHeight, mark);
            }
        }
    }
}
