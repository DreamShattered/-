using System;
using System.Collections.Generic;
using System.IO;

namespace FocusFreeze.Core
{
    /// <summary>
    /// 素材库：从指定文件夹里挑选图片与音频，并支持两者之间的绑定关系。
    ///
    /// 配对方式：
    ///   ByName   同名配对（A.png 配 A.mp3）
    ///   ByIndex  按顺序配对（第 N 张图配第 N 个音频）
    ///   None     各自独立选取
    ///
    /// 选取顺序：随机（洗牌轮换，一轮内不重复）或按名称顺序循环。
    /// </summary>
    public sealed class AssetLibrary
    {
        private static readonly string[] ImageExts = { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp" };
        private static readonly string[] AudioExts = { ".wav", ".mp3", ".m4a", ".aac", ".wma", ".flac", ".ogg" };
        private static readonly string[] VideoExts = { ".mp4", ".m4v", ".wmv", ".avi", ".mov", ".mkv", ".webm" };

        private readonly List<string> _images = new List<string>();
        private readonly List<string> _audios = new List<string>();
        private readonly List<string> _videos = new List<string>();
        private readonly List<int> _videoOrder = new List<int>();
        private readonly Dictionary<string, string> _audioByStem =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly Random _rng = new Random();
        private readonly List<int> _imageOrder = new List<int>();
        private readonly List<int> _audioOrder = new List<int>();
        private int _pairCursor;

        public int ImageCount { get { return _images.Count; } }
        public int AudioCount { get { return _audios.Count; } }
        public int VideoCount { get { return _videos.Count; } }
        public string LastScanNote { get; private set; } = "尚未扫描";

        public void Reload(AppConfig cfg)
        {
            _images.Clear();
            _audios.Clear();
            _videos.Clear();
            _audioByStem.Clear();
            _imageOrder.Clear();
            _audioOrder.Clear();
            _videoOrder.Clear();
            _pairCursor = 0;

            try
            {
                if (!string.IsNullOrWhiteSpace(cfg.ImageFolder) && Directory.Exists(cfg.ImageFolder))
                {
                    foreach (string f in Directory.EnumerateFiles(cfg.ImageFolder, "*", SearchOption.TopDirectoryOnly))
                    {
                        string ext = Path.GetExtension(f).ToLowerInvariant();
                        if (Array.IndexOf(ImageExts, ext) >= 0) _images.Add(f);
                    }
                    _images.Sort(StringComparer.OrdinalIgnoreCase);
                }

                if (!string.IsNullOrWhiteSpace(cfg.AudioFolder) && Directory.Exists(cfg.AudioFolder))
                {
                    foreach (string f in Directory.EnumerateFiles(cfg.AudioFolder, "*", SearchOption.TopDirectoryOnly))
                    {
                        string ext = Path.GetExtension(f).ToLowerInvariant();
                        if (Array.IndexOf(AudioExts, ext) >= 0)
                        {
                            _audios.Add(f);
                            _audioByStem[Path.GetFileNameWithoutExtension(f)] = f;
                        }
                    }
                    _audios.Sort(StringComparer.OrdinalIgnoreCase);
                }

                if (!string.IsNullOrWhiteSpace(cfg.VideoFolder) && Directory.Exists(cfg.VideoFolder))
                {
                    foreach (string f in Directory.EnumerateFiles(cfg.VideoFolder, "*", SearchOption.TopDirectoryOnly))
                    {
                        string ext = Path.GetExtension(f).ToLowerInvariant();
                        if (Array.IndexOf(VideoExts, ext) >= 0) _videos.Add(f);
                    }
                    _videos.Sort(StringComparer.OrdinalIgnoreCase);
                }

                LastScanNote = "图片 " + _images.Count + " 张 / 音频 " + _audios.Count
                             + " 个 / 视频 " + _videos.Count + " 段";
            }
            catch (Exception ex)
            {
                LastScanNote = "扫描失败：" + ex.Message;
            }
        }

        /// <summary>按配置挑出一组素材。固定模式直接返回配置里的路径。</summary>
        public void Pick(AppConfig cfg, out string imagePath, out string audioPath)
        {
            imagePath = cfg.ImagePath;
            audioPath = cfg.AudioPath;
            if (cfg.AssetSource == AssetSource.Fixed) return;

            bool sequential = cfg.AssetSource == AssetSource.FolderSequential;

            // 按顺序配对：图片与音频取同一序号，保证第 N 张配第 N 个。
            if (cfg.Pairing == PairMode.ByIndex && _images.Count > 0 && _audios.Count > 0)
            {
                int n = Math.Min(_images.Count, _audios.Count);
                int idx = _pairCursor % n;
                _pairCursor++;
                imagePath = _images[idx];
                audioPath = _audios[idx];
                return;
            }

            string img = _images.Count > 0
                ? (sequential ? NextSequential(_images, _imageOrder) : NextRandom(_images, _imageOrder))
                : cfg.ImagePath;

            string aud = "";
            if (cfg.Pairing == PairMode.ByName && _images.Count > 0 && _audios.Count > 0)
            {
                string stem = Path.GetFileNameWithoutExtension(img);
                string found;
                if (_audioByStem.TryGetValue(stem, out found)) aud = found;
            }

            if (string.IsNullOrEmpty(aud) && _audios.Count > 0 && cfg.Pairing != PairMode.None)
            {
                aud = sequential ? NextSequential(_audios, _audioOrder) : NextRandom(_audios, _audioOrder);
            }
            if (string.IsNullOrEmpty(aud) && cfg.Pairing == PairMode.None && _audios.Count > 0)
            {
                aud = sequential ? NextSequential(_audios, _audioOrder) : NextRandom(_audios, _audioOrder);
            }

            if (string.IsNullOrEmpty(aud)) aud = cfg.AudioPath;
            if (string.IsNullOrEmpty(img)) img = cfg.ImagePath;

            imagePath = img;
            audioPath = aud;
        }

        /// <summary>按配置挑一个视频。固定模式或没有视频文件夹时返回配置里的视频路径。</summary>
        public string PickVideo(AppConfig cfg)
        {
            if (cfg.AssetSource == AssetSource.Fixed || _videos.Count == 0)
                return string.IsNullOrWhiteSpace(cfg.VideoPath) ? "" : cfg.VideoPath;

            bool sequential = cfg.AssetSource == AssetSource.FolderSequential;
            return sequential ? NextSequential(_videos, _videoOrder) : NextRandom(_videos, _videoOrder);
        }

        /// <summary>当前扫描到的全部媒体路径（音频+视频），用于后台预热时长。</summary>
        public List<string> AllMediaPaths()
        {
            List<string> all = new List<string>();
            all.AddRange(_audios);
            all.AddRange(_videos);
            if (!string.IsNullOrWhiteSpace(_currentFixedAudio)) all.Add(_currentFixedAudio);
            if (!string.IsNullOrWhiteSpace(_currentFixedVideo)) all.Add(_currentFixedVideo);
            return all;
        }

        private string _currentFixedAudio = "";
        private string _currentFixedVideo = "";

        /// <summary>记录固定模式下使用到的文件，供预热。</summary>
        public void NoteFixedAssets(string audio, string video)
        {
            _currentFixedAudio = audio ?? "";
            _currentFixedVideo = video ?? "";
        }

        private string NextRandom(List<string> list, List<int> order)
        {
            if (order.Count == 0)
            {
                for (int i = 0; i < list.Count; i++) order.Add(i);
                for (int i = order.Count - 1; i > 0; i--)
                {
                    int j = _rng.Next(i + 1);
                    int t = order[i]; order[i] = order[j]; order[j] = t;
                }
            }
            int last = order.Count - 1;
            int idx = order[last];
            order.RemoveAt(last);
            return list[idx];
        }

        private string NextSequential(List<string> list, List<int> order)
        {
            if (order.Count == 0)
            {
                for (int i = 0; i < list.Count; i++) order.Add(i);
            }
            int idx = order[0];
            order.RemoveAt(0);
            return list[idx];
        }
    }
}
