using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FocusFreeze.Core
{
    /// <summary>持续按住一个按键时的计次规则。</summary>
    public enum HoldMode
    {
        /// <summary>按住只算一次：只算真正按下的那一下，系统的自动重复被忽略。</summary>
        PressOnce = 0,
        /// <summary>按住算多次：每按住 HoldRepeatIntervalMs 毫秒计一次。</summary>
        HoldRepeat = 1
    }

    /// <summary>触发时使用哪一类素材。</summary>
    public enum AssetKind
    {
        /// <summary>图片 + 音频（可分别选择并配对）。</summary>
        ImageAudio = 0,
        /// <summary>直接播放一段视频（自带画面与声音，无需配对）。</summary>
        Video = 1
    }

    /// <summary>素材来源。</summary>
    public enum AssetSource
    {
        /// <summary>固定使用单个图片与单个音频。</summary>
        Fixed = 0,
        /// <summary>从文件夹里随机选取（洗牌轮换，一轮内不重复）。</summary>
        FolderRandom = 1,
        /// <summary>从文件夹里按名称顺序循环选取。</summary>
        FolderSequential = 2
    }

    /// <summary>图片与音频的配对方式。</summary>
    public enum PairMode
    {
        /// <summary>不配对：图片与音频各自独立选取。</summary>
        None = 0,
        /// <summary>同名配对：A.png 找 A.mp3 / A.wav 等。</summary>
        ByName = 1,
        /// <summary>按顺序配对：第 N 张图配第 N 个音频。</summary>
        ByIndex = 2
    }

    /// <summary>触发时对前台画面的遮挡方式。</summary>
    public enum CoverMode
    {
        /// <summary>不遮挡。</summary>
        None = 0,
        /// <summary>高斯模糊。</summary>
        Blur = 1,
        /// <summary>马赛克（像素化）。</summary>
        Pixelate = 2,
        /// <summary>纯色遮挡。</summary>
        Solid = 3,
        /// <summary>仅暗化：画面仍可见但被压暗，看不出细节。</summary>
        Dim = 4,
        /// <summary>自定义背景图：用指定的图片铺满屏幕（此模式不抓屏）。</summary>
        CustomImage = 5
    }

    /// <summary>冻结时长的决定方式。</summary>
    public enum SuspendDurationMode
    {
        /// <summary>跟随展示时长（即音频/视频时长）加上时间偏移，默认方式。</summary>
        MatchOverlay = 0,
        /// <summary>固定秒数。</summary>
        Fixed = 1
    }

    /// <summary>图片/音频展示时长的决定方式。</summary>
    public enum OverlayDurationMode
    {
        /// <summary>固定秒数。</summary>
        Fixed = 0,
        /// <summary>跟随音频时长（音频放完就收起）。</summary>
        AudioLength = 1,
        /// <summary>音频时长 + 额外秒数。</summary>
        AudioPlus = 2
    }

    /// <summary>所有可调参数，持久化到 exe 同目录的 config.json。</summary>
    public sealed class AppConfig
    {
        public bool Enabled { get; set; } = true;

        /// <summary>统计窗口长度（毫秒）。</summary>
        public int WindowMs { get; set; } = 1000;

        /// <summary>窗口内输入事件达到该数量即触发。</summary>
        public int ThresholdCount { get; set; } = 25;

        /// <summary>两次触发之间的强制冷却（毫秒）。</summary>
        public int CooldownMs { get; set; } = 20000;

        /// <summary>挂起进程（定格画面）的时长，秒。与图片展示时长相互独立。</summary>
        public double SuspendSeconds { get; set; } = 1.0;

        /// <summary>冻结时长的决定方式：默认跟随音频/视频时长，也可用固定秒数。</summary>
        public SuspendDurationMode SuspendMode { get; set; } = SuspendDurationMode.MatchOverlay;

        /// <summary>跟随音频时长时的时间偏移（秒），范围 -0.5 ~ +0.5：正数多冻一会儿，负数提前放开。</summary>
        public double SuspendOffsetSeconds { get; set; } = 0.0;

        /// <summary>图片/视频展示时长相对音频时长的偏移（秒），范围 -0.5 ~ +0.5。</summary>
        public double OverlayOffsetSeconds { get; set; } = 0.0;

        /// <summary>遮挡层是否持续到展示结束（游戏已恢复运行但屏幕仍被盖住，防止音频期间看到局势）。</summary>
        public bool CoverUntilOverlayEnds { get; set; } = true;

        /// <summary>图片与音频的展示时长，秒。OverlayMode = Fixed 时使用。</summary>
        public double OverlaySeconds { get; set; } = 3.0;

        /// <summary>展示时长的决定方式：固定 / 跟随音频时长 / 音频时长+额外。</summary>
        public OverlayDurationMode OverlayMode { get; set; } = OverlayDurationMode.AudioLength;

        /// <summary>OverlayMode = AudioPlus 时，在音频时长之外多加的秒数。</summary>
        public double OverlayExtraSeconds { get; set; } = 1.0;

        /// <summary>按住按键的计次规则。</summary>
        public HoldMode KeyHoldMode { get; set; } = HoldMode.PressOnce;

        /// <summary>HoldRepeat 模式下，按住多少毫秒算一次操作。</summary>
        public int HoldRepeatIntervalMs { get; set; } = 500;

        /// <summary>恢复后请求目标窗口重绘（部分 D3D 程序挂起恢复后画面不刷新）。</summary>
        public bool ForceRedrawAfterResume { get; set; } = true;

        /// <summary>触发时遮挡前台画面的方式。</summary>
        public CoverMode Cover { get; set; } = CoverMode.None;

        /// <summary>高斯模糊的强度（半径）。</summary>
        public double BlurRadius { get; set; } = 28.0;

        /// <summary>马赛克块大小（像素）。</summary>
        public int PixelBlockSize { get; set; } = 16;

        /// <summary>暗化程度 0~1，会叠加在所选遮挡方式之上。</summary>
        public double DimOpacity { get; set; } = 0.35;

        /// <summary>纯色遮挡使用的颜色。</summary>
        public string SolidCoverColor { get; set; } = "#000000";

        /// <summary>自定义遮挡背景图路径（Cover = CustomImage 时使用，铺满屏幕）。</summary>
        public string CoverImagePath { get; set; } = "";

        public bool SuspendForeground { get; set; } = true;
        public bool SwallowInput { get; set; } = true;

        public string ImagePath { get; set; } = "";
        public string AudioPath { get; set; } = "";

        /// <summary>素材来源：固定 / 文件夹随机 / 文件夹顺序。</summary>
        public AssetSource AssetSource { get; set; } = AssetSource.Fixed;

        /// <summary>图片文件夹（素材来源为文件夹时使用）。</summary>
        public string ImageFolder { get; set; } = "";

        /// <summary>音频文件夹（素材来源为文件夹时使用）。</summary>
        public string AudioFolder { get; set; } = "";

        /// <summary>图片与音频的配对方式。</summary>
        public PairMode Pairing { get; set; } = PairMode.ByName;

        /// <summary>素材类型：图片+音频，或视频。</summary>
        public AssetKind Kind { get; set; } = AssetKind.ImageAudio;

        /// <summary>单个视频文件（固定模式下使用）。</summary>
        public string VideoPath { get; set; } = "";

        /// <summary>视频文件夹（素材来源为文件夹时使用）。</summary>
        public string VideoFolder { get; set; } = "";

        /// <summary>视频是否全屏铺满（否则按中间偏下小窗显示）。</summary>
        public bool VideoFullscreen { get; set; } = true;

        /// <summary>视频模式下是否一直等到视频播完（否则按展示时长截断）。</summary>
        public bool VideoWaitUntilEnd { get; set; } = true;
        public double AudioVolume { get; set; } = 0.85;

        /// <summary>图片下方的文案，留空则只显示图片。</summary>
        public string Caption { get; set; } = "";

        /// <summary>定格方式：挂起进程，或向游戏发送暂停键。</summary>
        public FreezeMode FreezeMode { get; set; } = FreezeMode.Suspend;

        /// <summary>暂停键的虚拟键码，东方系列默认为 ESC (0x1B)。</summary>
        public int PauseKeyVirtualKey { get; set; } = 0x1B;

        /// <summary>定格期间强制把覆盖层压到最顶层（游戏窗口往往是 TOPMOST）。</summary>
        public bool ForceOverlayTopmost { get; set; } = true;

        /// <summary>用户自定义的游戏档案，优先于内置表。</summary>
        public List<GameProfile> GameProfiles { get; set; } = new List<GameProfile>();

        /// <summary>图片中心所在位置的屏幕高度比例（0.5 = 正中，0.62 = 中间偏下）。</summary>
        public double CenterYRatio { get; set; } = 0.62;

        /// <summary>图片最大占屏宽度比例（图片本身更小时按原尺寸显示，不放大）。</summary>
        public double MaxWidthRatio { get; set; } = 0.8;

        /// <summary>图片最大占屏高度比例，默认 1/3：图片落在屏幕中偏下的三分之一范围内。</summary>
        public double MaxHeightRatio { get; set; } = 0.333;

        /// <summary>这些进程不会被挂起（仍然显示素材并播放音频）。</summary>
        public string[] Blacklist { get; set; } = new[]
        {
            "explorer", "dwm", "csrss", "wininit", "winlogon", "services", "lsass",
            "Registry", "System", "Idle", "Taskmgr", "ShellExperienceHost",
            "StartMenuExperienceHost", "SearchHost", "TextInputHost",
            "ApplicationFrameHost", "SystemSettings", "SecurityHealthService",
            "SecurityHealthSystray", "MsMpEng", "FocusFreeze", "多动症矫正器"
        };

        [JsonIgnore]
        public string FilePath { get; set; } = "";

        private static readonly JsonSerializerOptions Opts = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };

        public static AppConfig LoadOrCreate(string baseDir)
        {
            string assets = Path.Combine(baseDir, "assets");
            string cfgPath = Path.Combine(baseDir, "config.json");

            DefaultAssets.Ensure(assets, out string png, out string wav);

            AppConfig cfg = null;
            if (File.Exists(cfgPath))
            {
                try
                {
                    cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(cfgPath), Opts);
                }
                catch
                {
                    cfg = null;
                }
            }

            if (cfg == null) cfg = new AppConfig();
            cfg.FilePath = cfgPath;
            if (string.IsNullOrWhiteSpace(cfg.ImagePath)) cfg.ImagePath = png;
            if (string.IsNullOrWhiteSpace(cfg.AudioPath)) cfg.AudioPath = wav;
            if (cfg.Blacklist == null || cfg.Blacklist.Length == 0) cfg.Blacklist = new AppConfig().Blacklist;
            cfg.Sanitize();
            try { cfg.Save(); } catch { }
            return cfg;
        }

        public void Sanitize()
        {
            WindowMs = Math.Clamp(WindowMs, 100, 10000);
            ThresholdCount = Math.Clamp(ThresholdCount, 1, 2000);
            CooldownMs = Math.Clamp(CooldownMs, 0, 3600000);
            SuspendSeconds = Math.Clamp(SuspendSeconds, 0.2, 60.0);
            SuspendOffsetSeconds = Math.Clamp(SuspendOffsetSeconds, -0.5, 0.5);
            OverlayOffsetSeconds = Math.Clamp(OverlayOffsetSeconds, -0.5, 0.5);
            OverlaySeconds = Math.Clamp(OverlaySeconds, 0.5, 600.0);
            HoldRepeatIntervalMs = Math.Clamp(HoldRepeatIntervalMs, 30, 5000);
            BlurRadius = Math.Clamp(BlurRadius, 0.0, 120.0);
            PixelBlockSize = Math.Clamp(PixelBlockSize, 2, 128);
            DimOpacity = Math.Clamp(DimOpacity, 0.0, 1.0);
            AudioVolume = Math.Clamp(AudioVolume, 0.0, 1.0);
            CenterYRatio = Math.Clamp(CenterYRatio, 0.05, 0.95);
            MaxWidthRatio = Math.Clamp(MaxWidthRatio, 0.05, 1.0);
            MaxHeightRatio = Math.Clamp(MaxHeightRatio, 0.05, 1.0);
        }

        public void Save()
        {
            if (string.IsNullOrEmpty(FilePath)) return;
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Opts));
        }

        public AppConfig Clone()
        {
            AppConfig c = (AppConfig)MemberwiseClone();
            c.Blacklist = (string[])Blacklist.Clone();
            return c;
        }
    }
}
