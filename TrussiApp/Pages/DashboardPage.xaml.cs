using TrussiApp.Pages;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

namespace TrussiApp.Pages
{
    public sealed partial class DashboardPage : Page
    {
        private MainWindow? _main;

        public DashboardPage()
        {
            InitializeComponent();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            if (e.Parameter is MainWindow main)
            {
                _main = main;
                _main.Serial.ConnectionChanged += OnConnectionChanged;
                _main.TrussiRing.ConnectionChanged += OnTrussiRingConnectionChanged;
                UpdateTrussiRingDot(_main.TrussiRing.IsConnected);

                UpdateAxiDeckDot(_main.Serial.IsConnected);
            }
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            if (_main != null) {
                _main.Serial.ConnectionChanged -= OnConnectionChanged;
                _main.TrussiRing.ConnectionChanged -= OnTrussiRingConnectionChanged;
            }
        }

        private void OnConnectionChanged(bool connected)
        {
            DispatcherQueue.TryEnqueue(() => UpdateAxiDeckDot(connected));
        }

        private void UpdateAxiDeckDot(bool connected)
        {
            AxiDeckDot.Fill = connected
                ? new SolidColorBrush(Colors.LimeGreen)
                : new SolidColorBrush(ColorHelper.FromArgb(255, 200, 50, 50));
        }

        private void OnTrussiRingConnectionChanged(bool connected)
        {
            DispatcherQueue.TryEnqueue(() => UpdateTrussiRingDot(connected));
        }

        private void UpdateTrussiRingDot(bool connected)
        {
            TrussiRingDot.Fill = connected
                ? new SolidColorBrush(Colors.LimeGreen)
                : new SolidColorBrush(ColorHelper.FromArgb(255, 200, 50, 50));
        }

        private void AxiDeckCard_Click(object sender, RoutedEventArgs e)
        {
            _main?.OpenDetailPanel(typeof(AxiDeckPage));
        }

        private void TrussiRingCard_Click(object sender, RoutedEventArgs e)
        {
            _main?.OpenDetailPanel(typeof(TrussiRingPage));
        }
    }
}