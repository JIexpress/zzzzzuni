// SettingsBootApplier.cs —— 开机应用点（TASK-A-R9-01）：
// 修掉「设置只在打开设置面板时才从 settings.json 载入」的根因
// （SettingsModel.cs 的 Load() 原先只有 SettingsEntry.OpenSettings /
//  GameBootstrap.OpenSettings 两个调用点）。没有这一个点，玩家上次存的
//  动态分辨率=开 到下次启动都不会生效，两行开关等于摆设。

using UnityEngine;
using ZFrame.Core;

namespace ZFrame.View
{
    /// <summary>
    /// 冷启动时把 settings.json 应用到引擎（TASK-A-R9-01 的根因修复）。
    ///
    /// 时机：<see cref="RuntimeInitializeLoadType.AfterSceneLoad"/> —— 必须在
    /// <see cref="FrameGovernorHost"/> 的 BeforeSceneLoad 自举之后，否则
    /// <c>FrameGovernorHost.Instance</c> 还是 null，两行的 apply 会落空。
    ///
    /// 路径：与设置面板完全同一条 —— 建 <see cref="SettingsModel"/> 后调
    /// <see cref="SettingsModel.Load"/>，由它按定义顺序重放每个 Apply 回调。
    /// 因此这里没有第二套解析/生效逻辑，也不改变 settings.json 的任何格式。
    ///
    /// 容错：无文件 / 空文件 / 坏 JSON / 高版本 / 未知键 / 类型不符，全部由
    /// <see cref="SettingsModel.Load"/> 静默跳过；全新用户（无 settings.json）
    /// 的返回值是 0，引擎状态一个字段都不动（零破坏默认）。
    /// </summary>
    public static class SettingsBootApplier
    {
        private const string LogChannel = "Settings";

        /// <summary>开机自举：把磁盘上的 settings.json 应用一遍（每会话一次）。</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoBoot()
        {
            ApplyFromDisk();
        }

        /// <summary>
        /// 按 <paramref name="rootDirectory"/> 里的 settings.json 应用全部已知设置，
        /// 返回真正被应用（类型正确且回调跑过）的条目数。
        /// <paramref name="rootDirectory"/> 为 null/空 = 真实
        /// <see cref="Application.persistentDataPath"/>；测试必须注入临时目录。
        /// </summary>
        public static int ApplyFromDisk(string rootDirectory = null)
        {
            var model = new SettingsModel(SettingsRegistry.BuildDefaults(), rootDirectory);
            int applied = model.Load();
            if (applied > 0)
            {
                // 证据锚点：启动日志里能看到"已应用 N 项"以及宿主上的实际生效值。
                ZLog.Info(LogChannel,
                    $"settings.json 开机应用 {applied} 项（不经设置面板）；{DescribeGovernorState()}");
            }

            return applied;
        }

        /// <summary>日志里的生效证据：直接读宿主（而不是 model 的值镜像），证明值真的抵达了 governor。</summary>
        private static string DescribeGovernorState()
        {
            FrameGovernorHost host = FrameGovernorHost.Instance;
            if (host == null)
            {
                return "governor host 尚未自举";
            }

            float targetMs = host.Governor != null ? host.Governor.TargetMs : 0f;
            return $"governor enabled={host.IsGovernorEnabled}, target={host.TargetFps:0}fps, targetMs={targetMs:0.0}ms";
        }
    }
}
