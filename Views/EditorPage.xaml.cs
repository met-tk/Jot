using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Windows.System;
using Windows.UI.Core;
using Journal.Common;
using Journal.Controls;
using Journal.Models;
using Journal.Services;
using Journal.ViewModels;

namespace Journal.Views
{
    public sealed partial class EditorPage : Page
    {
        public EditorViewModel ViewModel { get; } = new();

        private readonly PlatenHapticController _platenHaptics;
        private readonly EditorUndoRedoManager _undoManager = new();

        // 字迹遮罩直接采样 RichEditBox 的实际像素，避免第二套文字排版。
        private InkWeatheringRenderer? _inkRenderer;
        private string? _currentInkTexturePath;
        private ScrollViewer? _editorInnerScrollViewer;
        private double _lastInkAgingIntensity = -1.0;
        private Windows.UI.Color? _lastAppliedInkColor;
        private int _inkUpdateVersion;
        private int _dateLoadVersion;
        private readonly SolidColorBrush? _nativeSelectionHighlightColor;
        private readonly SolidColorBrush? _nativeSelectionHighlightColorWhenNotFocused;
        private readonly SolidColorBrush _editorInkBrush;
        private SolidColorBrush? _agedSelectionHighlightColor;
        private bool _isUsingAgedSelectionHighlight;

        // 文本模型双向同步防重入标志
        private bool _isUpdatingEditorFromModel;
        private bool _isUpdatingModelFromEditor;
        private bool _isPerformingUndoRedo;
        private bool _isComposing;
        private bool _wasComposingAtKeyDownStart;
        private long _lastKeyDownSoundTicks;
        private long _lastCompositionSoundTicks;

        // 页内查找状态
        private readonly List<int> _findMatchPositions = new();
        private int _currentFindMatchIndex = -1;
        private string? _pendingSearchKeyword;

        public EditorPage()
        {
            this.InitializeComponent();
            this.NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Required;
            _nativeSelectionHighlightColor = ContentEditorBox.SelectionHighlightColor;
            _nativeSelectionHighlightColorWhenNotFocused = ContentEditorBox.SelectionHighlightColorWhenNotFocused;
            _editorInkBrush = (SolidColorBrush)ContentEditorBox.Resources["TextControlForeground"];
            ContentEditorBox.Resources["TextControlForegroundPointerOver"] = _editorInkBrush;
            ContentEditorBox.Resources["TextControlForegroundFocused"] = _editorInkBrush;
            ContentEditorBox.Foreground = _editorInkBrush;

            // 1. 初始化字纸微震动控制器与字迹风化渲染器
            _platenHaptics = new PlatenHapticController(EditorPaperContainer);
            InitializeInkWeatheringVisual();

            // 2. 挂载编辑器底层键盘事件与内容同步
            this.AddHandler(UIElement.PreviewKeyDownEvent, new KeyEventHandler(OnPagePreviewKeyDown), true);
            ContentEditorBox.TextChanged += ContentEditorBox_TextChanged;
            ContentEditorBox.SelectionChanged += ContentEditorBox_SelectionChanged;
            ContentEditorBox.Loaded += (s, e) => AttachEditorScrollViewer();
            ContentEditorBox.LostFocus += (s, e) =>
            {
                _isComposing = false;
                _wasComposingAtKeyDownStart = false;
                _undoManager.BreakCoalescing();
            };

            // 挂载 IME 中文拼音输入法组合事件，按词组建立独立撤回步，并在非键盘选词（如鼠标点击候选词）时补发击键音
            ContentEditorBox.TextCompositionStarted += (s, e) =>
            {
                _isComposing = true;
            };
            ContentEditorBox.TextCompositionEnded += (s, e) =>
            {
                _isComposing = false;
                _wasComposingAtKeyDownStart = false;
                string cleanText = GetCleanEditorText();
                RecordUserEditForUndo(cleanText, forceNewStep: true);

                long now = DateTime.UtcNow.Ticks;
                if (now - _lastKeyDownSoundTicks > 800_000)
                {
                    _lastCompositionSoundTicks = now;
                    _platenHaptics.PlayRecoil(isEnter: false);
                    SoundEffectService.Instance.PlayKeyClick(false);
                }

                SyncViewModelContent(cleanText);
            };

            // 使用 AddHandler (handledEventsToo: true) 拦截原生控件吃掉的 Enter 与 Space 等击键
            ContentEditorBox.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(OnEditorKeyDown), true);

            // 3. 插入时间戳请求
            ViewModel.RequestInsertText += (textToInsert) =>
            {
                _undoManager.BreakCoalescing();
                ContentEditorBox.Document.Selection.TypeText(textToInsert);
                ContentEditorBox.Focus(FocusState.Programmatic);
            };

            // 4. 监听视图模型状态
            ViewModel.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(ViewModel.DiaryContent))
                {
                    if (!_isUpdatingModelFromEditor && !ViewModel.IsLoading)
                    {
                        SetEditorText(ViewModel.DiaryContent);
                    }
                }
                else if (e.PropertyName == nameof(ViewModel.CurrentDate))
                {
                    if (!ViewModel.IsLoading)
                    {
                        AgingEngineService.Instance.SetCurrentViewingDate(ViewModel.CurrentDate);
                        UpdateInkWeatheringVisual();
                    }
                }
                else if (e.PropertyName == nameof(ViewModel.IsLoading) && !ViewModel.IsLoading)
                {
                    SetEditorText(ViewModel.DiaryContent);
                    AgingEngineService.Instance.SetCurrentViewingDate(ViewModel.CurrentDate);
                    UpdateInkWeatheringVisual();
                }
            };

            // 5. 订阅设置变更，动态响应排版字体与字号（严格与 Loaded/Unloaded 生命周期对齐）
            this.Loaded += (s, e) =>
            {
                AppSettingsService.Instance.SettingsChanged -= OnSettingsChanged;
                AppSettingsService.Instance.SettingsChanged += OnSettingsChanged;
                AmbianceThemeEngine.AmbiancePaletteChanged -= OnAmbiancePaletteChanged;
                AmbianceThemeEngine.AmbiancePaletteChanged += OnAmbiancePaletteChanged;
                this.ActualThemeChanged -= OnActualThemeChanged;
                this.ActualThemeChanged += OnActualThemeChanged;
                ApplyTypography(AppSettingsService.Instance.CurrentSettings);
                AttachEditorScrollViewer();
                UpdateInkWeatheringVisual(forceTextureReload: false);

                if (!string.IsNullOrEmpty(ViewModel.DiaryContent))
                {
                    string clean = GetCleanEditorText();
                    if (string.IsNullOrEmpty(clean))
                    {
                        SetEditorText(ViewModel.DiaryContent);
                    }
                }
            };

            this.Unloaded += async (s, e) =>
            {
                try
                {
                    _inkUpdateVersion++;
                    DetachEditorScrollViewer();
                    AppSettingsService.Instance.SettingsChanged -= OnSettingsChanged;
                    AmbianceThemeEngine.AmbiancePaletteChanged -= OnAmbiancePaletteChanged;
                    this.ActualThemeChanged -= OnActualThemeChanged;
                    ViewModel.DisposeAutoSaveTimer();
                    if (!ViewModel.IsLoading && ViewModel.HasUnsavedChanges)
                    {
                        await ViewModel.SaveCurrentAsync();
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[EditorPage] Unloaded error: {ex.Message}");
                }
            };
        }

        public void DisposeResources()
        {
            try
            {
                ViewModel.DisposeAutoSaveTimer();
                DetachEditorScrollViewer();
                _inkRenderer?.Dispose();
                _inkRenderer = null;
            }
            catch { }
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            DateTime targetDate = (e.Parameter is DateTime d) ? d.Date : DateTime.Today;

            if (ViewModel.CurrentDate.Date == targetDate.Date)
            {
                if (ViewModel.HasUnsavedChanges)
                {
                    AgingEngineService.Instance.SetCurrentViewingDate(targetDate);
                    ApplyTypography(AppSettingsService.Instance.CurrentSettings);
                    await UpdateInkWeatheringVisualAsync();
                    ApplyPendingSearchHighlightIfAny();
                    ContentEditorBox.Focus(FocusState.Programmatic);
                    return;
                }

                if (!string.IsNullOrEmpty(ViewModel.DiaryContent))
                {
                    bool existsOnDisk = DiaryStorageService.Instance.DiaryExists(targetDate);
                    if (existsOnDisk)
                    {
                        string diskContent = await DiaryStorageService.Instance.LoadDiaryContentAsync(targetDate);
                        if (string.Equals(
                            DiaryStorageService.NormalizeLineEndings(diskContent),
                            DiaryStorageService.NormalizeLineEndings(ViewModel.DiaryContent),
                            StringComparison.Ordinal))
                        {
                            AgingEngineService.Instance.SetCurrentViewingDate(targetDate);
                            ApplyTypography(AppSettingsService.Instance.CurrentSettings);
                            await UpdateInkWeatheringVisualAsync();
                            ApplyPendingSearchHighlightIfAny();
                            ContentEditorBox.Focus(FocusState.Programmatic);
                            return;
                        }
                    }
                }
            }

            await LoadDateExplicitlyAsync(targetDate);
        }

        public async Task FlushAndSaveCurrentAsync()
        {
            string liveText = GetCleanEditorText();
            SyncViewModelContent(liveText);
            await ViewModel.SaveCurrentAsync();
        }

        public async Task LoadDateExplicitlyAsync(DateTime targetDate)
        {
            int version = ++_dateLoadVersion;

            if (ViewModel.CurrentDate.Date != targetDate.Date && ViewModel.HasUnsavedChanges)
            {
                string liveText = GetCleanEditorText();
                SyncViewModelContent(liveText);
                await ViewModel.SaveCurrentAsync();
            }

            if (version != _dateLoadVersion) return;
            var settings = AppSettingsService.Instance.CurrentSettings;
            if (AgingEngineService.Instance.CalculateAgingIntensity(
                    targetDate, settings.AgingInkIntensity, settings.IsAgingEnabled) > 0.0001)
            {
                for (int attempt = 0; attempt < 3 && !IsInkTexturePrepared(); attempt++)
                {
                    await UpdateInkWeatheringVisualAsync(preloadOnly: true);
                    if (version != _dateLoadVersion) return;
                }
            }
            if (version != _dateLoadVersion) return;
            await ViewModel.LoadDateAsync(targetDate);
            ApplyPendingSearchHighlightIfAny();
        }

        protected override void OnNavigatingFrom(NavigatingCancelEventArgs e)
        {
            base.OnNavigatingFrom(e);
            ViewModel.DisposeAutoSaveTimer();
            if (!ViewModel.IsLoading && ViewModel.HasUnsavedChanges)
            {
                string liveText = GetCleanEditorText();
                SyncViewModelContent(liveText);
                _ = ViewModel.SaveCurrentAsync();
            }
        }

        private string? _lastTypographyFont;
        private double _lastTypographySize = -1;
        private double _lastTypographySpacing = -1;

        private void OnSettingsChanged(object? sender, AppSettings settings)
        {
            this.DispatcherQueue.TryEnqueue(() =>
            {
                if (!string.Equals(_lastTypographyFont, settings.FontFamily, StringComparison.Ordinal) ||
                    Math.Abs(_lastTypographySize - settings.FontSize) >= 0.01 ||
                    Math.Abs(_lastTypographySpacing - settings.LineSpacing) >= 0.005)
                {
                    ApplyTypography(settings);
                }
                UpdateInkWeatheringVisual(forceTextureReload: false);
            });
        }

        #region 动态排版与字体 (Typography)

        public void ApplyTypography(AppSettings settings)
        {
            try
            {
                bool fontOrSizeChanged = !string.Equals(_lastTypographyFont, settings.FontFamily, StringComparison.Ordinal) ||
                                         Math.Abs(_lastTypographySize - settings.FontSize) >= 0.01;

                _lastTypographyFont = settings.FontFamily;
                _lastTypographySize = settings.FontSize;
                _lastTypographySpacing = settings.LineSpacing;

                var family = AppSettings.BuildXamlFontFamily(settings.FontFamily);

                if (fontOrSizeChanged)
                {
                    ContentEditorBox.FontFamily = family;
                    ContentEditorBox.FontSize = settings.FontSize;
                }

                double lineSpacingMultiplier = Math.Clamp(settings.LineSpacing, 1.2, 2.2);
                double exactLineHeightPx = Math.Round(settings.FontSize * lineSpacingMultiplier * 1.35, 1);
                float exactLineHeightPt = (float)(exactLineHeightPx * 0.75);

                ApplyDocumentLineSpacingOnly(exactLineHeightPt);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ApplyTypography Error] {ex.Message}");
            }
        }

        private void ApplyDocumentLineSpacingOnly(float exactLineHeightPt)
        {
            try
            {
                var doc = ContentEditorBox.Document;
                if (doc == null) return;

                var defaultFormat = doc.GetDefaultParagraphFormat();
                defaultFormat.SpaceBefore = 0;
                defaultFormat.SpaceAfter = 0;
                defaultFormat.SetLineSpacing(LineSpacingRule.Exactly, exactLineHeightPt);
                doc.SetDefaultParagraphFormat(defaultFormat);

                var wholeStory = doc.GetRange(0, int.MaxValue);
                wholeStory.ParagraphFormat.SpaceBefore = 0;
                wholeStory.ParagraphFormat.SpaceAfter = 0;
                wholeStory.ParagraphFormat.SetLineSpacing(LineSpacingRule.Exactly, exactLineHeightPt);
            }
            catch { }
        }

        #endregion

        #region 键盘敲击音效与快捷键路由

        private void OnPagePreviewKeyDown(object sender, KeyRoutedEventArgs e)
        {
            _wasComposingAtKeyDownStart = _isComposing;

            bool isCtrl = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
                           & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;
            bool isShift = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
                            & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;
            bool isAlt = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu)
                          & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;

            if (e.Key == VirtualKey.Escape && FindBarBorder != null && FindBarBorder.Visibility == Visibility.Visible)
            {
                e.Handled = true;
                CloseFindBar();
                return;
            }

            if (isCtrl && !isAlt)
            {
                if (e.Key == VirtualKey.Z)
                {
                    e.Handled = true;
                    if (isShift)
                    {
                        PerformRedo();
                    }
                    else
                    {
                        PerformUndo();
                    }
                    return;
                }
                else if (e.Key == VirtualKey.Y && !isShift)
                {
                    e.Handled = true;
                    PerformRedo();
                    return;
                }
                else if (e.Key == VirtualKey.S && !isShift)
                {
                    e.Handled = true;
                    TriggerManualSave();
                    return;
                }
                else if (e.Key == VirtualKey.T && !isShift)
                {
                    e.Handled = true;
                    TriggerInsertTimestamp();
                    return;
                }
                else if (e.Key == VirtualKey.F && !isShift)
                {
                    e.Handled = true;
                    OpenFindBar();
                    return;
                }
            }
        }

        private void OnEditorKeyDown(object sender, KeyRoutedEventArgs e)
        {
            var key = e.Key;
            bool wasComposing = _wasComposingAtKeyDownStart || _isComposing;
            _wasComposingAtKeyDownStart = false;

            bool isCtrl = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
                           & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;
            bool isAlt = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu)
                          & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;
            if (isCtrl || isAlt)
            {
                return;
            }

            if (key is VirtualKey.Shift or VirtualKey.LeftShift or VirtualKey.RightShift
                or VirtualKey.Control or VirtualKey.LeftControl or VirtualKey.RightControl
                or VirtualKey.Menu or VirtualKey.LeftMenu or VirtualKey.RightMenu
                or VirtualKey.CapitalLock or VirtualKey.NumberKeyLock or VirtualKey.Scroll
                or VirtualKey.LeftWindows or VirtualKey.RightWindows or VirtualKey.Application
                or VirtualKey.Escape or VirtualKey.Pause or VirtualKey.Snapshot or VirtualKey.Insert
                or VirtualKey.Left or VirtualKey.Right or VirtualKey.Up or VirtualKey.Down
                or VirtualKey.PageUp or VirtualKey.PageDown or VirtualKey.Home or VirtualKey.End
                or (>= VirtualKey.F1 and <= VirtualKey.F12))
            {
                return;
            }

            long now = DateTime.UtcNow.Ticks;
            if (now - _lastCompositionSoundTicks < 500_000)
            {
                return;
            }

            if (key == VirtualKey.Enter)
            {
                _lastKeyDownSoundTicks = now;
                if (wasComposing)
                {
                    _platenHaptics.PlayRecoil(isEnter: false);
                    SoundEffectService.Instance.PlayKeyClick(false);
                }
                else
                {
                    _platenHaptics.PlayRecoil(isEnter: true);
                    SoundEffectService.Instance.PlayEnterKey();
                }
                return;
            }

            _lastKeyDownSoundTicks = now;
            bool isSpecial = (key is VirtualKey.Space or VirtualKey.Back or VirtualKey.Delete or VirtualKey.Tab);
            _platenHaptics.PlayRecoil(isEnter: false);
            SoundEffectService.Instance.PlayKeyClick(isSpecial);
        }

        private string GetCleanEditorText()
        {
            ContentEditorBox.Document.GetText(TextGetOptions.None, out string text);
            if (text.EndsWith("\r\n")) return text[..^2];
            if (text.EndsWith("\r") || text.EndsWith("\n")) return text[..^1];
            return text;
        }

        private void ContentEditorBox_SelectionChanged(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingEditorFromModel || _isPerformingUndoRedo || _isComposing || ViewModel.IsLoading)
                return;

            if (ContentEditorBox.Document != null)
            {
                int selStart = Math.Max(0, ContentEditorBox.Document.Selection.StartPosition);
                int selLen = Math.Abs(ContentEditorBox.Document.Selection.Length);
                _undoManager.UpdateSelection(selStart, selLen);
            }
        }

        private void ContentEditorBox_TextChanged(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingEditorFromModel || _isPerformingUndoRedo || ViewModel.IsLoading)
            {
                return;
            }

            string cleanText = GetCleanEditorText();

            if (!_isComposing)
            {
                RecordUserEditForUndo(cleanText, forceNewStep: false);
            }

            SyncViewModelContent(cleanText);
        }

        private void SyncViewModelContent(string cleanText)
        {
            if (ViewModel.DiaryContent != cleanText)
            {
                _isUpdatingModelFromEditor = true;
                try
                {
                    ViewModel.DiaryContent = cleanText;
                }
                finally
                {
                    _isUpdatingModelFromEditor = false;
                }
            }
        }

        private void RecordUserEditForUndo(string cleanText, bool forceNewStep)
        {
            if (_isUpdatingEditorFromModel || _isPerformingUndoRedo || ViewModel.IsLoading)
                return;

            int selStart = Math.Max(0, ContentEditorBox.Document.Selection.StartPosition);
            int selLen = Math.Abs(ContentEditorBox.Document.Selection.Length);
            _undoManager.RecordEdit(cleanText, selStart, selLen, forceNewStep);
        }

        private void PerformUndo()
        {
            if (_isUpdatingEditorFromModel || _isPerformingUndoRedo || ViewModel.IsLoading)
                return;

            string currentLiveText = GetCleanEditorText();
            int selStart = Math.Max(0, ContentEditorBox.Document.Selection.StartPosition);
            int selLen = Math.Abs(ContentEditorBox.Document.Selection.Length);
            _isComposing = false;

            if (_undoManager.TryUndo(currentLiveText, selStart, selLen, out var targetSnapshot))
            {
                ApplyHistorySnapshot(targetSnapshot);
            }
        }

        private void PerformRedo()
        {
            if (_isUpdatingEditorFromModel || _isPerformingUndoRedo || ViewModel.IsLoading)
                return;

            _isComposing = false;

            if (_undoManager.TryRedo(out var targetSnapshot))
            {
                ApplyHistorySnapshot(targetSnapshot);
            }
        }

        private void ApplyHistorySnapshot(EditorHistorySnapshot snapshot)
        {
            _isPerformingUndoRedo = true;
            try
            {
                ContentEditorBox.Document.SetText(TextSetOptions.None, snapshot.Text);
                ApplyTypography(AppSettingsService.Instance.CurrentSettings);
                _lastAppliedInkColor = null;
                ApplyAgedInkColorToDocument(_lastInkAgingIntensity, IsDarkModeEffective());

                string actualText = GetCleanEditorText();
                int maxPos = actualText.Length;
                int start = Math.Clamp(snapshot.SelectionStart, 0, maxPos);
                int end = Math.Clamp(start + snapshot.SelectionLength, start, maxPos);
                ContentEditorBox.Document.Selection.SetRange(start, end);
                ContentEditorBox.Document.Selection.ScrollIntoView(PointOptions.None);

                ContentEditorBox.Document.UndoLimit = 0;
                ContentEditorBox.Document.UndoLimit = 100;

                _undoManager.ConfirmAppliedSnapshot(actualText, start, end - start);

                SyncViewModelContent(actualText);
                ContentEditorBox.Focus(FocusState.Programmatic);
            }
            finally
            {
                _isPerformingUndoRedo = false;
            }
        }

        private void SetEditorText(string content)
        {
            _isUpdatingEditorFromModel = true;
            _isComposing = false;
            _wasComposingAtKeyDownStart = false;
            try
            {
                string safeContent = content ?? string.Empty;
                ContentEditorBox.Document.SetText(TextSetOptions.None, safeContent);
                ApplyTypography(AppSettingsService.Instance.CurrentSettings);
                _lastAppliedInkColor = null;
                var agingSettings = AppSettingsService.Instance.CurrentSettings;
                _lastInkAgingIntensity = AgingEngineService.Instance.CalculateAgingIntensity(
                    ViewModel.CurrentDate, agingSettings.AgingInkIntensity, agingSettings.IsAgingEnabled);
                ApplyAgedInkColorToDocument(_lastInkAgingIntensity, IsDarkModeEffective());
                _inkRenderer?.SetAppearance(_lastInkAgingIntensity / AgingConstants.MaxAgingSaturation,
                    IsDarkModeEffective());

                ContentEditorBox.Document.UndoLimit = 0;
                ContentEditorBox.Document.UndoLimit = 100;

                string actualText = GetCleanEditorText();
                ViewModel.AlignLoadedContent(actualText);
                _undoManager.ResetHistory(actualText);
            }
            finally
            {
                _isUpdatingEditorFromModel = false;
            }
        }

        private void EditorViewport_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            double availableWidth = e.NewSize.Width;
            if (availableWidth > 0 && EditorPaperContainer != null)
            {
                double targetWidth = Math.Min(860, Math.Max(1, availableWidth - 48));
                EditorPaperContainer.Width = targetWidth;
            }
        }

        #endregion

        #region 页内查找与搜索词跨页定位 (Ctrl+F)

        public void OpenFindBarWithKeyword(string? keyword)
        {
            if (string.IsNullOrWhiteSpace(keyword)) return;
            _pendingSearchKeyword = keyword.Trim();
            if (!ViewModel.IsLoading)
            {
                ApplyPendingSearchHighlightIfAny();
            }
        }

        private void ApplyPendingSearchHighlightIfAny()
        {
            if (string.IsNullOrWhiteSpace(_pendingSearchKeyword)) return;
            string kw = _pendingSearchKeyword;
            _pendingSearchKeyword = null;

            if (FindBarBorder != null && FindQueryTextBox != null)
            {
                FindBarBorder.Visibility = Visibility.Visible;
                FindQueryTextBox.Text = kw;
                RebuildFindMatchesAndSelect(kw, selectFirst: true, focusEditorSelection: false);
            }
        }

        private void OpenFindBar()
        {
            if (FindBarBorder == null || FindQueryTextBox == null) return;
            FindBarBorder.Visibility = Visibility.Visible;

            string selectedText = ContentEditorBox.Document.Selection.Text?.Trim() ?? string.Empty;
            if (!string.IsNullOrEmpty(selectedText) && selectedText.Length <= 40 && !selectedText.Contains('\r'))
            {
                FindQueryTextBox.Text = selectedText;
            }
            else if (!string.IsNullOrWhiteSpace(FindQueryTextBox.Text))
            {
                RebuildFindMatchesAndSelect(FindQueryTextBox.Text.Trim(), selectFirst: true, focusEditorSelection: false);
            }

            FindQueryTextBox.Focus(FocusState.Programmatic);
            FindQueryTextBox.SelectAll();
        }

        private void CloseFindBar()
        {
            if (FindBarBorder != null)
            {
                FindBarBorder.Visibility = Visibility.Collapsed;
            }
            _findMatchPositions.Clear();
            _currentFindMatchIndex = -1;
            ContentEditorBox.Focus(FocusState.Programmatic);
        }

        private void OpenFindBarButton_Click(object sender, RoutedEventArgs e) => OpenFindBar();

        private void CloseFindBarButton_Click(object sender, RoutedEventArgs e) => CloseFindBar();

        private void FindQueryTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            string query = FindQueryTextBox?.Text?.Trim() ?? string.Empty;
            RebuildFindMatchesAndSelect(query, selectFirst: true, focusEditorSelection: false);
        }

        private void FindQueryTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == VirtualKey.Enter)
            {
                e.Handled = true;
                bool isShift = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
                                & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;
                StepFindMatch(forward: !isShift);
            }
            else if (e.Key == VirtualKey.Escape)
            {
                e.Handled = true;
                CloseFindBar();
            }
        }

        private void FindPreviousButton_Click(object sender, RoutedEventArgs e) => StepFindMatch(forward: false);

        private void FindNextButton_Click(object sender, RoutedEventArgs e) => StepFindMatch(forward: true);

        private void RebuildFindMatchesAndSelect(string query, bool selectFirst, bool focusEditorSelection)
        {
            _findMatchPositions.Clear();
            _currentFindMatchIndex = -1;

            if (string.IsNullOrEmpty(query) || ContentEditorBox.Document == null)
            {
                if (FindMatchStatusText != null) FindMatchStatusText.Text = "0 / 0";
                return;
            }

            ContentEditorBox.Document.GetText(TextGetOptions.None, out string rawDocText);
            if (string.IsNullOrEmpty(rawDocText))
            {
                if (FindMatchStatusText != null) FindMatchStatusText.Text = "0 / 0";
                return;
            }

            int idx = 0;
            while ((idx = rawDocText.IndexOf(query, idx, StringComparison.OrdinalIgnoreCase)) != -1)
            {
                _findMatchPositions.Add(idx);
                idx += query.Length;
                if (_findMatchPositions.Count >= 500) break;
            }

            if (_findMatchPositions.Count == 0)
            {
                if (FindMatchStatusText != null) FindMatchStatusText.Text = "0 / 0";
                return;
            }

            if (selectFirst)
            {
                _currentFindMatchIndex = 0;
                SelectCurrentFindMatch(query.Length, focusEditorSelection);
            }
        }

        private void StepFindMatch(bool forward)
        {
            string query = FindQueryTextBox?.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(query)) return;

            if (_findMatchPositions.Count == 0)
            {
                RebuildFindMatchesAndSelect(query, selectFirst: true, focusEditorSelection: false);
                return;
            }

            if (forward)
            {
                _currentFindMatchIndex = (_currentFindMatchIndex + 1) % _findMatchPositions.Count;
            }
            else
            {
                _currentFindMatchIndex = (_currentFindMatchIndex - 1 + _findMatchPositions.Count) % _findMatchPositions.Count;
            }

            SelectCurrentFindMatch(query.Length, focusEditorSelection: false);
        }

        private void SelectCurrentFindMatch(int queryLength, bool focusEditorSelection)
        {
            if (_currentFindMatchIndex < 0 || _currentFindMatchIndex >= _findMatchPositions.Count) return;

            int pos = _findMatchPositions[_currentFindMatchIndex];
            if (FindMatchStatusText != null)
            {
                FindMatchStatusText.Text = $"{_currentFindMatchIndex + 1} / {_findMatchPositions.Count}";
            }

            try
            {
                ContentEditorBox.Document.Selection.SetRange(pos, pos + queryLength);
                ContentEditorBox.Document.Selection.ScrollIntoView(PointOptions.None);
                if (focusEditorSelection)
                {
                    ContentEditorBox.Focus(FocusState.Programmatic);
                }
            }
            catch { }
        }

        #endregion

        private void TriggerInsertTimestamp()
        {
            string liveText = GetCleanEditorText();
            SyncViewModelContent(liveText);

            string? prefixContext = null;
            if (ContentEditorBox.Document != null)
            {
                int selStart = Math.Clamp(ContentEditorBox.Document.Selection.StartPosition, 0, liveText.Length);
                prefixContext = liveText[..selStart];
            }

            ViewModel.InsertTimestampAtContext(prefixContext);
        }

        private void TimestampButton_Click(object sender, RoutedEventArgs e)
        {
            TriggerInsertTimestamp();
        }

        private void TriggerManualSave()
        {
            string liveText = GetCleanEditorText();
            SyncViewModelContent(liveText);
            SoundEffectService.Instance.PlaySaveSound();
            _ = ViewModel.SaveCurrentAsync();
        }

        private void ManualSaveButton_Click(object sender, RoutedEventArgs e)
        {
            TriggerManualSave();
        }

        #region 岁月沉淀：字迹风化

        private void AttachEditorScrollViewer()
        {
            ContentEditorBox.ApplyTemplate();
            var scrollViewer = FindDescendantScrollViewer(ContentEditorBox);
            if (ReferenceEquals(_editorInnerScrollViewer, scrollViewer)) return;
            DetachEditorScrollViewer();
            _editorInnerScrollViewer = scrollViewer;
            if (scrollViewer != null)
            {
                scrollViewer.ViewChanged += EditorInnerScrollViewer_ViewChanged;
                UpdateInkTextureScrollOffset();
            }
        }

        private void DetachEditorScrollViewer()
        {
            if (_editorInnerScrollViewer != null)
                _editorInnerScrollViewer.ViewChanged -= EditorInnerScrollViewer_ViewChanged;
            _editorInnerScrollViewer = null;
        }

        private static ScrollViewer? FindDescendantScrollViewer(DependencyObject root)
        {
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is ScrollViewer scrollViewer) return scrollViewer;
                var nested = FindDescendantScrollViewer(child);
                if (nested != null) return nested;
            }
            return null;
        }

        private void EditorInnerScrollViewer_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
        {
            UpdateInkTextureScrollOffset();
        }

        private void UpdateInkTextureScrollOffset()
        {
            _inkRenderer?.SetScrollOffset(_editorInnerScrollViewer?.VerticalOffset ?? 0.0);
        }

        private bool IsDarkModeEffective()
        {
            var palette = AmbianceThemeEngine.CurrentPalette;
            if (palette.ForcedDarkTheme.HasValue) return palette.ForcedDarkTheme.Value;
            if (ActualTheme == ElementTheme.Dark) return true;
            if (ActualTheme == ElementTheme.Light) return false;
            return Application.Current?.RequestedTheme == ApplicationTheme.Dark;
        }

        private void OnAmbiancePaletteChanged()
        {
            DispatcherQueue.TryEnqueue(() => UpdateInkWeatheringVisual());
        }

        private void OnActualThemeChanged(FrameworkElement sender, object args)
        {
            UpdateInkWeatheringVisual();
        }

        private void InitializeInkWeatheringVisual()
        {
            try
            {
                _inkRenderer = new InkWeatheringRenderer(ContentEditorBox, InkWeatheringHost);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"[InkWeathering Init Error] {ex}");
            }
        }

        private void UpdateInkWeatheringVisual(bool forceTextureReload = false)
        {
            _ = UpdateInkWeatheringVisualAsync(forceTextureReload);
        }

        private bool IsInkTexturePrepared()
        {
            return _inkRenderer?.HasTexture == true &&
                string.Equals(_currentInkTexturePath,
                    AppSettingsService.Instance.CurrentSettings.AgingInkTexturePath,
                    StringComparison.Ordinal);
        }

        private async Task UpdateInkWeatheringVisualAsync(bool forceTextureReload = false, bool preloadOnly = false)
        {
            if (_inkRenderer == null) return;
            int version = ++_inkUpdateVersion;
            CanvasBitmap? newTexture = null;
            try
            {
                var settings = AppSettingsService.Instance.CurrentSettings;
                if (!preloadOnly)
                {
                    _lastInkAgingIntensity = AgingEngineService.Instance.CalculateAgingIntensity(
                        ViewModel.CurrentDate, settings.AgingInkIntensity, settings.IsAgingEnabled);
                    bool dark = IsDarkModeEffective();
                    ApplyAgedInkColorToDocument(_lastInkAgingIntensity, dark);
                    _inkRenderer.SetAppearance(_lastInkAgingIntensity / AgingConstants.MaxAgingSaturation, dark);
                    if (_lastInkAgingIntensity <= 0.0001) return;
                }
                if (!forceTextureReload && IsInkTexturePrepared()) return;
                string? customPath = settings.AgingInkTexturePath;
                string texturePath = await AgingEngineService.Instance.GetInkWeatheringMaskPathAsync(customPath);
                if (version != _inkUpdateVersion || _inkRenderer == null) return;
                newTexture = await _inkRenderer.LoadTextureAsync(texturePath);
                if (version != _inkUpdateVersion || _inkRenderer == null) return;
                _inkRenderer.SetTexture(newTexture);
                newTexture = null;
                _currentInkTexturePath = customPath;
                UpdateInkTextureScrollOffset();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"[InkWeathering Texture Error] {ex}");
            }
            finally
            {
                newTexture?.Dispose();
            }
        }

        private static Windows.UI.Color GetBaseAgedInkColor(double intensity, bool isDark)
        {
            var fresh = isDark
                ? Windows.UI.Color.FromArgb(255, 255, 255, 255)
                : Windows.UI.Color.FromArgb(255, 26, 26, 26);
            if (intensity <= 0.0001)
            {
                return fresh;
            }

            var aged = isDark
                ? Windows.UI.Color.FromArgb(255, 202, 190, 176)
                : Windows.UI.Color.FromArgb(255, 85, 72, 58);
            double t = Math.Clamp(intensity / AgingConstants.MaxAgingSaturation, 0.0, 1.0);
            return Windows.UI.Color.FromArgb(255,
                (byte)Math.Round(fresh.R + (aged.R - fresh.R) * t),
                (byte)Math.Round(fresh.G + (aged.G - fresh.G) * t),
                (byte)Math.Round(fresh.B + (aged.B - fresh.B) * t));
        }

        private void ApplyAgedInkColorToDocument(double intensity, bool isDark)
        {
            try
            {
                var doc = ContentEditorBox.Document;
                if (doc == null) return;

                bool aged = intensity > 0.0001;
                if (_isUsingAgedSelectionHighlight != aged)
                {
                    if (aged)
                    {
                        _agedSelectionHighlightColor ??= new SolidColorBrush(
                            Windows.UI.Color.FromArgb(255, 0, 120, 215));
                        ContentEditorBox.SelectionHighlightColor = _agedSelectionHighlightColor;
                        ContentEditorBox.SelectionHighlightColorWhenNotFocused = _agedSelectionHighlightColor;
                    }
                    else
                    {
                        ContentEditorBox.SelectionHighlightColor = _nativeSelectionHighlightColor;
                        ContentEditorBox.SelectionHighlightColorWhenNotFocused =
                            _nativeSelectionHighlightColorWhenNotFocused;
                    }
                    _isUsingAgedSelectionHighlight = aged;
                }

                Windows.UI.Color targetColor = GetBaseAgedInkColor(intensity, isDark);
                _editorInkBrush.Color = targetColor;
                if (!ReferenceEquals(ContentEditorBox.Foreground, _editorInkBrush))
                    ContentEditorBox.Foreground = _editorInkBrush;

                if (_lastAppliedInkColor == targetColor) return;

                var defaultFormat = doc.GetDefaultCharacterFormat();
                defaultFormat.ForegroundColor = targetColor;
                doc.SetDefaultCharacterFormat(defaultFormat);

                var wholeStory = doc.GetRange(0, int.MaxValue);
                wholeStory.CharacterFormat.ForegroundColor = targetColor;
                _lastAppliedInkColor = targetColor;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ApplyAgedInkColorToDocument Error] {ex.Message}");
            }
        }

        #endregion
    }
}
