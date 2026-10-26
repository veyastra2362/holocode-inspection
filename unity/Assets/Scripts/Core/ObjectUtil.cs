using UnityEngine;

namespace Inspection
{
    /// <summary>
    /// 对象销毁辅助。
    ///
    /// 为什么需要它：Unity 在**编辑模式**（包括 EditMode 单元测试）里调用
    /// <c>Object.Destroy</c> 会打印错误且“延迟到下一帧”才销毁，而编辑模式没有下一帧，
    /// 结果是对象其实没被销毁——标记仍会挡住射线、测试里断言移除也会失败。
    /// 运行时则必须用 <c>Destroy</c>（<c>DestroyImmediate</c> 在运行时是危险用法）。
    ///
    /// 这一行为正是 Unity Test Runner 暴露出来的（见 AI_LOG P-013），
    /// 仅靠 .NET 桩无法发现。
    /// </summary>
    public static class ObjectUtil
    {
        /// <summary>按当前是否处于播放模式，选择 Destroy 或 DestroyImmediate。</summary>
        public static void SafeDestroy(Object target)
        {
            if (target == null)
            {
                return;
            }
#if UNITY_EDITOR
            if (!Application.isPlaying)
            {
                Object.DestroyImmediate(target);
                return;
            }
#endif
            Object.Destroy(target);
        }
    }
}
