using System;
using System.Threading;
using System.Threading.Tasks;
using Windows.Media.Control;

namespace TrussiApp
{
    public class TrackDetector
    {
        private const int PollInterval = 1000;

        public event Action<string, string, string, int>? TrackPlaying;
        public event Action? TrackStopped;

        private CancellationTokenSource _cts = new();
        private bool _wasPlaying = false;
        private string _lastTitle = "";

        public void Start()
        {
            _cts = new CancellationTokenSource();
            Task.Run(() => DetectionLoop(_cts.Token));
            Console.WriteLine("[Track] Detector started.");
        }

        public void Stop()
        {
            _cts.Cancel();
            Console.WriteLine("[Track] Detector stopped.");
        }

        private async Task DetectionLoop(CancellationToken ct)
        {
            GlobalSystemMediaTransportControlsSessionManager? manager = null;

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    manager = await GlobalSystemMediaTransportControlsSessionManager
                        .RequestAsync();
                    Console.WriteLine("[Track] Session manager acquired.");
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Track] Failed to get session manager: {ex.Message}");
                    await Delay(3000, ct);
                }
            }

            if (manager == null) return;

            while (!ct.IsCancellationRequested)
            {
                try { await PollSession(manager); }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Track] Poll error: {ex.Message}");
                }
                await Delay(PollInterval, ct);
            }
        }

        private async Task PollSession(
            GlobalSystemMediaTransportControlsSessionManager manager)
        {
            var session = manager.GetCurrentSession();

            if (session == null)
            {
                if (_wasPlaying)
                {
                    Console.WriteLine("[Track] No active session.");
                    _wasPlaying = false;
                    _lastTitle = "";
                    TrackStopped?.Invoke();
                }
                return;
            }

            var playback = session.GetPlaybackInfo();
            bool isPlaying = playback?.PlaybackStatus ==
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

            if (!isPlaying)
            {
                if (_wasPlaying)
                {
                    Console.WriteLine("[Track] Paused or stopped.");
                    _wasPlaying = false;
                    _lastTitle = "";
                    TrackStopped?.Invoke();
                }
                return;
            }

            GlobalSystemMediaTransportControlsSessionMediaProperties? props = null;
            try { props = await session.TryGetMediaPropertiesAsync(); }
            catch (Exception ex)
            {
                Console.WriteLine($"[Track] Failed to get properties: {ex.Message}");
                return;
            }

            if (props == null) return;

            string title = props.Title ?? "";
            string artist = props.Artist ?? "";

            await Task.Delay(500);
            var timeline = session.GetTimelineProperties();

            string duration = "0:00";
            int progress = 0;

            if (timeline != null)
            {
                double totalSecs = timeline.EndTime.TotalSeconds;
                double positionSecs = timeline.Position.TotalSeconds;

                TimeSpan elapsed = DateTimeOffset.UtcNow - timeline.LastUpdatedTime;
                if (elapsed.TotalSeconds > 0)
                    positionSecs += elapsed.TotalSeconds;

                if (totalSecs > 0)
                {
                    positionSecs = Math.Clamp(positionSecs, 0, totalSecs);

                    progress = (int)Math.Clamp(
                        (positionSecs / totalSecs) * 100,
                        0,
                        100);

                    duration = FormatDuration(timeline.EndTime);
                }

                if (LogSettings.VerboseTrackLogging)
                {
                    Console.WriteLine(
                        $"[Track] Position: {FormatDuration(timeline.Position)}" +
                        $" / {duration} ({progress}%)");
                }
            }

            if (title != _lastTitle)
            {
                Console.WriteLine($"[Track] Now playing: \"{title}\" — \"{artist}\" [{duration}]");
                _lastTitle = title;
            }

            _wasPlaying = true;
            TrackPlaying?.Invoke(title, artist, duration, progress);
        }

        private static string FormatDuration(TimeSpan t)
        {
            return t.Hours > 0
                ? $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}"
                : $"{t.Minutes}:{t.Seconds:D2}";
        }

        private static async Task Delay(int ms, CancellationToken ct)
        {
            await Task.Delay(ms, ct).ContinueWith(_ => { });
        }

        public void ForceResend() => _lastTitle = "";
    }
}