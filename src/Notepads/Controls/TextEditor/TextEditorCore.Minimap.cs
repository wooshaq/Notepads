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
    using Windows.UI.Xaml.Media;
    using Windows.System;

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

        // "Scroll past end": RichEditBox does not let us enlarge its scroll extent, so the extra
        // space is virtual: once the real ScrollViewer is at the bottom, the text area, line numbers
        // and line highlighter are translated up by _pastEndOffset.
        private double _pastEndOffset;
        private readonly TranslateTransform _pastEndTextTransform = new TranslateTransform();
        private readonly TranslateTransform _pastEndLineNumberTransform = new TranslateTransform();
        private readonly TranslateTransform _pastEndHighlighterTransform = new TranslateTransform();
        private UIElement _contentScrollViewerContent;

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

            if (ScrollPastEndEnabled)
            {
                if (VisualTreeHelper.GetParent(_contentScrollViewer) is UIElement textHost)
                {
                    textHost.RenderTransform = _pastEndTextTransform;
                }
                _lineNumberGrid.RenderTransform = _pastEndLineNumberTransform;
                _lineHighlighterAndIndicatorCanvas.RenderTransform = _pastEndHighlighterTransform;

                // Wheel over the text: catch it before the ScrollViewer does (bubbling order)
                // Wheel over the empty "past end" area: comes to the editor itself
                AddHandler(PointerWheelChangedEvent, new PointerEventHandler(OnEditorPointerWheelForPastEnd), false);
            }

            ActualThemeChanged += OnActualThemeChangedForMinimap;
        }

        private void HookContentWheelHandlerForPastEnd()
        {
            if (!ScrollPastEndEnabled || _contentScrollViewer == null) return;
            if (!(_contentScrollViewer.Content is UIElement content) || ReferenceEquals(content, _contentScrollViewerContent)) return;

            _contentScrollViewerContent?.RemoveHandler(PointerWheelChangedEvent, new PointerEventHandler(OnContentPointerWheelForPastEnd));
            _contentScrollViewerContent = content;
            _contentScrollViewerContent.AddHandler(PointerWheelChangedEvent, new PointerEventHandler(OnContentPointerWheelForPastEnd), false);
        }

        private void DisposeSublimeLikeFeatures()
        {
            ActualThemeChanged -= OnActualThemeChangedForMinimap;

            if (ScrollPastEndEnabled)
            {
                RemoveHandler(PointerWheelChangedEvent, new PointerEventHandler(OnEditorPointerWheelForPastEnd));
                _contentScrollViewerContent?.RemoveHandler(PointerWheelChangedEvent, new PointerEventHandler(OnContentPointerWheelForPastEnd));
                _contentScrollViewerContent = null;
            }

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

        private double GetMaxPastEndOffset()
        {
            if (!ScrollPastEndEnabled || _contentScrollViewer == null) return 0;
            var sv = _contentScrollViewer;
            // Text fits in the window -> nothing to scroll at all (like Sublime)
            if (sv.ScrollableHeight <= 0.5) return 0;
            // Last line may go up to the very top of the viewport
            var visibleTextPart = sv.ExtentHeight - Math.Max(0, sv.ScrollableHeight);
            return Math.Max(0, visibleTextPart - GetSingleLineHeight());
        }

        private double GetEffectiveVerticalOffset() => _contentScrollViewer.VerticalOffset + _pastEndOffset;
        private double GetEffectiveScrollableHeight() => Math.Max(0, _contentScrollViewer.ScrollableHeight) + GetMaxPastEndOffset();
        private double GetEffectiveExtentHeight() => _contentScrollViewer.ExtentHeight + GetMaxPastEndOffset();

        private void SetPastEndOffset(double value)
        {
            value = Math.Min(Math.Max(0, value), GetMaxPastEndOffset());
            if (Math.Abs(value - _pastEndOffset) < 0.01) return;

            _pastEndOffset = value;
            _pastEndTextTransform.Y = -value;
            _pastEndLineNumberTransform.Y = -value;
            _pastEndHighlighterTransform.Y = -value;

            SyncEditorVerticalScrollBar();
            _minimapCanvas?.Invalidate();
        }

        /// <summary>Re-clamp after text / size changes; keep caret visible.</summary>
        private void UpdateScrollPastEndSpace()
        {
            if (!ScrollPastEndEnabled || _contentScrollViewer == null) return;
            HookContentWheelHandlerForPastEnd();
            SetPastEndOffset(_pastEndOffset);
        }

        private bool _normalizingPastEnd;

        /// <summary>
        /// The virtual past-end space only works while the real ScrollViewer sits at its bottom:
        /// the text is moved up by _pastEndOffset, and the RichEditBox draws nothing below its own
        /// viewport. Windows can scroll the ScrollViewer itself (smooth wheel, touchpad, scroll bar
        /// of the RichEditBox) while the text is still moved up, which leaves an empty band at the
        /// bottom with the text cut off above it. Fold the virtual space back so the same lines stay
        /// on screen: scrolling back up uses up the virtual space first, then scrolls the text.
        /// </summary>
        private void NormalizePastEndAfterScroll()
        {
            if (!ScrollPastEndEnabled || _normalizingPastEnd || _pastEndOffset <= 0 || _contentScrollViewer == null) return;

            var sv = _contentScrollViewer;
            var scrollable = Math.Max(0, sv.ScrollableHeight);
            if (sv.VerticalOffset >= scrollable - 0.5) return;

            var effective = sv.VerticalOffset + _pastEndOffset;
            _normalizingPastEnd = true;
            try
            {
                if (effective <= scrollable)
                {
                    SetPastEndOffset(0);
                    sv.ChangeView(null, effective, null, true);
                }
                else
                {
                    sv.ChangeView(null, scrollable, null, true);
                    SetPastEndOffset(effective - scrollable);
                }
            }
            finally
            {
                _normalizingPastEnd = false;
            }
        }

        private void EnsureCaretVisibleWithPastEnd()
        {
            if (!ScrollPastEndEnabled || _pastEndOffset <= 0 || _contentScrollViewer == null) return;

            // RichEditBox scrolled the caret into ITS viewport; the virtual offset may still hide it above
            Document.Selection.GetRect(Windows.UI.Text.PointOptions.ClientCoordinates, out var rect, out _);
            var caretTopInViewport = rect.Y - _contentScrollViewer.VerticalOffset;
            if (caretTopInViewport < _pastEndOffset)
            {
                SetPastEndOffset(Math.Max(0, caretTopInViewport));
            }
        }

        /// <summary>Scroll to an offset in "effective" space (real offset + virtual past-end space).</summary>
        private void ScrollToEffectiveOffset(double target)
        {
            var sv = _contentScrollViewer;
            if (sv == null) return;

            var scrollable = Math.Max(0, sv.ScrollableHeight);
            target = Math.Min(Math.Max(0, target), GetEffectiveScrollableHeight());

            if (target <= scrollable)
            {
                SetPastEndOffset(0);
                sv.ChangeView(null, target, null, true);
            }
            else
            {
                if (sv.VerticalOffset < scrollable - 0.5) sv.ChangeView(null, scrollable, null, true);
                SetPastEndOffset(target - scrollable);
            }
        }

        private bool IsAtRealBottom()
        {
            var sv = _contentScrollViewer;
            var scrollable = Math.Max(0, sv.ScrollableHeight);
            // _contentScrollViewerVerticalOffset = final view of a running scroll animation
            return sv.VerticalOffset >= scrollable - 1 && _contentScrollViewerVerticalOffset >= scrollable - 1;
        }

        /// <summary>Returns true when the wheel step was used for the virtual past-end space.</summary>
        private bool TryHandleWheelForPastEnd(int wheelDelta)
        {
            if (!ScrollPastEndEnabled || _contentScrollViewer == null || wheelDelta == 0) return false;

            var step = GetSingleLineHeight() * 3 * Math.Abs(wheelDelta) / 120.0;

            if (wheelDelta < 0) // down
            {
                if (!IsAtRealBottom() || _pastEndOffset >= GetMaxPastEndOffset() - 0.01) return false;
                SetPastEndOffset(_pastEndOffset + step);
                return true;
            }

            if (_pastEndOffset > 0) // up, first eat the virtual space
            {
                SetPastEndOffset(_pastEndOffset - step);
                return true;
            }

            return false;
        }

        private static bool HasWheelModifiers(PointerRoutedEventArgs e)
        {
            return (e.KeyModifiers & (VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift | VirtualKeyModifiers.Menu)) != 0;
        }

        // Wheel over the text area (fires before the ScrollViewer handles it)
        private void OnContentPointerWheelForPastEnd(object sender, PointerRoutedEventArgs e)
        {
            if (HasWheelModifiers(e)) return;
            var delta = e.GetCurrentPoint((UIElement)sender).Properties.MouseWheelDelta;
            if (TryHandleWheelForPastEnd(delta)) e.Handled = true;
        }

        // Wheel over the empty area below the text (not over the ScrollViewer)
        private void OnEditorPointerWheelForPastEnd(object sender, PointerRoutedEventArgs e)
        {
            if (HasWheelModifiers(e) || _contentScrollViewer == null) return;
            var delta = e.GetCurrentPoint(this).Properties.MouseWheelDelta;
            if (!TryHandleWheelForPastEnd(delta))
            {
                var step = GetSingleLineHeight() * 3 * delta / 120.0;
                ScrollToEffectiveOffset(GetEffectiveVerticalOffset() - step);
            }
            e.Handled = true;
        }

        #endregion

        #region Always visible scroll bar

        private void SyncEditorVerticalScrollBar()
        {
            if (!AlwaysVisibleScrollBarEnabled || _editorVerticalScrollBar == null) return;

            // The scroll bar covers the real text only: at the end of the file the thumb is at the
            // bottom and stays there while scrolling further into the virtual past-end space.
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

            ScrollToEffectiveOffset(e.NewValue);
        }

        private void OnSidePanelPointerWheelChanged(object sender, PointerRoutedEventArgs e)
        {
            if (_contentScrollViewer == null) return;

            var delta = e.GetCurrentPoint((UIElement)sender).Properties.MouseWheelDelta;
            if (!TryHandleWheelForPastEnd(delta))
            {
                var step = GetSingleLineHeight() * 3 * delta / 120.0;
                ScrollToEffectiveOffset(GetEffectiveVerticalOffset() - step);
            }
            e.Handled = true;
        }

        #endregion

        #region Minimap

        private MinimapGeometry GetMinimapGeometry(int lineCount)
        {
            var sv = _contentScrollViewer;
            var controlHeight = _minimapCanvas?.ActualHeight ?? 0;

            // Map the real text height onto exactly lineCount minimap rows. Using the nominal single
            // line height here made the minimap taller than its rows whenever the editor's actual
            // line height differs (line spacing), so at the end of the file the rows ran out above
            // the bottom of the minimap and it looked scrolled past its end.
            var textHeight = Math.Max(1, sv.ExtentHeight - Padding.Top - Padding.Bottom);
            var scale = (MinimapLineHeight * Math.Max(1, lineCount)) / textHeight;

            // Minimap itself maps the REAL document; the virtual past-end space only moves the
            // viewport box further down (it slides out below the minimap, like in Sublime).
            var totalMinimapHeight = sv.ExtentHeight * scale;
            var boxHeight = sv.ViewportHeight * scale;
            var scrollable = Math.Max(0, sv.ScrollableHeight);
            var fits = totalMinimapHeight <= controlHeight || scrollable <= 0;

            var offset = fits ? 0 : (Math.Min(sv.VerticalOffset, scrollable) / scrollable) * (totalMinimapHeight - controlHeight);

            return new MinimapGeometry
            {
                Scale = scale,
                Offset = offset,
                BoxTop = (sv.VerticalOffset + _pastEndOffset) * scale - offset,
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
            return Math.Max(0, _contentScrollViewer.ScrollableHeight) / free;
        }

        private double ClampVerticalOffset(double offset)
        {
            return Math.Min(Math.Max(0, offset), GetEffectiveScrollableHeight());
        }

        private void OnMinimapDraw(CanvasControl sender, CanvasDrawEventArgs args)
        {
            if (!_loaded || _contentScrollViewer == null) return;

            var width = sender.ActualWidth;
            var height = sender.ActualHeight;
            if (width <= 0 || height <= 0) return;

            // Like requested: show the minimap only when the text is taller than the window
            if (_contentScrollViewer.ScrollableHeight <= 0.5) return;

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
            var rowHeight = MinimapLineHeight;
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
            if (_contentScrollViewer.ScrollableHeight <= 0.5) return; // minimap hidden

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
                ScrollToEffectiveOffset(target);
                _minimapDragStartVerticalOffset = target;
            }
            else
            {
                _minimapDragStartVerticalOffset = GetEffectiveVerticalOffset();
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

            ScrollToEffectiveOffset(target);
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
