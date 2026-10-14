using System;
using System.IO.Ports;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TrussiApp
{
    public class SerialManager
    {
        // ─────────────────────────────────────────────
        //  CONFIG
        // ─────────────────────────────────────────────
        private readonly int _baudRate;
        private readonly string _expectedAck;
        private const string HandshakeMsg = "CONNECT";
        private const string TimePrefix = "TIME:";
        private const int TimeInterval = 30;
        private const int ReconnectDelay = 5000;
        private const int PingInterval = 5000;
        private const int PongTimeout = 12000;

        public SerialManager(int baudRate = 9600, string expectedAck = "ACK")
        {
            _baudRate = baudRate;
            _expectedAck = expectedAck;
        }

        // ─────────────────────────────────────────────
        //  EVENTS
        // ─────────────────────────────────────────────
        public event Action<bool>? ConnectionChanged;
        public event Action<string>? StatusChanged;
        public event Action<int, bool>? ButtonEvent;
        public event Action<int, int>? KnobEvent;
        public event Action<string>? RawMessageReceived;

        // ─────────────────────────────────────────────
        //  STATE
        // ─────────────────────────────────────────────
        private SerialPort? _port;
        private CancellationTokenSource _cts = new();
        private readonly object _lock = new();
        private DateTime _lastPong = DateTime.MinValue;
        private DateTime _lastPing = DateTime.MinValue;
        private readonly StringBuilder _readBuffer = new();
        private string? _lastKnownPort = null;
        private DateTime _lastTimeSend = DateTime.MinValue;
        private string _portCacheKeyName = "lastport";

        public bool IsConnected { get; private set; }
        public string? LastKnownPort => _lastKnownPort;
        public DateTime? LastConnectedAt { get; private set; }

        // Add with other fields
        private string PortCachePath =>
            Path.Combine(
                Windows.Storage.ApplicationData.Current.LocalFolder.Path,
                "lastport.txt");

        private void SaveLastPort(string portName)
        {
            try { File.WriteAllText(PortCachePath, portName); }
            catch { }
        }

        private string? LoadLastPort()
        {
            try
            {
                if (File.Exists(PortCachePath))
                {
                    string port = File.ReadAllText(PortCachePath).Trim();
                    Console.WriteLine($"[Serial] Last known port from disk: {port}");
                    return string.IsNullOrEmpty(port) ? null : port;
                }
            }
            catch { }
            return null;
        }

        // ─────────────────────────────────────────────
        //  PUBLIC API
        // ─────────────────────────────────────────────
        public void Start(string portCacheKey = "lastport")
        {
            _portCacheKeyName = portCacheKey;
            _cts = new CancellationTokenSource();
            _lastKnownPort = LoadLastPort();
            Task.Run(() => ConnectionLoop(_cts.Token));
            Console.WriteLine("[Serial] Manager started.");
        }

        public void Stop()
        {
            _cts.Cancel();
            ClosePort();
            Console.WriteLine("[Serial] Manager stopped.");
        }

        public void Reset()
        {
            Console.WriteLine("[Serial] Reset requested.");
            ClosePort();
        }

        public void SendTime()
        {
            string msg = $"{TimePrefix}{DateTime.Now:HH:mm}";
            Console.WriteLine($"[Serial] -> {msg}");
            Send(msg);
        }

        public void SendLabel(int index, string label)
        {
            if (index < 0 || index > 5) return;
            string msg = $"LABEL:{index + 1}:{label}";
            Console.WriteLine($"[Serial] -> {msg}");
            Send(msg);
        }

        public void SendAllLabels(string[] labels)
        {
            if (labels.Length != 6) return;
            string msg = $"LABELS:{string.Join("|", labels)}";
            Console.WriteLine($"[Serial] -> {msg}");
            Send(msg);
        }

        public void SendTrack(string title, string artist, string duration, int progress)
        {
            string msg = $"TRACK:{title}|{artist}|{duration}|{progress}";
            if (LogSettings.VerboseTrackLogging)
                Console.WriteLine($"[Serial] -> {msg}");
            Send(msg);
        }

        public void SendNoTrack()
        {
            Console.WriteLine("[Serial] -> NOTRACK");
            Send("NOTRACK");
        }

        public void SendRaw(string msg)
        {
            Console.WriteLine($"[Serial] -> {msg}");
            Send(msg);
        }

        // ─────────────────────────────────────────────
        //  CONNECTION LOOP
        // ─────────────────────────────────────────────
        private async Task ConnectionLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                RaiseStatus("Searching for device...");

                // FindArduinoPort returns an already-open, already-handshaked port
                SerialPort? serial = await FindArduinoPort(ct);

                if (serial == null)
                {
                    RaiseStatus("No device found — retrying...");
                    await Delay(ReconnectDelay, ct);
                    continue;
                }

                string portName = serial.PortName;
                Console.WriteLine($"[Serial] Using already-open port {portName}.");
                RaiseStatus($"Connected on {portName}");

                try
                {
                    lock (_lock)
                    {
                        _port = serial;
                        IsConnected = true;
                        _lastPong = DateTime.Now;
                        _lastPing = DateTime.Now;
                        _readBuffer.Clear();
                    }

                    ConnectionChanged?.Invoke(true);
                    SendTime();

                    // ── Main loop ──
                    while (!ct.IsCancellationRequested)
                    {
                        // Time sync — every 30 seconds
                        if ((DateTime.Now - _lastTimeSend).TotalSeconds >= TimeInterval)
                        {
                            SendTime();
                            _lastTimeSend = DateTime.Now;
                        }

                        // Ping
                        if ((DateTime.Now - _lastPing).TotalMilliseconds >= PingInterval)
                        {
                            Send("PING");
                            _lastPing = DateTime.Now;
                            Console.WriteLine("[Serial] -> PING");
                        }

                        // Pong timeout
                        if ((DateTime.Now - _lastPong).TotalMilliseconds >= PongTimeout)
                        {
                            Console.WriteLine("[Serial] Pong timeout — disconnecting.");
                            RaiseStatus("Device not responding...");
                            break;
                        }

                        // Check port alive
                        bool alive;
                        lock (_lock) { alive = _port is { IsOpen: true }; }
                        if (!alive) break;

                        // Read incoming
                        string? incoming = null;
                        lock (_lock)
                        {
                            try { incoming = TryReadLine(_port!); }
                            catch (TimeoutException) { }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"[Serial] Read error: {ex.Message}");
                                break;
                            }
                        }

                        if (incoming != null)
                        {
                            Console.WriteLine($"[Serial] <- {incoming}");
                            HandleIncoming(incoming);
                        }

                        await Delay(50, ct);
                    }
                }
                catch (Exception ex) when (
                    ex is UnauthorizedAccessException or
                    InvalidOperationException or
                    System.IO.IOException)
                {
                    Console.WriteLine($"[Serial] Error: {ex.Message}");
                    RaiseStatus($"Serial error: {ex.Message}");
                }
                finally
                {
                    ClosePort();
                    ConnectionChanged?.Invoke(false);
                    RaiseStatus("Disconnected — retrying...");
                }

                await Delay(ReconnectDelay, ct);
            }
        }

        // ─────────────────────────────────────────────
        //  PORT FINDER
        //  Opens each port, handshakes, returns the
        //  live open port on success — no double open
        // ─────────────────────────────────────────────
        private async Task<SerialPort?> FindArduinoPort(CancellationToken ct)
        {
            string[] ports = SerialPort.GetPortNames();

            if (ports.Length == 0)
            {
                Console.WriteLine("[Serial] No COM ports found.");
                return null;
            }

            Console.WriteLine($"[Serial] Available ports: {string.Join(", ", ports)}");

            // Try last known port first — skip probe delay if it responds
            if (_lastKnownPort != null && Array.Exists(ports, p => p == _lastKnownPort))
            {
                Console.WriteLine($"[Serial] Trying last known port {_lastKnownPort} first...");
                var result = await ProbePort(_lastKnownPort, ct);
                if (result != null) return result;
                Console.WriteLine($"[Serial] Last known port failed — scanning all.");
            }

            // Fall back to scanning all ports
            foreach (string portName in ports)
            {
                if (ct.IsCancellationRequested) return null;
                if (portName == _lastKnownPort) continue; // already tried

                var result = await ProbePort(portName, ct);
                if (result != null) return result;
            }

            return null;
        }

        // Extract probe logic into its own method
        private async Task<SerialPort?> ProbePort(string portName, CancellationToken ct)
        {
            Console.WriteLine($"[Serial] Probing {portName}...");
            SerialPort? probe = null;
            try
            {
                probe = new SerialPort(portName, _baudRate)
                {
                    ReadTimeout = 500,
                    WriteTimeout = 1000,
                    NewLine = "\n"
                };
                probe.Open();
                await Delay(2500, ct);
                probe.DiscardInBuffer();
                _readBuffer.Clear();

                probe.WriteLine(HandshakeMsg);
                Console.WriteLine($"[Serial] {portName} -> CONNECT");

                var deadline = DateTime.Now.AddSeconds(4);
                while (DateTime.Now < deadline && !ct.IsCancellationRequested)
                {
                    string? line = TryReadLine(probe);
                    if (line != null)
                    {
                        Console.WriteLine($"[Serial] {portName} <- {line}");
                        if (line == _expectedAck)
                        {
                            _lastKnownPort = portName;
                            LastConnectedAt = DateTime.Now;
                            SaveLastPort(portName);
                            Console.WriteLine($"[Serial] Arduino found on {portName}!");
                            return probe;
                        }
                    }
                    await Delay(50, ct);
                }

                Console.WriteLine($"[Serial] {portName} — no ACK.");
                probe.Close();
                probe.Dispose();
                return null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Serial] {portName} — failed ({ex.Message})");
                try { probe?.Close(); probe?.Dispose(); } catch { }
                return null;
            }
            finally
            {
                _readBuffer.Clear();
            }
        }

        // ─────────────────────────────────────────────
        //  LINE READER — byte-by-byte accumulator
        // ─────────────────────────────────────────────
        private string? TryReadLine(SerialPort port)
        {
            while (port.BytesToRead > 0)
            {
                int b = port.ReadByte();
                if (b == -1) break;

                char c = (char)b;
                if (c == '\n')
                {
                    string line = _readBuffer.ToString().TrimEnd('\r').Trim();
                    _readBuffer.Clear();
                    if (line.Length > 0) return line;
                }
                else
                {
                    _readBuffer.Append(c);
                }
            }
            return null;
        }

        // ─────────────────────────────────────────────
        //  INCOMING PARSER
        // ─────────────────────────────────────────────
        private void HandleIncoming(string line)
        {
            if (line == "PONG" || line == _expectedAck)
            {
                lock (_lock) { _lastPong = DateTime.Now; }
                return;
            }

            if (line == "PING")
            {
                Send("PONG");
                lock (_lock) { _lastPong = DateTime.Now; }
                return;
            }

            if (line.StartsWith("BTN:"))
            {
                var parts = line.Split(':');
                if (parts.Length == 3 &&
                    int.TryParse(parts[1], out int n) && n >= 1 && n <= 6)
                {
                    bool pressed = parts[2] == "DOWN";
                    Console.WriteLine($"[Serial] Button {n} {(pressed ? "DOWN" : "UP")}");
                    ButtonEvent?.Invoke(n - 1, pressed);
                }
                return;
            }

            if (line.StartsWith("KNOB"))
            {
                if (line.Length >= 6 &&
                    int.TryParse(line[4].ToString(), out int knob))
                {
                    int dir = line[5] == '+' ? 1 : -1;
                    Console.WriteLine($"[Serial] Knob {knob} {(dir > 0 ? "CW" : "CCW")}");
                    KnobEvent?.Invoke(knob - 1, dir);
                }
                return;
            }

            RawMessageReceived?.Invoke(line);
            Console.WriteLine($"[Serial] Unhandled message: {line}");
        }

        // ─────────────────────────────────────────────
        //  HELPERS
        // ─────────────────────────────────────────────
        private void Send(string msg)
        {
            lock (_lock)
            {
                try
                {
                    if (_port is { IsOpen: true })
                        _port.WriteLine(msg);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Serial] Send failed: {ex.Message}");
                }
            }
        }

        private void ClosePort()
        {
            lock (_lock)
            {
                try { _port?.Close(); _port?.Dispose(); }
                catch { }
                _port = null;
                IsConnected = false;
                _readBuffer.Clear();
            }
            Console.WriteLine("[Serial] Port closed.");
        }

        private void RaiseStatus(string text) => StatusChanged?.Invoke(text);

        private static async Task Delay(int ms, CancellationToken ct)
        {
            await Task.Delay(ms, ct).ContinueWith(_ => { });
        }
    }
}