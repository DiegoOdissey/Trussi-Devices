using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace TrussiApp
{
    public class TrussiRingManager
    {
        public readonly SerialManager Serial = new(baudRate: 115200, expectedAck: "ACK:TRUSSIRING");
        private readonly AudioSessionManager _audio = new();

        private string _portCacheKey = "lastport_trussiring";

        public event Action<bool>? ConnectionChanged;
        public event Action<int, bool>? ButtonEvent;

        private bool _appListSent = false;

        public TrussiRingManager()
        {
            Serial.ConnectionChanged += OnConnectionChanged;
            Serial.ButtonEvent += OnButtonEvent;
            Serial.RawMessageReceived += OnRawMessage;
        }

        public void Start() => Serial.Start(portCacheKey: _portCacheKey);
        public void Stop() => Serial.Stop();

        public bool IsConnected => Serial.IsConnected;

        private void OnConnectionChanged(bool connected)
        {
            ConnectionChanged?.Invoke(connected);
            if (connected)
            {
                SendDate();
                SendAppList();
            }
            else
            {
                _appListSent = false;
            }
        }

        private void OnButtonEvent(int index, bool pressed) => ButtonEvent?.Invoke(index, pressed);

        private void OnRawMessage(string line)
        {
            if (line.StartsWith("VOL:"))
            {
                var parts = line.Split(':');
                if (parts.Length == 3)
                {
                    string name = parts[1];
                    int dir = parts[2] == "+" ? 1 : -1;
                    HandleVolumeCommand(name, dir);
                }
                return;
            }

            if (line.StartsWith("APPSELECT:"))
            {
                string app = line.Substring(10).Trim();
                Console.WriteLine($"[TrussiRing] Active app on device: {app}");
                return;
            }
        }

        private void HandleVolumeCommand(string appName, int direction)
        {
            Task.Run(() =>
            {
                if (string.Equals(appName, "Master", StringComparison.OrdinalIgnoreCase))
                {
                    AdjustMasterVolume(direction);
                }
                else
                {
                    _audio.AdjustVolume(appName, direction);
                }

                int pct = GetVolumePct(appName);
                Serial.SendRaw($"VOLLVL:{appName}:{pct}");
            });
        }

        private void AdjustMasterVolume(int direction)
        {
            try
            {
                using var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
                using var device = enumerator.GetDefaultAudioEndpoint(
                    NAudio.CoreAudioApi.DataFlow.Render,
                    NAudio.CoreAudioApi.Role.Multimedia);
                float current = device.AudioEndpointVolume.MasterVolumeLevelScalar;
                device.AudioEndpointVolume.MasterVolumeLevelScalar =
                    Math.Clamp(current + (direction > 0 ? 0.05f : -0.05f), 0f, 1f);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TrussiRing] Master volume error: {ex.Message}");
            }
        }

        private int GetVolumePct(string appName)
        {
            try
            {
                if (string.Equals(appName, "Master", StringComparison.OrdinalIgnoreCase))
                {
                    using var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
                    using var device = enumerator.GetDefaultAudioEndpoint(
                        NAudio.CoreAudioApi.DataFlow.Render,
                        NAudio.CoreAudioApi.Role.Multimedia);
                    return (int)(device.AudioEndpointVolume.MasterVolumeLevelScalar * 100f);
                }
                return _audio.GetVolumePct(appName);
            }
            catch { return 50; }
        }

        public void SendAppList()
        {
            Task.Run(() =>
            {
                var apps = _audio.GetActiveProcessNames();
                var list = new List<string> { "Master" };
                list.AddRange(apps);
                string msg = "APPLIST:" + string.Join("|", list);
                Serial.SendRaw(msg);
                Console.WriteLine($"[TrussiRing] -> {msg}");
                _appListSent = true;
            });
        }

        public void SendDate()
        {
            string[] months = {
                "Gennaio","Febbraio","Marzo","Aprile","Maggio","Giugno",
                "Luglio","Agosto","Settembre","Ottobre","Novembre","Dicembre"
            };
            var now = DateTime.Now;
            string msg = $"DATE:{now.Day} {months[now.Month - 1]} {now.Year}";
            Serial.SendRaw(msg);
        }

        public void SendTrack(string title, string artist, string duration, int progress)
        {
            if (Serial.IsConnected)
                Serial.SendRaw($"TRACK:{title}|{artist}|{duration}|{progress}");
        }

        public void SendNoTrack()
        {
            if (Serial.IsConnected)
                Serial.SendRaw("NOTRACK");
        }

        public void RefreshAppList()
        {
            if (Serial.IsConnected)
                SendAppList();
        }
    }
}