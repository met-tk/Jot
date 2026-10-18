using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.UI.Composition;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Windows.System;
using Windows.UI.Core;
using Journal.Models;
using Journal.Services;
using Journal.ViewModels;

namespace Journal.Views
{
    public sealed partial class EditorPage : Page
    {
        public EditorViewModel ViewModel { get; } = new();

        // 硬件合成器微震动动画字段 (0-GC Allocation)
        private Microsoft.UI.Composition.Visual? _paperVisual;
        private Microsoft.UI.Composition.Compositor? _compositor;
        private Microsoft.UI.Composition.Vector3KeyFrameAnimation? _normalRecoilAnim;
        private Microsoft.UI.Composition.Vector3KeyFrameAnimation? _enterRecoilAnim;

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
        private bool _isUpdatingEditorFromModel = false;
        private bool _isUpdatingModelFromEditor = false;
        private bool _isPerformingUndoRedo = false;
        private bool _isComposing = false;
        private bool _wasComposingAtKeyDownStart = false;
        private long _lastKeyDownSoundTicks = 0;
        private long _lastCompositionSoundTicks = 0;

        // 受控分段撤回/反撤回栈（防止穿透初始加载基线清空全文，并支持 Ctrl+Shift+Z 反撤回）
        private const int MaxUndoSteps = 200;
        private readonly struct EditorHistorySnapshot
        {
            public string Text { get; }
            public int SelectionStart { get; }
            public int SelectionLength { get; }

            public EditorHistorySnapshot(string text, int selectionStart, int selectionLength)
            {
                Text = text ?? string.Empty;
                SelectionStart = Math.Max(0, selectionStart);
                SelectionLength = Math.Max(0, selectionLength);
            }
        }

        private readonly List<EditorHistorySnapshot> _undoStack = new();
        private readonly Stack<EditorHistorySnapshot> _redoStack = new();
        private EditorHistorySnapshot _currentConfirmedSnapshot = new(string.Empty, 0, 0);
        private DateTime _lastEditTimestamp = DateTime.MinValue;
        private int _lastEditDirection = 0;
        private int _charsSinceLastSnapshot = 0;
        private bool _canCoalesceCurrentBurst = false;

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

            // 1. 初始化字纸微震动动画
            InitializePlatenVibration();

            // 纸纹由 MainWindow 负责，字迹由编辑器自身的图像负责。
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
                _canCoalesceCurrentBurst = false;
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
                    PlayPlatenRecoil(isEnter: false);
                    SoundEffectService.Instance.PlayKeyClick(false);
                }

                SyncViewModelContent(cleanText);
            };

            // 使用 AddHandler (handledEventsToo: true) 拦截原生控件吃掉的 Enter 与 Space 等击键
            ContentEditorBox.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(OnEditorKeyDown), true);

            // 3. 插入时间戳请求
            ViewModel.RequestInsertText += (textToInsert) =>
            {
                _canCoalesceCurrentBurst = false;
                ContentEditorBox.Document.Selection.TypeText(textToInsert);
                ContentEditorBox.Focus(FocusState.Programmatic);
            };

            // 5. 监听视图模型状态
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
                    // Commit the paper and ink date after the matching diary content arrives.
                    SetEditorText(ViewModel.DiaryContent);
                    AgingEngineService.Instance.SetCurrentViewingDate(ViewModel.CurrentDate);
                    UpdateInkWeatheringVisual();
                }
            };

            // 6. 订阅设置变更，动态响应排版字体与字号（严格与 Loaded/Unloaded 生命周期对齐）
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

                // 页面挂载完成校验：若 RichEditBox 呈现空文档而 ViewModel.DiaryContent 有实际内容，安全重新对齐刷写
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

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            DateTime targetDate = (e.Parameter is DateTime d) ? d.Date : DateTime.Today;

            // 关键草稿与内存保护守卫：
            // 若导航回的目标日期正是当前正在编辑的同一天，且内存中已有正文或未保存输入，绝不从磁盘重新载入！
            // 完整保留用户未保存的输入内容、光标位置与撤回历史栈
            if (ViewModel.CurrentDate.Date == targetDate.Date && 
                (!string.IsNullOrEmpty(ViewModel.DiaryContent) || ViewModel.HasUnsavedChanges))
            {
                AgingEngineService.Instance.SetCurrentViewingDate(targetDate);
                ApplyTypography(AppSettingsService.Instance.CurrentSettings);
                await UpdateInkWeatheringVisualAsync();
                ContentEditorBox.Focus(FocusState.Programmatic);
                return;
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

            // 若之前在编辑其他日期且有未保存更改，先持久化旧日期
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
                // Prepare the texture and compositor before the new document is shown.
                // The currently visible diary keeps its exact rendering while this runs.
                if (!IsLoaded)
                {
                    var loaded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    RoutedEventHandler? onLoaded = null;
                    onLoaded = (s, e) =>
                    {
                        Loaded -= onLoaded;
                        loaded.TrySetResult(true);
                    };
                    Loaded += onLoaded;
                    await loaded.Task;
                }
                if (version != _dateLoadVersion) return;
                // A settings/theme refresh can supersede an in-flight compositor
                // update. Recheck the prepared brush before publishing old text.
                for (int attempt = 0; attempt < 3 && !IsInkTexturePrepared(); attempt++)
                {
                    await UpdateInkWeatheringVisualAsync(preloadOnly: true);
                    if (version != _dateLoadVersion) return;
                }
            }
            if (version != _dateLoadVersion) return;
            await ViewModel.LoadDateAsync(targetDate);
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

        #region 拟物字纸微震动 (Platen Recoil Vibration)

        private void InitializePlatenVibration()
        {
            try
            {
                ElementCompositionPreview.SetIsTranslationEnabled(EditorPaperContainer, true);
                _paperVisual = ElementCompositionPreview.GetElementVisual(EditorPaperContainer);
                _compositor = _paperVisual.Compositor;

                // 常规击键：瞬间下沉 1.1px，50ms 弹性平滑复位
                _normalRecoilAnim = _compositor.CreateVector3KeyFrameAnimation();
                _normalRecoilAnim.Duration = TimeSpan.FromMilliseconds(50);
                _normalRecoilAnim.InsertKeyFrame(0.0f, new Vector3(0, 0, 0));
                _normalRecoilAnim.InsertKeyFrame(0.24f, new Vector3(0, 1.1f, 0),
                    _compositor.CreateCubicBezierEasingFunction(new Vector2(0.1f, 0.9f), new Vector2(0.2f, 1.0f)));
                _normalRecoilAnim.InsertKeyFrame(1.0f, new Vector3(0, 0, 0),
                    _compositor.CreateCubicBezierEasingFunction(new Vector2(0.4f, 0.0f), new Vector2(0.2f, 1.0f)));

                // 回车换行：机架推拉下沉 2.2px，75ms 阻尼复位
                _enterRecoilAnim = _compositor.CreateVector3KeyFrameAnimation();
                _enterRecoilAnim.Duration = TimeSpan.FromMilliseconds(75);
                _enterRecoilAnim.InsertKeyFrame(0.0f, new Vector3(0, 0, 0));
                _enterRecoilAnim.InsertKeyFrame(0.20f, new Vector3(0, 2.2f, 0),
                    _compositor.CreateCubicBezierEasingFunction(new Vector2(0.1f, 0.9f), new Vector2(0.2f, 1.0f)));
                _enterRecoilAnim.InsertKeyFrame(1.0f, new Vector3(0, 0, 0),
                    _compositor.CreateCubicBezierEasingFunction(new Vector2(0.4f, 0.0f), new Vector2(0.2f, 1.0f)));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[PlatenVibration Init Error] {ex.Message}");
            }
        }

        private void PlayPlatenRecoil(bool isEnter)
        {
            if (!AppSettingsService.Instance.CurrentSettings.IsHapticVibrationEnabled || _paperVisual == null) return;

            try
            {
                if (isEnter && _enterRecoilAnim != null)
                {
                    _paperVisual.StartAnimation("Translation", _enterRecoilAnim);
                }
                else if (_normalRecoilAnim != null)
                {
                    _paperVisual.StartAnimation("Translation", _normalRecoilAnim);
                }
            }
            catch { }
        }

        #endregion

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

                // 1. 仅当字体或字号确实改变时更新控件属性，避免无效布局重排
                if (fontOrSizeChanged)
                {
                    ContentEditorBox.FontFamily = family;
                    ContentEditorBox.FontSize = settings.FontSize;
                }

                // 统一精准行高计算（像素与磅）：
                // 在中文字体（如思源宋体、微软雅黑）中，自然行高约为字号的 1.35 倍
                // 当 settings.LineSpacing = 1.2 时，实际单行垂直间隙 = FontSize * LineSpacing * 1.35
                double lineSpacingMultiplier = Math.Clamp(settings.LineSpacing, 1.2, 2.2);
                double exactLineHeightPx = Math.Round(settings.FontSize * lineSpacingMultiplier * 1.35, 1);
                float exactLineHeightPt = (float)(exactLineHeightPx * 0.75); // 1 pt = 4/3 px

                // 编辑器独占排版；纹理跟随其已排好的字形，不再猜测行高。
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

        #region 键盘敲击音效与事件捕获

        private void OnPagePreviewKeyDown(object sender, KeyRoutedEventArgs e)
        {
            _wasComposingAtKeyDownStart = _isComposing;

            bool isCtrl = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
                           & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;
            bool isShift = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
                            & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;
            bool isAlt = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu)
                          & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;

            if (isCtrl && !isAlt)
            {
                if (e.Key == VirtualKey.Z)
                {
                    e.Handled = true;
                    if (isShift)
                    {
                        // Ctrl + Shift + Z: 撤销撤回（重做 Redo）
                        PerformRedo();
                    }
                    else
                    {
                        // Ctrl + Z: 撤回（Undo）
                        PerformUndo();
                    }
                    return;
                }
                else if (e.Key == VirtualKey.Y && !isShift)
                {
                    // 兼容 Ctrl + Y 重做
                    e.Handled = true;
                    PerformRedo();
                    return;
                }
                else if (e.Key == VirtualKey.S && !isShift)
                {
                    // Ctrl + S: 手动保存日记并播放保存反馈音效
                    e.Handled = true;
                    TriggerManualSave();
                    return;
                }
                else if (e.Key == VirtualKey.T && !isShift)
                {
                    // Ctrl + T: 插入时间戳
                    e.Handled = true;
                    string liveText = GetCleanEditorText();
                    SyncViewModelContent(liveText);
                    ViewModel.InsertTimestamp();
                    return;
                }
            }
        }

        private void OnEditorKeyDown(object sender, KeyRoutedEventArgs e)
        {
            var key = e.Key;
            bool wasComposing = _wasComposingAtKeyDownStart || _isComposing;
            _wasComposingAtKeyDownStart = false;

            // 1. 过滤 Ctrl / Alt 组合命令快捷键 (如 Ctrl+S, Ctrl+T, Ctrl+C, Ctrl+Z, Alt+Tab 等)
            bool isCtrl = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
                           & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;
            bool isAlt = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu)
                          & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;
            if (isCtrl || isAlt)
            {
                return;
            }

            // 2. 过滤独立功能、导航与修饰键 (Shift, Alt, CapsLock, Win, 方向键, F1~F12 等)
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

            // 3. 回车换行专属音效与字纸深回弹（若处于 IME 拼音组合态按回车直接上屏英文，则按常规上屏键发声）
            if (key == VirtualKey.Enter)
            {
                _lastKeyDownSoundTicks = now;
                if (wasComposing)
                {
                    PlayPlatenRecoil(isEnter: false);
                    SoundEffectService.Instance.PlayKeyClick(false);
                }
                else
                {
                    PlayPlatenRecoil(isEnter: true);
                    SoundEffectService.Instance.PlayEnterKey();
                }
                return;
            }

            // 4. 有效击键（含常规字母数字、标点符号、空格退格以及 IME 中文拼音 VirtualKey.ProcessKey = 229）
            _lastKeyDownSoundTicks = now;
            bool isSpecial = (key is VirtualKey.Space or VirtualKey.Back or VirtualKey.Delete or VirtualKey.Tab);
            PlayPlatenRecoil(isEnter: false);
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

            // Cursor movement is frequent in long diaries; use the text already
            // recorded by TextChanged instead of reading the entire document.
            if (ContentEditorBox.Document != null)
            {
                int selStart = Math.Max(0, ContentEditorBox.Document.Selection.StartPosition);
                int selLen = Math.Abs(ContentEditorBox.Document.Selection.Length);
                if (Math.Abs(selStart - _currentConfirmedSnapshot.SelectionStart) > 1 || selLen != _currentConfirmedSnapshot.SelectionLength)
                {
                    _canCoalesceCurrentBurst = false;
                }
                _currentConfirmedSnapshot = new EditorHistorySnapshot(_currentConfirmedSnapshot.Text, selStart, selLen);
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

        private void ResetUndoHistory(string baselineText)
        {
            _undoStack.Clear();
            _redoStack.Clear();
            string safe = baselineText ?? string.Empty;
            _currentConfirmedSnapshot = new EditorHistorySnapshot(safe, safe.Length, 0);
            _lastEditTimestamp = DateTime.MinValue;
            _lastEditDirection = 0;
            _charsSinceLastSnapshot = 0;
            _canCoalesceCurrentBurst = false;
        }

        private void PushToUndoStack(EditorHistorySnapshot snapshot)
        {
            if (_undoStack.Count > 0 && string.Equals(_undoStack[^1].Text, snapshot.Text, StringComparison.Ordinal))
            {
                _undoStack[^1] = snapshot;
                return;
            }

            if (_undoStack.Count >= MaxUndoSteps)
            {
                // 始终保留 index 0（初始加载基线），移除次旧的步骤
                _undoStack.RemoveAt(1);
            }
            _undoStack.Add(snapshot);
        }

        private void RecordUserEditForUndo(string cleanText, bool forceNewStep)
        {
            if (_isUpdatingEditorFromModel || _isPerformingUndoRedo || ViewModel.IsLoading)
                return;

            string prevText = _currentConfirmedSnapshot.Text;
            if (string.Equals(prevText, cleanText, StringComparison.Ordinal))
                return;

            DateTime now = DateTime.UtcNow;
            double elapsedMs = (now - _lastEditTimestamp).TotalMilliseconds;
            int deltaLen = cleanText.Length - prevText.Length;
            int editDirection = deltaLen > 0 ? 1 : (deltaLen < 0 ? -1 : 0);

            int selStart = Math.Max(0, ContentEditorBox.Document.Selection.StartPosition);
            int selLen = Math.Abs(ContentEditorBox.Document.Selection.Length);

            bool shouldStartNewStep = forceNewStep
                || !_canCoalesceCurrentBurst
                || _undoStack.Count == 0
                || elapsedMs > 450
                || editDirection != _lastEditDirection
                || Math.Abs(deltaLen) > 1
                || _charsSinceLastSnapshot >= 10;

            if (!shouldStartNewStep && deltaLen == 1 && selStart > 0 && selStart <= cleanText.Length)
            {
                char insertedChar = cleanText[selStart - 1];
                if (insertedChar is '\r' or '\n' or ' ' or '\t'
                    or '，' or '。' or '！' or '？' or '；' or '：' or '、'
                    or ',' or '.' or '!' or '?' or ';' or ':')
                {
                    shouldStartNewStep = true;
                }
            }

            if (shouldStartNewStep)
            {
                PushToUndoStack(_currentConfirmedSnapshot);
                _canCoalesceCurrentBurst = !forceNewStep && Math.Abs(deltaLen) == 1;
                _charsSinceLastSnapshot = 1;
            }
            else
            {
                _charsSinceLastSnapshot++;
            }

            _redoStack.Clear();
            _currentConfirmedSnapshot = new EditorHistorySnapshot(cleanText, selStart, selLen);
            _lastEditTimestamp = now;
            _lastEditDirection = editDirection;
        }

        private void PerformUndo()
        {
            if (_isUpdatingEditorFromModel || _isPerformingUndoRedo || ViewModel.IsLoading)
                return;

            string currentLiveText = GetCleanEditorText();
            if (!string.Equals(currentLiveText, _currentConfirmedSnapshot.Text, StringComparison.Ordinal))
            {
                RecordUserEditForUndo(currentLiveText, forceNewStep: true);
            }
            _isComposing = false;

            if (_undoStack.Count == 0)
                return;

            int lastIdx = _undoStack.Count - 1;
            EditorHistorySnapshot targetSnapshot = _undoStack[lastIdx];
            _undoStack.RemoveAt(lastIdx);

            _redoStack.Push(_currentConfirmedSnapshot);
            ApplyHistorySnapshot(targetSnapshot);
        }

        private void PerformRedo()
        {
            if (_isUpdatingEditorFromModel || _isPerformingUndoRedo || ViewModel.IsLoading)
                return;

            _isComposing = false;

            if (_redoStack.Count == 0)
                return;

            EditorHistorySnapshot targetSnapshot = _redoStack.Pop();
            PushToUndoStack(_currentConfirmedSnapshot);
            ApplyHistorySnapshot(targetSnapshot);
        }

        private void ApplyHistorySnapshot(EditorHistorySnapshot snapshot)
        {
            _isPerformingUndoRedo = true;
            _canCoalesceCurrentBurst = false;
            _charsSinceLastSnapshot = 0;
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

                _currentConfirmedSnapshot = new EditorHistorySnapshot(actualText, start, end - start);

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
                ResetUndoHistory(actualText);
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

        private void InsertTimestampAccelerator_Invoked(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender, Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
        {
            args.Handled = true;
            string liveText = GetCleanEditorText();
            SyncViewModelContent(liveText);
            ViewModel.InsertTimestamp();
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

        private void SaveAccelerator_Invoked(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender, Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
        {
            args.Handled = true;
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
                if (version != _inkUpdateVersion) return;
                newTexture = await _inkRenderer.LoadTextureAsync(texturePath);
                if (version != _inkUpdateVersion) return;
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

                // RichEditBox may retain explicit formatting on existing runs.
                // Recolor them when the date/theme changes; typing uses the same
                // default color and does not trigger this whole-document pass.
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
