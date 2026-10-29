using System;
using System.Numerics;
using Journal.Services;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;

namespace Journal.Controls
{
    /// <summary>
    /// 独立管理编辑器打字机压纸滚筒（Platen）物理回弹微震动合成器动画（零 GC 分配）
    /// </summary>
    public sealed class PlatenHapticController
    {
        private readonly Visual? _paperVisual;
        private readonly Vector3KeyFrameAnimation? _normalRecoilAnim;
        private readonly Vector3KeyFrameAnimation? _enterRecoilAnim;

        public PlatenHapticController(UIElement targetContainer)
        {
            try
            {
                ElementCompositionPreview.SetIsTranslationEnabled(targetContainer, true);
                _paperVisual = ElementCompositionPreview.GetElementVisual(targetContainer);
                var compositor = _paperVisual.Compositor;

                // 常规击键：瞬间下沉 1.1px，50ms 弹性平滑复位
                _normalRecoilAnim = compositor.CreateVector3KeyFrameAnimation();
                _normalRecoilAnim.Duration = TimeSpan.FromMilliseconds(50);
                _normalRecoilAnim.InsertKeyFrame(0.0f, new Vector3(0, 0, 0));
                _normalRecoilAnim.InsertKeyFrame(0.24f, new Vector3(0, 1.1f, 0),
                    compositor.CreateCubicBezierEasingFunction(new Vector2(0.1f, 0.9f), new Vector2(0.2f, 1.0f)));
                _normalRecoilAnim.InsertKeyFrame(1.0f, new Vector3(0, 0, 0),
                    compositor.CreateCubicBezierEasingFunction(new Vector2(0.4f, 0.0f), new Vector2(0.2f, 1.0f)));

                // 回车换行：机架推拉下沉 2.2px，75ms 阻尼复位
                _enterRecoilAnim = compositor.CreateVector3KeyFrameAnimation();
                _enterRecoilAnim.Duration = TimeSpan.FromMilliseconds(75);
                _enterRecoilAnim.InsertKeyFrame(0.0f, new Vector3(0, 0, 0));
                _enterRecoilAnim.InsertKeyFrame(0.20f, new Vector3(0, 2.2f, 0),
                    compositor.CreateCubicBezierEasingFunction(new Vector2(0.1f, 0.9f), new Vector2(0.2f, 1.0f)));
                _enterRecoilAnim.InsertKeyFrame(1.0f, new Vector3(0, 0, 0),
                    compositor.CreateCubicBezierEasingFunction(new Vector2(0.4f, 0.0f), new Vector2(0.2f, 1.0f)));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[PlatenHapticController Init Error] {ex.Message}");
            }
        }

        public void PlayRecoil(bool isEnter)
        {
            if (!AppSettingsService.Instance.CurrentSettings.IsHapticVibrationEnabled || _paperVisual == null)
                return;

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
    }
}
