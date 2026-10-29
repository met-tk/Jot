using System;
using System.Collections.Generic;

namespace Journal.Controls
{
    public readonly struct EditorHistorySnapshot
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

    /// <summary>
    /// 独立管理编辑器受控分段撤回/反撤回历史栈（支持自适应长文内存上限、连续输入合并与初始基线保护）
    /// </summary>
    public sealed class EditorUndoRedoManager
    {
        private const int DefaultMaxUndoSteps = 160;
        private const int LongDocMaxUndoSteps = 60;
        private const int LongDocCharThreshold = 15000;

        private readonly List<EditorHistorySnapshot> _undoStack = new();
        private readonly Stack<EditorHistorySnapshot> _redoStack = new();
        private EditorHistorySnapshot _currentConfirmedSnapshot = new(string.Empty, 0, 0);
        private DateTime _lastEditTimestamp = DateTime.MinValue;
        private int _lastEditDirection;
        private int _charsSinceLastSnapshot;
        private bool _canCoalesceCurrentBurst;

        public EditorHistorySnapshot CurrentSnapshot => _currentConfirmedSnapshot;

        public void BreakCoalescing()
        {
            _canCoalesceCurrentBurst = false;
        }

        public void ResetHistory(string baselineText)
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

        public void UpdateSelection(int selStart, int selLen)
        {
            selStart = Math.Max(0, selStart);
            selLen = Math.Max(0, selLen);
            if (Math.Abs(selStart - _currentConfirmedSnapshot.SelectionStart) > 1 ||
                selLen != _currentConfirmedSnapshot.SelectionLength)
            {
                _canCoalesceCurrentBurst = false;
            }
            _currentConfirmedSnapshot = new EditorHistorySnapshot(_currentConfirmedSnapshot.Text, selStart, selLen);
        }

        public void RecordEdit(string cleanText, int selStart, int selLen, bool forceNewStep)
        {
            string prevText = _currentConfirmedSnapshot.Text;
            if (string.Equals(prevText, cleanText, StringComparison.Ordinal))
                return;

            DateTime now = DateTime.UtcNow;
            double elapsedMs = (now - _lastEditTimestamp).TotalMilliseconds;
            int deltaLen = cleanText.Length - prevText.Length;
            int editDirection = deltaLen > 0 ? 1 : (deltaLen < 0 ? -1 : 0);

            selStart = Math.Max(0, selStart);
            selLen = Math.Max(0, selLen);

            bool isLongDoc = cleanText.Length >= LongDocCharThreshold;
            int coalesceCharLimit = isLongDoc ? 24 : 12;
            double coalesceTimeWindowMs = isLongDoc ? 650 : 450;

            bool shouldStartNewStep = forceNewStep
                || !_canCoalesceCurrentBurst
                || _undoStack.Count == 0
                || elapsedMs > coalesceTimeWindowMs
                || editDirection != _lastEditDirection
                || Math.Abs(deltaLen) > 1
                || _charsSinceLastSnapshot >= coalesceCharLimit;

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
                PushToUndoStack(_currentConfirmedSnapshot, isLongDoc);
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

        public bool TryUndo(string currentLiveText, int currentSelStart, int currentSelLen, out EditorHistorySnapshot targetSnapshot)
        {
            if (!string.Equals(currentLiveText, _currentConfirmedSnapshot.Text, StringComparison.Ordinal))
            {
                RecordEdit(currentLiveText, currentSelStart, currentSelLen, forceNewStep: true);
            }

            if (_undoStack.Count == 0)
            {
                targetSnapshot = default;
                return false;
            }

            int lastIdx = _undoStack.Count - 1;
            targetSnapshot = _undoStack[lastIdx];
            _undoStack.RemoveAt(lastIdx);

            _redoStack.Push(_currentConfirmedSnapshot);
            return true;
        }

        public bool TryRedo(out EditorHistorySnapshot targetSnapshot)
        {
            if (_redoStack.Count == 0)
            {
                targetSnapshot = default;
                return false;
            }

            targetSnapshot = _redoStack.Pop();
            bool isLongDoc = _currentConfirmedSnapshot.Text.Length >= LongDocCharThreshold;
            PushToUndoStack(_currentConfirmedSnapshot, isLongDoc);
            return true;
        }

        public void ConfirmAppliedSnapshot(string actualText, int start, int length)
        {
            _canCoalesceCurrentBurst = false;
            _charsSinceLastSnapshot = 0;
            _currentConfirmedSnapshot = new EditorHistorySnapshot(actualText, start, length);
        }

        private void PushToUndoStack(EditorHistorySnapshot snapshot, bool isLongDoc)
        {
            if (_undoStack.Count > 0 && string.Equals(_undoStack[^1].Text, snapshot.Text, StringComparison.Ordinal))
            {
                _undoStack[^1] = snapshot;
                return;
            }

            int maxSteps = isLongDoc ? LongDocMaxUndoSteps : DefaultMaxUndoSteps;
            while (_undoStack.Count >= maxSteps && _undoStack.Count > 1)
            {
                // 始终保留 index 0（初始加载基线），移除次旧的步骤
                _undoStack.RemoveAt(1);
            }
            _undoStack.Add(snapshot);
        }
    }
}
