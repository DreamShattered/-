using System;
using System.Collections.Generic;

namespace FocusFreeze.Core
{
    /// <summary>定格的实现方式。</summary>
    public enum FreezeMode
    {
        /// <summary>挂起目标进程：画面停在当下这一帧。</summary>
        Suspend = 0,
        /// <summary>向游戏发送暂停键（东方系列为 ESC）：由游戏自己暂停，状态最干净。</summary>
        PauseKey = 1
    }

    /// <summary>单个程序的定格策略。</summary>
    public sealed class GameProfile
    {
        /// <summary>进程名，不含 .exe，不区分大小写。</summary>
        public string ProcessName { get; set; } = "";
        public string DisplayName { get; set; } = "";
        /// <summary>是否允许挂起该进程来定格画面。</summary>
        public bool SuspendProcess { get; set; } = true;
        /// <summary>
        /// 该游戏的冻结时长上限（秒）。0 或负数表示不限制，
        /// 此时冻结时长完全等于「音频时长 + 偏移」。
        /// </summary>
        public double SuspendCapSeconds { get; set; } = 0.0;
        /// <summary>该程序推荐的定格方式（来自本机实测结论）。</summary>
        public FreezeMode PreferredMode { get; set; } = FreezeMode.Suspend;
        public string Note { get; set; } = "";
    }

    /// <summary>
    /// 游戏适配档案。表中的数据全部来自本机实测（渲染 API、是否独占全屏、
    /// 覆盖层可见性、挂起定格与恢复行为）。
    ///
    /// 冻结时长上限默认不限制（SuspendCapSeconds = 0），
    /// 需要限制时可在 config.json 的 GameProfiles 数组里覆盖。
    /// </summary>
    public static class GameProfiles
    {
        private static readonly List<GameProfile> Builtin = new List<GameProfile>
        {
            new GameProfile
            {
                ProcessName = "th09",
                DisplayName = "东方花映塚 (D3D8 -> dgVoodoo2 -> D3D11)",
                SuspendProcess = true,
                SuspendCapSeconds = 0.0,
                Note = "启动瞬间会短暂切到 60Hz 再回 240Hz。窗口为全屏尺寸但非独占，覆盖层可见。"
                     + "实测：挂起后画面差异 0.0%（完全定格），恢复后游戏自行继续。"
            },
            new GameProfile
            {
                ProcessName = "th10",
                DisplayName = "东方风神录 (D3D9)",
                SuspendProcess = true,
                SuspendCapSeconds = 0.0,
                PreferredMode = FreezeMode.PauseKey,
                Note = "必须经 Steam 启动，启动时会弹出模式选择框，需选 [ウィンドウ] 窗口模式；"
                     + "窗口模式下 646x520、刷新率不变、覆盖层可见、GDI 可抓画面。"
                     + "实测：ESC = 暂停/解除暂停（游戏内按 Z 五次进入对局后验证）。"
                     + "本档案默认发送暂停键而非挂起进程。"
            },
            new GameProfile
            {
                ProcessName = "th06nc",
                DisplayName = "东方红魔乡 新典 (D3D11 / x64)",
                SuspendProcess = true,
                SuspendCapSeconds = 0.0,
                Note = "D3D11 + DXGI + XAudio2，2582x1656 无边框窗口，非独占全屏，覆盖层可直接盖住。"
            },
            new GameProfile { ProcessName = "th06", DisplayName = "东方红魔乡 (原版 D3D8)", SuspendProcess = true, Note = "老引擎，建议窗口模式。" },
            new GameProfile { ProcessName = "th07", DisplayName = "东方妖妖梦 (D3D8)", SuspendProcess = true, Note = "老引擎，建议窗口模式。" },
            new GameProfile { ProcessName = "th08", DisplayName = "东方永夜抄 (D3D8)", SuspendProcess = true, Note = "老引擎，建议窗口模式。" },
            new GameProfile { ProcessName = "th11", DisplayName = "东方地灵殿 (D3D9)", SuspendProcess = true },
            new GameProfile { ProcessName = "th12", DisplayName = "东方星莲船 (D3D9)", SuspendProcess = true },
            new GameProfile { ProcessName = "th09c", DisplayName = "东方花映塚 中文版", SuspendProcess = true, Note = "桌面版副本。" }
        };

        public static GameProfile Match(string processName)
        {
            if (!string.IsNullOrWhiteSpace(processName))
            {
                string name = processName.Trim();
                if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    name = name.Substring(0, name.Length - 4);
                foreach (GameProfile p in Builtin)
                {
                    if (string.Equals(p.ProcessName, name, StringComparison.OrdinalIgnoreCase)) return p;
                }
            }
            return new GameProfile
            {
                ProcessName = "",
                DisplayName = "",
                SuspendProcess = true,
                SuspendCapSeconds = 0.0,
                Note = "未收录的程序，使用全局设置。"
            };
        }

        public static IReadOnlyList<GameProfile> All { get { return Builtin; } }

        /// <summary>写入 config.json 里的自定义档案优先，没有则用内置。</summary>
        public static GameProfile Match(string processName, List<GameProfile> custom)
        {
            if (custom != null && custom.Count > 0)
            {
                string name = (processName ?? "").Trim();
                if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    name = name.Substring(0, name.Length - 4);
                foreach (GameProfile p in custom)
                {
                    if (string.Equals(p.ProcessName, name, StringComparison.OrdinalIgnoreCase)) return p;
                }
            }
            return Match(processName);
        }
    }
}
