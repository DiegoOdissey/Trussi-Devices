using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using System;
using TrussiApp.Pages;

namespace TrussiApp
{
    public sealed partial class MainWindow : Window
    {
        public const bool DebugSimulateConnected = false;

        // ─────────────────────────────────────────────
        //  SERVICES
        // ─────────────────────────────────────────────
        public readonly SerialManager Serial = new();
        public readonly TrackDetector Tracker = new();
        public readonly BindingManager Bindings = new();
        public readonly TrayManager Tray = new();
        public readonly TrussiRingBindingManager TrussiRingBindings = new();
        public readonly TrussiRingManager TrussiRing = new();

        public bool NotificationsEnabled { get; set; } = true;

        private bool _detailOpen = false;

        // ─────────────────────────────────────────────
        //  SETUP
        // ─────────────────────────────────────────────
        public MainWindow()
        {
            InitializeComponent();
            SetWindowSize(1400, 800);
            RootGrid.RequestedTheme = ElementTheme.Dark;

            Console.WriteLine("[App] TrussiApp starting...");

            // Load saved bindings
            Bindings.Load();
            TrussiRingBindings.Load();

            // Init tray
            Tray.Initialize(this);

            // Navigate to dashboard
            MainFrame.Navigate(typeof(DashboardPage), this,
                new Microsoft.UI.Xaml.Media.Animation
                    .SuppressNavigationTransitionInfo());

            // Wire events
            Serial.ConnectionChanged += OnConnectionChanged;
            Serial.StatusChanged += OnStatusChanged;
            Serial.ButtonEvent += OnButtonEvent;
            Serial.KnobEvent += OnKnobEvent;

            Tracker.TrackPlaying += OnTrackPlaying;
            Tracker.TrackStopped += OnTrackStopped;

            if (!DebugSimulateConnected) {
                Serial.Start();
                TrussiRing.Start();
                TrussiRing.ConnectionChanged += OnTrussiRingConnectionChanged;
                TrussiRing.ButtonEvent += OnTrussiRingButtonEvent;
            }

            Tracker.Start();
            Console.WriteLine("[App] Init complete.");
        }

        // ─────────────────────────────────────────────
        //  SERIAL EVENTS
        // ─────────────────────────────────────────────
        private void OnConnectionChanged(bool connected)
        {
            Console.WriteLine(
                $"[App] Connection: {(connected ? "CONNECTED" : "DISCONNECTED")}");

            if (NotificationsEnabled)
                Tray.Notify("TrussiApp",
                    connected ? "AxiDeck connesso." : "AxiDeck disconnesso.");

            Tray.UpdateTooltip(connected ? "TrussiApp — AxiDeck connesso" : "TrussiApp");

            // Send labels to deck on connect
            if (connected)
            {
                Serial.SendAllLabels(Bindings.GetButtonLabels());
                Serial.SendTime();
            }
        }

        private void OnStatusChanged(string text)
        {
            Console.WriteLine($"[App] Status: {text}");
        }

        private void OnButtonEvent(int index, bool pressed)
        {
            Console.WriteLine($"[App] Button {index + 1} {(pressed ? "DOWN" : "UP")}");
            if (pressed)
                Bindings.ExecuteButton(index);
        }

        private void OnKnobEvent(int knob, int direction)
        {
            Console.WriteLine($"[App] Knob {knob + 1} {(direction > 0 ? "CW" : "CCW")}");
            Bindings.ExecuteKnob(knob, direction);
        }

        // ─────────────────────────────────────────────
        //  TRACK EVENTS
        // ─────────────────────────────────────────────
        private void OnTrackPlaying(string title, string artist, string duration, int progress)
        {
            if (LogSettings.VerboseTrackLogging)
                Console.WriteLine($"[App] Track: {title} — {artist} [{duration}] {progress}%");

            if (Serial.IsConnected) {
                Serial.SendTrack(title, artist, duration, progress);
            }

            if(TrussiRing.IsConnected)
            {
                TrussiRing.SendTrack(title, artist, duration, progress);
            }
        }

        private void OnTrackStopped()
        {
            Console.WriteLine("[App] Track stopped.");

            if (Serial.IsConnected)
                Serial.SendNoTrack();

            if (TrussiRing.IsConnected)
                TrussiRing.SendNoTrack();
        }

        private void OnTrussiRingConnectionChanged(bool connected)
        {
            Console.WriteLine($"[App] TrussiRing: {(connected ? "CONNECTED" : "DISCONNECTED")}");

            if (NotificationsEnabled)
                Tray.Notify("TrussiApp",
                    connected ? "TrussiRing connesso." : "TrussiRing disconnesso.");

            if(connected) Tracker.ForceResend();
        }

        private void OnTrussiRingButtonEvent(int index, bool pressed)
        {
            Console.WriteLine($"[App] TrussiRing Button {index + 1} {(pressed ? "DOWN" : "UP")}");
            if (pressed)
                TrussiRingBindings.ExecuteButton(index);
        }

        // ─────────────────────────────────────────────
        //  PANEL OPEN / CLOSE
        // ─────────────────────────────────────────────
        public void OpenDetailPanel(Type pageType)
        {
            if (!_detailOpen)
            {
                _detailOpen = true;
                DetailCol.Width = new GridLength(480);
                DetailDivider.Visibility = Visibility.Visible;
                SetWindowSize(1700, 800);
            }

            DetailFrame.Navigate(pageType, this,
                new Microsoft.UI.Xaml.Media.Animation.SlideNavigationTransitionInfo
                {
                    Effect = Microsoft.UI.Xaml.Media.Animation
                             .SlideNavigationTransitionEffect.FromRight
                });
        }

        public void CloseDetailPanel()
        {
            _detailOpen = false;
            DetailCol.Width = new GridLength(0);
            DetailDivider.Visibility = Visibility.Collapsed;
            DetailFrame.Content = null;
            SetWindowSize(1400, 800);
        }

        // ─────────────────────────────────────────────
        //  SIDEBAR NAV
        // ─────────────────────────────────────────────
        private void NavDevices_Click(object sender, RoutedEventArgs e)
        {
            SetNavActive(NavDevices, NavSettings);
            CloseDetailPanel();
            MainFrame.Navigate(typeof(DashboardPage), this,
                new Microsoft.UI.Xaml.Media.Animation
                    .SuppressNavigationTransitionInfo());
        }

        private void NavSettings_Click(object sender, RoutedEventArgs e)
        {
            SetNavActive(NavSettings, NavDevices);
            CloseDetailPanel();
            MainFrame.Navigate(typeof(SettingsPage), this,
                new Microsoft.UI.Xaml.Media.Animation
                    .SuppressNavigationTransitionInfo());
        }

        private void CheckUpdates_Click(object sender, RoutedEventArgs e)
        {
            Console.WriteLine("[Nav] Check for updates.");
        }

        private static void SetNavActive(
            Microsoft.UI.Xaml.Controls.Button active,
            Microsoft.UI.Xaml.Controls.Button inactive)
        {
            active.Background = new SolidColorBrush(
                                      ColorHelper.FromArgb(255, 34, 34, 34));
            active.Foreground = new SolidColorBrush(Colors.White);
            inactive.Background = new SolidColorBrush(Colors.Transparent);
            inactive.Foreground = new SolidColorBrush(
                                      ColorHelper.FromArgb(255, 136, 136, 136));
        }

        // ─────────────────────────────────────────────
        //  HELPERS
        // ─────────────────────────────────────────────
        private void SetWindowSize(int width, int height)
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);
            appWindow.Resize(new Windows.Graphics.SizeInt32(width, height));
        }
    }
}