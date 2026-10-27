// ---------------------------------------------------------------------------------------------
//  Sublime Text-like additions for Notepads (personal build):
//   - minimap on the right side (click / drag / mouse wheel)
//   - always-visible vertical scroll bar (does not auto-hide)
//   - "scroll past end": the last line can be scrolled up to the top of the viewport
//  See LICENSE file in the project root for license information.
// ---------------------------------------------------------------------------------------------

namespace Notepads.Controls.TextEditor
{
    using System;
    using System.Numerics;
    using System.Text;
    using Microsoft.Graphics.Canvas.Text;
    using Microsoft.Graphics.Canvas.UI.Xaml;
    using Windows.UI;
    using Windows.UI.Xaml;
    using Windows.UI.Xaml.Controls;
    using Windows.UI.Xaml.Controls.Primitives;
    using Windows.UI.Xaml.Input;

    [TemplatePart(Name = MinimapCanvasName, Type = typeof(CanvasControl))]
    [TemplatePart(Name = EditorVerticalScrollBarName, Type = typeof(ScrollBar))]
    public partial class TextEditorCore
    {
        // ------------------------------------------------------------------
        // Switches - change and rebuild
        // ------------------------------------------------------------------
        private static readonly bool MinimapEnabled = true;
        private static readonly bool AlwaysVisibleScrollBarEnabled = true;
        private static readonly bool ScrollPastEndEnabled = true;

        private const double MinimapWidth = 110;        // DIPs
        private const double MinimapLineHeight = 2.5;   // DIPs per document line in minimap (Sublime ~2-3)
        private const float MinimapFontSize = 2.5f;     // glyph size inside the minimap
        private const int MinimapMaxCharsPerLine = 160;
        private const int MinimapTabSize = 4;

        // ------------------------------------------------------------------

        private const string MinimapCanvasName = "MinimapCanvas";
        private CanvasControl _minimapCanvas;
        private const string EditorVerticalScrollBarName = "EditorVerticalScrollBar";
        private ScrollBar _editorVerticalScrollBar;

        private CanvasTextFormat _minimapTextFormat;
        private bool _isMinimapPointerOver;
        private bool _isMinimapDragging;
        private double _minimapDragStartPointerY;
        private double _minimapDragStartVerticalOffset;
        private bool _isScrollBarThumbDragging;

        private double _scrollPastEndExtraSpace = -1;

        private struct MinimapGeometry
        {
            public double Scale;         // minimap px per editor px
            public double Offset;        // how far the minimap itself is scrolled (minimap px)
            public double BoxTop;        // viewport box top in minimap control coordinates
            public double BoxHeight;
            public bool ContentFits;     // whole document fits in the minimap height
            public double ControlHeight;
        }

        private void InitializeSublimeLikeFeatures()
        {
            _minimapCanvas = GetTemplateChild(MinimapCanvasName) as CanvasControl;
            _editorVerticalScrollBar = GetTemplateChild(EditorVerticalScrollBarName) as ScrollBar;

            if (_minimapCanvas != null)
            {
                if (MinimapEnabled)
                {
                    _minimapCanvas.Width = MinimapWidth;
                    _minimapCanvas.Draw += OnMinimapDraw;
                    _minimapCanvas.PointerPressed += OnMinimapPointerPressed;
                    _minimapCanvas.PointerMoved += OnMinimapPointerMoved;
                    _minimapCanvas.PointerReleased += OnMinimapPointerReleased;
                    _minimapCanvas.PointerCaptureLost += OnMinimapPointerCaptureLost;
                    _minimapCanvas.PointerEntered += OnMinimapPointerEntered;
                    _minimapCanvas.PointerExited += OnMinimapPointerExited;
                    _minimapCanvas.PointerWheelChanged += OnSidePanelPointerWheelChanged;
                    _minimapCanvas.SizeChanged += OnMinimapSizeChanged;
                }
                else
                {
                    _minimapCanvas.Visibility = Visibility.Collapsed;
                }
            }

            if (_editorVerticalScrollBar != null)
            {
                if (AlwaysVisibleScrollBarEnabled)
                {
                    _editorVerticalScrollBar.Scroll += OnEditorVerticalScrollBarScroll;
                    _editorVerticalScrollBar.PointerWheelChanged += OnSidePanelPointerWheelChanged;
                }
                else
                {
                    _editorVerticalScrollBar.Visibility = Visibility.Collapsed;
                    _contentScrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
                }
            }

            ActualThemeChanged += OnActualThemeChangedForMinimap;
        }

        private void DisposeSublimeLikeFeatures()
        {
            ActualThemeChanged -= OnActualThemeChangedForMinimap;

            if (_minimapCanvas != null)
            {
                _minimapCanvas.Draw -= OnMinimapDraw;
                _minimapCanvas.PointerPressed -= OnMinimapPointerPressed;
                _minimapCanvas.PointerMoved -= OnMinimapPointerMoved;
                _minimapCanvas.PointerReleased -= OnMinimapPointerReleased;
                _minimapCanvas.PointerCaptureLost -= OnMinimapPointerCaptureLost;
                _minimapCanvas.PointerEntered -= OnMinimapPointerEntered;
                _minimapCanvas.PointerExited -= OnMinimapPointerExited;
                _minimapCanvas.PointerWheelChanged -= OnSidePanelPointerWheelChanged;
                _minimapCanvas.SizeChanged -= OnMinimapSizeChanged;
                // Win2D requirement: release device resources when control goes away
                _minimapCanvas.RemoveFromVisualTree();
                _minimapCanvas = null;
            }

            if (_editorVerticalScrollBar != null)
            {
                _editorVerticalScrollBar.Scroll -= OnEditorVerticalScrollBarScroll;
                _editorVerticalScrollBar.PointerWheelChanged -= OnSidePanelPointerWheelChanged;
            }

            _minimapTextFormat?.Dispose();
            _minimapTextFormat = null;
        }

        /// <summary>
        /// Width taken on the right side by the minimap + scroll bar (used by line highlighter).
        /// </summary>
        private double GetRightSidePanelsWidth()
        {
            double width = 0;
            if (_minimapCanvas != null && _minimapCanvas.Visibility == Visibility.Visible) width += _minimapCanvas.ActualWidth;
            if (_editorVerticalScrollBar != null && _editorVerticalScrollBar.Visibility == Visibility.Visible) width += _editorVerticalScrollBar.ActualWidth;
            return width;
        }

        /// <summary>
        /// Call whenever scroll position, viewport, text or font changes.
        /// </summary>
        private void RefreshSublimeLikeFeatures(bool updateScrollPastEnd = false)
        {
            if (!_loaded || _contentScrollViewer == null) return;

            if (updateScrollPastEnd) UpdateScrollPastEndSpace();
            SyncEditorVerticalScrollBar();
            _minimapCanvas?.Invalidate();
        }

        #region Scroll past end

        private void UpdateScrollPastEndSpace()
        {
            if (!ScrollPastEndEnabled || _contentScrollViewer == null) return;

            // RichEditBox puts its internal text view as the ScrollViewer content.
            // A bottom margin on it enlarges the scrollable extent, so the last line can reach the top.
            if (!(_contentScrollViewer.Content is FrameworkElement textView)) return;

            var extra = Math.Max(0, _contentScrollViewer.ViewportHeight - GetSingleLineHeight());
            if (Math.Abs(extra - _scrollPastEndExtraSpace) < 0.5) return;

            _scrollPastEndExtraSpace = extra;
            var margin = textView.Margin;
            textView.Margin = new Thickness(margin.Left, margin.Top, margin.Right, extra);
        }

        private double GetScrollPastEndSpace()
        {
            return ScrollPastEndEnabled && _scrollPastEndExtraSpace > 0 ? _scrollPastEndExtraSpace : 0;
        }

        #endregion

        #region Always visible scroll bar

        private void SyncEditorVerticalScrollBar()
        {
            if (!AlwaysVisibleScrollBarEnabled || _editorVerticalScrollBar == null) return;

            var sv = _contentScrollViewer;
            var max = Math.Max(0, sv.ScrollableHeight);
            var lineHeight = GetSingleLineHeight();

            _editorVerticalScrollBar.Minimum = 0;
            _editorVerticalScrollBar.Maximum = max;
            _editorVerticalScrollBar.ViewportSize = Math.Max(1, sv.ViewportHeight);
            _editorVerticalScrollBar.SmallChange = lineHeight;
            _editorVerticalScrollBar.LargeChange = Math.Max(lineHeight, sv.ViewportHeight - lineHeight);
            _editorVerticalScrollBar.IsEnabled = max > 0.5;

            if (!_isScrollBarThumbDragging)
            {
                _editorVerticalScrollBar.Value = Math.Min(max, Math.Max(0, sv.VerticalOffset));
            }
        }

        private void OnEditorVerticalScrollBarScroll(object sender, ScrollEventArgs e)
        {
            if (_contentScrollViewer == null) return;

            _isScrollBarThumbDragging = e.ScrollEventType == ScrollEventType.ThumbTrack;
            if (e.ScrollEventType == ScrollEventType.EndScroll) _isScrollBarThumbDragging = false;

            _contentScrollViewer.ChangeView(null, e.NewValue, null, true);
        }

        private void OnSidePanelPointerWheelChanged(object sender, PointerRoutedEventArgs e)
        {
            if (_contentScrollViewer == null) return;

            var delta = e.GetCurrentPoint((UIElement)sender).Properties.MouseWheelDelta;
            var step = GetSingleLineHeight() * 3 * delta / 120.0;
            _contentScrollViewer.ChangeView(null, _contentScrollViewer.VerticalOffset - step, null, false);
            e.Handled = true;
        }

        #endregion

        #region Minimap

        private MinimapGeometry GetMinimapGeometry(int lineCount)
        {
            var sv = _contentScrollViewer;
            var controlHeight = _minimapCanvas?.ActualHeight ?? 0;

            var lineHeight = GetSingleLineHeight();
            var textHeight = Math.Max(1, sv.ExtentHeight - GetScrollPastEndSpace());

            // No wrap: exact line mapping. Wrap: proportional mapping (logical lines vs. visual height).
            var scale = (TextWrapping == TextWrapping.NoWrap || lineCount <= 0)
                ? MinimapLineHeight / Math.Max(1, lineHeight)
                : (MinimapLineHeight * lineCount) / textHeight;

            var totalMinimapHeight = sv.ExtentHeight * scale;
            var boxHeight = sv.ViewportHeight * scale;
            var scrollable = Math.Max(0, sv.ScrollableHeight);
            var fits = totalMinimapHeight <= controlHeight || scrollable <= 0;

            var offset = fits ? 0 : (sv.VerticalOffset / scrollable) * (totalMinimapHeight - controlHeight);

            return new MinimapGeometry
            {
                Scale = scale,
                Offset = offset,
                BoxTop = sv.VerticalOffset * scale - offset,
                BoxHeight = boxHeight,
                ContentFits = fits,
                ControlHeight = controlHeight,
            };
        }

        /// <summary>Editor pixels scrolled per one minimap pixel of viewport-box movement.</summary>
        private double GetOffsetPerMinimapPixel(MinimapGeometry g)
        {
            var free = g.ControlHeight - g.BoxHeight;
            if (g.ContentFits || free <= 1) return 1 / Math.Max(0.0001, g.Scale);
            return _contentScrollViewer.ScrollableHeight / free;
        }

        private double ClampVerticalOffset(double offset)
        {
            return Math.Min(Math.Max(0, offset), Math.Max(0, _contentScrollViewer.ScrollableHeight));
        }

        private void OnMinimapDraw(CanvasControl sender, CanvasDrawEventArgs args)
        {
            if (!_loaded || _contentScrollViewer == null) return;

            var width = sender.ActualWidth;
            var height = sender.ActualHeight;
            if (width <= 0 || height <= 0) return;

            var lines = GetDocumentLinesCache();
            var lineCount = Math.Max(1, lines.Length - 1);
            var g = GetMinimapGeometry(lineCount);

            var isDark = ActualTheme == ElementTheme.Dark;
            var textColor = isDark ? Color.FromArgb(0xB4, 0xE6, 0xE6, 0xE6) : Color.FromArgb(0xB4, 0x20, 0x20, 0x20);
            var boxAlpha = (byte)(_isMinimapPointerOver || _isMinimapDragging ? 0x30 : 0x1C);
            var boxColor = isDark ? Color.FromArgb(boxAlpha, 0xFF, 0xFF, 0xFF) : Color.FromArgb(boxAlpha, 0x00, 0x00, 0x00);

            if (_minimapTextFormat == null)
            {
                _minimapTextFormat = new CanvasTextFormat
                {
                    FontFamily = FontFamily?.Source ?? "Consolas",
                    FontSize = MinimapFontSize,
                    WordWrapping = CanvasWordWrapping.NoWrap,
                    VerticalAlignment = CanvasVerticalAlignment.Top,
                };
            }
            else if (FontFamily != null && _minimapTextFormat.FontFamily != FontFamily.Source)
            {
                _minimapTextFormat.FontFamily = FontFamily.Source;
            }

            var ds = args.DrawingSession;
            ds.TextAntialiasing = CanvasTextAntialiasing.Grayscale;

            // Rows in minimap space. Same mapping as the viewport box so they line up.
            var rowHeight = TextWrapping == TextWrapping.NoWrap
                ? MinimapLineHeight
                : g.Scale * Math.Max(1, _contentScrollViewer.ExtentHeight - GetScrollPastEndSpace()) / lineCount;
            var topPadding = Padding.Top * g.Scale;

            var first = Math.Max(0, (int)Math.Floor((g.Offset - topPadding) / rowHeight) - 1);
            var last = Math.Min(lineCount - 1, (int)Math.Ceiling((g.Offset + height) / rowHeight) + 1);
            var builder = new StringBuilder(MinimapMaxCharsPerLine);

            for (var i = first; i <= last; i++)
            {
                var text = PrepareMinimapLine(lines[i], builder);
                if (text == null) continue;

                var y = (float)(topPadding + i * rowHeight - g.Offset);
                ds.DrawText(text, new Vector2(2, y), textColor, _minimapTextFormat);
            }

            // Viewport box (like Sublime's minimap viewport)
            ds.FillRectangle(0, (float)g.BoxTop, (float)width, (float)Math.Max(2, g.BoxHeight), boxColor);
        }

        private static string PrepareMinimapLine(string line, StringBuilder builder)
        {
            if (string.IsNullOrWhiteSpace(line)) return null;

            builder.Clear();
            foreach (var ch in line)
            {
                if (builder.Length >= MinimapMaxCharsPerLine) break;

                if (ch == '\t')
                {
                    var spaces = MinimapTabSize - (builder.Length % MinimapTabSize);
                    builder.Append(' ', spaces);
                }
                else if (!char.IsControl(ch))
                {
                    builder.Append(ch);
                }
            }

            return builder.Length == 0 ? null : builder.ToString();
        }

        private void OnMinimapPointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (_contentScrollViewer == null || _minimapCanvas == null) return;

            var point = e.GetCurrentPoint(_minimapCanvas);
            if (!point.Properties.IsLeftButtonPressed) return;

            var lines = GetDocumentLinesCache();
            var g = GetMinimapGeometry(Math.Max(1, lines.Length - 1));
            var y = point.Position.Y;

            if (y < g.BoxTop || y > g.BoxTop + g.BoxHeight)
            {
                // Click outside the viewport box: center the viewport on the clicked spot
                var target = g.ContentFits
                    ? (y + g.Offset) / Math.Max(0.0001, g.Scale) - _contentScrollViewer.ViewportHeight / 2
                    : (y - g.BoxHeight / 2) * GetOffsetPerMinimapPixel(g);
                target = ClampVerticalOffset(target);
                _contentScrollViewer.ChangeView(null, target, null, true);
                _minimapDragStartVerticalOffset = target;
            }
            else
            {
                _minimapDragStartVerticalOffset = _contentScrollViewer.VerticalOffset;
            }

            _minimapDragStartPointerY = y;
            _isMinimapDragging = true;
            _minimapCanvas.CapturePointer(e.Pointer);
            _minimapCanvas.Invalidate();
            e.Handled = true;
        }

        private void OnMinimapPointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!_isMinimapDragging || _contentScrollViewer == null || _minimapCanvas == null) return;

            var lines = GetDocumentLinesCache();
            var g = GetMinimapGeometry(Math.Max(1, lines.Length - 1));
            var dy = e.GetCurrentPoint(_minimapCanvas).Position.Y - _minimapDragStartPointerY;
            var target = ClampVerticalOffset(_minimapDragStartVerticalOffset + dy * GetOffsetPerMinimapPixel(g));

            _contentScrollViewer.ChangeView(null, target, null, true);
            e.Handled = true;
        }

        private void OnMinimapPointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (!_isMinimapDragging) return;
            _isMinimapDragging = false;
            _minimapCanvas?.ReleasePointerCapture(e.Pointer);
            _minimapCanvas?.Invalidate();
            e.Handled = true;
        }

        private void OnMinimapPointerCaptureLost(object sender, PointerRoutedEventArgs e)
        {
            _isMinimapDragging = false;
            _minimapCanvas?.Invalidate();
        }

        private void OnMinimapPointerEntered(object sender, PointerRoutedEventArgs e)
        {
            _isMinimapPointerOver = true;
            _minimapCanvas?.Invalidate();
        }

        private void OnMinimapPointerExited(object sender, PointerRoutedEventArgs e)
        {
            _isMinimapPointerOver = false;
            _minimapCanvas?.Invalidate();
        }

        private void OnMinimapSizeChanged(object sender, SizeChangedEventArgs e)
        {
            _minimapCanvas?.Invalidate();
        }

        private void OnActualThemeChangedForMinimap(FrameworkElement sender, object args)
        {
            _minimapCanvas?.Invalidate();
        }

        #endregion
    }
}
