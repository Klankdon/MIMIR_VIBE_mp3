# Create a new .NET 6 console app
dotnet new console -n SimpleMp3Player -force

# Change into the folder
cd SimpleMp3Player

# Add the necessary NuGet packages
dotnet add package NAudio
dotnet add package System.Drawing.Common   # needed for GDI+ drawing / bitmaps


// --- NEXT BLOCK ---

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace SimpleMp3Player
{
    // -------------------------------------------------
    // 1. Config / Skin model
    // -------------------------------------------------
    public class SkinConfig
    {
        public int WindowWidth { get; set; } = 600;
        public int WindowHeight { get; set; } = 400;
        public Color BackColor { get; set; } = Color.FromArgb(30, 30, 30);
        public Color ForeColor { get; set; } = Color.White;
        public int BarCount { get; set; } = 64;
        public float BarHeightScale { get; set; } = 1.0f;
        public string WallpaperPath { get; set; } = string.Empty; // relative to "wallpaper" subfolder
        public string WallpaperStretch { get; set; } = "tile"; // "tile" or "stretch"
        public float WallpaperOpacity { get; set; } = 1.0f;
    }

    // -------------------------------------------------
    // 2. Simple audio player wrapper (NAudio)
    // -------------------------------------------------
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

        // Feed the audio into the provider (required for streaming / effects later)
        public void Feed()
        {
            const int blockSize = 1024;
            byte[] buffer = new byte[blockSize];
            int read;
            while ((read = _reader.Read(buffer, 0, buffer.Length)) > 0)
                _provider.AddSamples(buffer, 0, read);
            // Signal end‑of‑stream to NAudio (flush)
            _provider.Flush();
        }

        public void Dispose()
        {
            _reader?.Dispose();
            _output?.Dispose();
        }
    }

    // -------------------------------------------------
    // 3. Audio visualizer (FFT based bar display)
    // -------------------------------------------------
    public class AudioVisualizer
    {
        public float[] Bars { get; } = new float[64];
        private readonly float[] _fftBuffer;
        private int _bufferPos;

        public AudioVisualizer(int barCount = 64)
        {
            Bars = new float[barCount];
            _fftBuffer = new float[barCount * 2]; // simplistic real‑FFT placeholder
        }

        // Very tiny “FFT” – just copy magnitude of the waveform for demo
        public void Update(float[] samples)
        {
            // Simple RMS per 64 bins
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
                Bars[i] = (float)Math.Sqrt(sum / binSize) * 2f; // scale to fit window
            }
        }
    }

    // -------------------------------------------------
    // 4. Main UI – WinForms window with skin support
    // -------------------------------------------------
    public class Mp3PlayerForm : Form
    {
        private readonly SkinConfig _skin;
        private readonly List<string> _playlist = new();
        private int _currentTrackIndex = -1;
        private SimpleAudioPlayer _player;
        private readonly AudioVisualizer _visualizer = new();
        private readonly Timer _timer = new() { Interval = 250 }; // UI refresh
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
            // Load skin from "skin.json" (default if missing)
            _skin = LoadSkin();

            // Apply wallpaper
            ApplyWallpaper();

            // UI setup
            Text = "Simple MP3 Player";
            Size = new Size(_skin.WindowWidth, _skin.WindowHeight);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = _skin.BackColor;
            ForeColor = _skin.ForeColor;
            Font = new Font("Segoe UI", 9F, FontStyle.Regular);

            // ---- Controls ------------------------------------------------
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

            // Visualizer picture box (bottom)
            _pbVisualizer = new PictureBox
            {
                Dock = DockStyle.Fill,
                SizeMode = PictureBoxSizeMode.StretchImage,
                BackColor = Color.Black
            };

            // Playlist list (right side – optional split)
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

            // Layout: visualizer + playlist (split container)
            var split = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Horizontal,
                Panel1.Controls = { _pbVisualizer },
                Panel2.Controls = { _txtPlaylist },
                SplitterDistance = (int)(Height * 0.70f)
            };

            // Assemble form
            Controls.Add(split);
            Controls.Add(pnlTop, 0, 0);
            Controls.Add(_pbProgress, 0, 1);
            Controls.Add(_lblInfo, 0, 2);

            // Timer for UI updates
            _timer.Tick += Timer_Tick;
            _timer.Start();

            // Load a sample playlist if any mp3 files exist in the working dir
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
                catch
                {
                    // fallback to defaults on parse error
                }
            }
            // Write a minimal default skin (helps first‑run users)
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
                    // Apply opacity via a tinted overlay (quick & dirty)
                    using (var gfx = CreateGraphics())
                    {
                        // We will draw the wallpaper in the Paint event later.
                    }
                }
                catch { /* ignore bad images */ }
            }
        }

        private void LoadDefaultPlaylist()
        {
            var files = Directory.GetFiles(Directory.GetCurrentDirectory(), "*.mp3");
            foreach (var f in files)
                _playlist.Add(Path.GetFileName(f));
            RefreshPlaylistDisplay();
        }

        private void RefreshPlaylistDisplay()
        {
            _txtPlaylist.Clear();
            for (int i = 0; i < _playlist.Count; i++)
                _txtPlaylist.AppendText($"{i + 1}. {_playlist[i]}{Environment.NewLine}");
        }

        // -------------------------------------------------
        // Event handlers
        // -------------------------------------------------
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
            _player?.Pause();
            UpdateInfo();
        }

        private void BtnStop_Click(object sender, EventArgs e)
        {
            _player?.Stop();
            UpdateInfo();
        }

        private void NextTrack()
        {
            _player?.Dispose();
            _player = null;
            _currentTrackIndex = (_currentTrackIndex + 1) % _playlist.Count;
            BtnPlay_Click(this, EventArgs.Empty);
        }

        private void Timer_Tick(object sender, EventArgs e)
        {
            if (_player == null) return;

            // Update progress bar
            double pos = _player.CurrentPosition;
            double dur = _player.TotalDuration;
            _pbProgress.Value = (int)((pos / dur) * 100);
            _lblInfo.Text = $"_player.IsPlaying ? \"Playing\" : \"Paused\" | {TimeSpan.FromSeconds(pos):hh\\:mm\\:ss} / {TimeSpan.FromSeconds(dur):hh\\:mm\\:ss}";

            // Generate a dummy sample array for visualizer (just use raw audio data for now)
            // For simplicity we just show static bars; you can swap this with a real FFT.
            _visualizer.Update(new float[_skin.BarCount]);
            _pbVisualizer.Invalidate(); // force repaint
        }

        private void Mp3PlayerForm_Paint(object sender, PaintEventArgs e)
        {
            // Draw wallpaper as background (simple tile or stretch)
            if (_wallpaper != null)
            {
                using var attr = new Drawing.Graphics((Graphics)e.Graphics);
                if (_skin.WallpaperStretch == "stretch")
                {
                    e.Graphics.DrawImage(_wallpaper, ClientRectangle);
                }
                else
                {
                    // tile
                    int w = _wallpaper.Width, h = _wallpaper.Height;
                    for (int x = 0; x < ClientRectangle.Width; x += w)
                        for (int y = 0; y < ClientRectangle.Height; y += h)
                            e.Graphics.DrawImage(_wallpaper, x, y, w, h);
                }
            }
        }

        private void Mp3PlayerForm_Resize(object sender, EventArgs e)
        {
            // Re‑draw visualizer when window resizes
            _pbVisualizer.Invalidate();
        }

        // Simple visualizer draw
        private void _pbVisualizer_Paint(object sender, PaintEventArgs e)
        {
            var rect = _pbVisualizer.ClientRectangle;
            float barWidth = rect.Width / (float)_skin.BarCount;
            for (int i = 0; i < _skin.BarCount; i++)
            {
                float h = _visualizer.Bars[i] * _skin.BarHeightScale;
                int x = i * (int)barWidth;
                int barHeight = (int)Math.Max(h, 2);
                int y = rect.Bottom - barHeight;
                using var brush = new SolidBrush(Color.Cyan);
                e.Graphics.FillRectangle(brush, x, y, (int)barWidth - 1, barHeight);
            }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _player?.Dispose();
            base.OnFormClosed(e);
        }
    }

    // -------------------------------------------------
    // 5. Entry point
    // -------------------------------------------------
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


// --- NEXT BLOCK ---

# Inside the repo folder (where Program.cs lives)
dotnet build
dotnet run --configuration Release   # starts the UI

# To produce a self‑contained exe (Windows, x64)
dotnet publish -c Release -r win-x64 --self-contained true -o ./publish


// --- NEXT BLOCK ---

{
  "WindowWidth": 720,
  "WindowHeight": 520,
  "BackColor": "#1E1E1E",
  "ForeColor": "#F0F0F0",
  "BarCount": 128,
  "BarHeightScale": 1.5,
  "WallpaperPath": "mybg.png",
  "WallpaperStretch": "stretch",
  "WallpaperOpacity": 0.85
}
