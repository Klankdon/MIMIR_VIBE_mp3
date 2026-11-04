using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace SimpleMp3Player
{
    public class SkinConfig
    {
        public int WindowWidth { get; set; } = 600;
        public int WindowHeight { get; set; } = 400;
        public Color BackColor { get; set; } = Color.FromArgb(30, 30, 30);
        public Color ForeColor { get; set; } = Color.White;
        public int BarCount { get; set; } = 64;
        public float BarHeightScale { get; set; } = 1.0f;
        public string WallpaperPath { get; set; } = string.Empty;
        public string WallpaperStretch { get; set; } = "tile";
        public float WallpaperOpacity { get; set; } = 1.0f;
    }

    public class SimpleAudioPlayer : IDisposable
    {
        private readonly WaveFileReader _reader;
        private readonly WaveOutEvent _output;
        private readonly BufferedWaveProvider _provider;

        public event EventHandler PlaybackFinished;

        public SimpleAudioPlayer(string filePath)
        {
            _reader = new WaveFileReader(filePath);
            _provider = new BufferedWaveProvider(_reader.WaveFormat) { ReadFully = true };
            _output = new WaveOutEvent();
            _output.Init(_provider);
            _output.PlaybackStopped += (_, __) => PlaybackFinished?.Invoke(this, EventArgs.Empty);
        }

        public void Play() => _output.Play();
        public void Pause() => _output.Pause();
        public void Stop()
        {
            _output.Stop();
            _provider.ClearBuffer();
        }

        public bool IsPlaying => _output.PlaybackState == PlaybackState.Playing;
        public bool IsPaused => _output.PlaybackState == PlaybackState.Paused;

        public double CurrentPosition => _reader.CurrentTime.TotalSeconds;
        public double TotalDuration => _reader.TotalTime.TotalSeconds;

        public void SetPosition(double seconds)
        {
            if (seconds < 0) seconds = 0;
            if (seconds > TotalDuration) seconds = TotalDuration;
            _reader.CurrentTime = TimeSpan.FromSeconds(seconds);
        }

        public void Feed()
        {
            const int blockSize = 1024;
            byte[] buffer = new byte[blockSize];
            int read;
            while ((read = _reader.Read(buffer, 0, buffer.Length)) > 0)
                _provider.AddSamples(buffer, 0, read);
            // Remove or omit _provider.Flush(); here
        }

        public void Dispose()
        {
            _reader?.Dispose();
            _output?.Dispose();
        }
    }

    public class AudioVisualizer
    {
        public float[] Bars { get; }
        private readonly float[] _fftBuffer;

        public AudioVisualizer(int barCount = 64)
        {
            Bars = new float[barCount];
            _fftBuffer = new float[barCount * 2];
        }

        public void Update(float[] samples)
        {
            int binSize = samples.Length / Bars.Length;
            for (int i = 0; i < Bars.Length; i++)
            {
                int start = i * binSize;
                float sum = 0;
                for (int j = 0; j < binSize && (start + j) < samples.Length; j++)
                {
                    float v = samples[start + j];
                    sum += v * v;
                }
                Bars[i] = (float)Math.Sqrt(sum / binSize) * 2f;
            }
        }
    }

    public class Mp3PlayerForm : Form
    {
        private readonly SkinConfig _skin;
        private readonly List<string> _playlist = new();
        private int _currentTrackIndex = -1;
        private SimpleAudioPlayer _player;
        private readonly AudioVisualizer _visualizer;
        private readonly System.Windows.Forms.Timer _timer = new() { Interval = 250 };
        private readonly TextBox _txtPlaylist;
        private readonly Button _btnPlay;
        private readonly Button _btnPause;
        private readonly Button _btnStop;
        private readonly ProgressBar _pbProgress;
        private readonly Label _lblInfo;
        private readonly PictureBox _pbVisualizer;
        private Bitmap _wallpaper;

        public Mp3PlayerForm()
        {
            _skin = LoadSkin();
            _visualizer = new AudioVisualizer(_skin.BarCount);
            ApplyWallpaper();

            Text = "Simple MP3 Player";
            Size = new Size(_skin.WindowWidth, _skin.WindowHeight);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = _skin.BackColor;
            ForeColor = _skin.ForeColor;
            Font = new Font("Segoe UI", 9F, FontStyle.Regular);

            var pnlTop = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 40,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowOnly,
                Padding = new Padding(5),
                BackColor = BackColor
            };

            _btnPlay = new Button { Text = "▶ Play", Width = 80 };
            _btnPause = new Button { Text = "‖ Pause", Width = 80 };
            _btnStop = new Button { Text = "■ Stop", Width = 80 };
            _btnPlay.Click += BtnPlay_Click;
            _btnPause.Click += BtnPause_Click;
            _btnStop.Click += BtnStop_Click;

            pnlTop.Controls.Add(_btnPlay);
            pnlTop.Controls.Add(_btnPause);
            pnlTop.Controls.Add(_btnStop);

            _pbProgress = new ProgressBar { Dock = DockStyle.Top, Height = 12, Value = 0 };
            _lblInfo = new Label
            {
                Dock = DockStyle.Top,
                Height = 24,
                TextAlign = ContentAlignment.MiddleCenter,
                Text = "No track loaded"
            };

            _pbVisualizer = new PictureBox
            {
                Dock = DockStyle.Fill,
                SizeMode = PictureBoxSizeMode.StretchImage,
                BackColor = Color.Black
            };
            _pbVisualizer.Paint += _pbVisualizer_Paint;

            _txtPlaylist = new TextBox
            {
                Dock = DockStyle.Right,
                Width = 180,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                BackColor = Color.FromArgb(45, 45, 45),
                ForeColor = ForeColor
            };

            var split = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Horizontal,
                SplitterDistance = (int)(Height * 0.70f)
            };
            split.Panel1.Controls.Add(_pbVisualizer);
            split.Panel2.Controls.Add(_txtPlaylist);

            Controls.Add(split);
            Controls.Add(_lblInfo);
            Controls.Add(_pbProgress);
            Controls.Add(pnlTop);

            _timer.Tick += Timer_Tick;
            _timer.Start();

            LoadDefaultPlaylist();
        }

        private SkinConfig LoadSkin()
        {
            const string skinPath = "skin.json";
            if (File.Exists(skinPath))
            {
                try
                {
                    var json = File.ReadAllText(skinPath);
                    return JsonSerializer.Deserialize<SkinConfig>(json) ?? new SkinConfig();
                }
                catch { }
            }
            var defaultSkin = new SkinConfig();
            File.WriteAllText(skinPath, JsonSerializer.Serialize(defaultSkin, new JsonSerializerOptions { WriteIndented = true }));
            return defaultSkin;
        }

        private void ApplyWallpaper()
        {
            string wallpaperFile = Path.Combine(Directory.GetCurrentDirectory(), "wallpaper", _skin.WallpaperPath);
            if (File.Exists(wallpaperFile))
            {
                try
                {
                    _wallpaper = new Bitmap(wallpaperFile);
                }
                catch { }
            }
        }

        private void LoadDefaultPlaylist()
        {
            var files = Directory.GetFiles(Directory.GetCurrentDirectory(), "*.mp3");
            foreach (var f in files)
                _playlist.Add(Path.GetFileName(f));
            RefreshPlaylistDisplay();
            if (_playlist.Count > 0) _currentTrackIndex = 0;
        }

        private void RefreshPlaylistDisplay()
        {
            _txtPlaylist.Clear();
            for (int i = 0; i < _playlist.Count; i++)
                _txtPlaylist.AppendText($"{i + 1}. {_playlist[i]}{Environment.NewLine}");
        }

        private void UpdateInfo()
        {
            if (_player == null)
            {
                _lblInfo.Text = "No track loaded";
            }
        }

        private void BtnPlay_Click(object sender, EventArgs e)
        {
            if (_currentTrackIndex < 0 || _currentTrackIndex >= _playlist.Count) return;

            string path = Path.Combine(Directory.GetCurrentDirectory(), _playlist[_currentTrackIndex]);
            if (!File.Exists(path)) return;

            _player?.Dispose();
            _player = new SimpleAudioPlayer(path);
            _player.PlaybackFinished += (s, ev) => NextTrack();
            _player.Play();

            UpdateInfo();
        }

        private void BtnPause_Click(object sender, EventArgs e)
        {
            if (_player != null)
            {
                if (_player.IsPlaying) _player.Pause();
                else if (_player.IsPaused) _player.Play();
            }
            UpdateInfo();
        }

        private void BtnStop_Click(object sender, EventArgs e)
        {
            _player?.Stop();
            UpdateInfo();
        }

        private void NextTrack()
        {
            if (_playlist.Count == 0) return;
            _player?.Dispose();
            _player = null;
            _currentTrackIndex = (_currentTrackIndex + 1) % _playlist.Count;
            BtnPlay_Click(this, EventArgs.Empty);
        }

        private void Timer_Tick(object sender, EventArgs e)
        {
            if (_player == null) return;

            double pos = _player.CurrentPosition;
            double dur = _player.TotalDuration;
            if (dur > 0)
            {
                _pbProgress.Value = Math.Clamp((int)((pos / dur) * 100), 0, 100);
            }
            string status = _player.IsPlaying ? "Playing" : "Paused";
            _lblInfo.Text = $"{status} | {TimeSpan.FromSeconds(pos):hh\\:mm\\:ss} / {TimeSpan.FromSeconds(dur):hh\\:mm\\:ss}";

            _visualizer.Update(new float[_skin.BarCount]);
            _pbVisualizer.Invalidate();
        }

        private void _pbVisualizer_Paint(object sender, PaintEventArgs e)
        {
            var rect = _pbVisualizer.ClientRectangle;
            if (_wallpaper != null)
            {
                if (_skin.WallpaperStretch == "stretch")
                {
                    e.Graphics.DrawImage(_wallpaper, rect);
                }
                else
                {
                    int w = _wallpaper.Width, h = _wallpaper.Height;
                    for (int x = 0; x < rect.Width; x += w)
                        for (int y = 0; y < rect.Height; y += h)
                            e.Graphics.DrawImage(_wallpaper, x, y, w, h);
                }
            }

            float barWidth = rect.Width / (float)_skin.BarCount;
            for (int i = 0; i < _skin.BarCount; i++)
            {
                float h = _visualizer.Bars[i] * _skin.BarHeightScale;
                int x = i * (int)barWidth;
                int barHeight = (int)Math.Max(h, 2);
                int y = rect.Bottom - barHeight;
                using var brush = new SolidBrush(Color.Cyan);
                e.Graphics.FillRectangle(brush, x, y, Math.Max((int)barWidth - 1, 1), barHeight);
            }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _player?.Dispose();
            base.OnFormClosed(e);
        }
    }

    static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new Mp3PlayerForm());
        }
    }
}
