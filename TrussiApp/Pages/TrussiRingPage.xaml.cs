using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;
using Windows.Storage.Pickers;

namespace TrussiApp.Pages
{
    public sealed partial class TrussiRingPage : Page
    {
        private MainWindow? _main;
        private int? _selectedIndex;
        private bool _listeningForShortcut = false;
        private Dictionary<string, Button> _keyButtons = new();

        public TrussiRingPage()
        {
            InitializeComponent();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            if (e.Parameter is MainWindow main)
            {
                _main = main;
                UpdateBadge(false); // no serial yet
            }

            _keyButtons = new Dictionary<string, Button>
            {
                ["KEY:0"] = Key1,
                ["KEY:1"] = Key2,
                ["KEY:2"] = Key3,
                ["KEY:3"] = Key4
            };
        }

        private void UpdateBadge(bool connected)
        {
            if (connected)
            {
                StatusBadge.Background = new SolidColorBrush(ColorHelper.FromArgb(255, 20, 60, 20));
                StatusBadgeText.Text = "Connesso";
                StatusBadgeText.Foreground = new SolidColorBrush(Colors.LimeGreen);
            }
            else
            {
                StatusBadge.Background = new SolidColorBrush(ColorHelper.FromArgb(255, 40, 20, 20));
                StatusBadgeText.Text = "Disconnesso";
                StatusBadgeText.Foreground = new SolidColorBrush(ColorHelper.FromArgb(255, 200, 50, 50));
            }
        }

        // ─────────────────────────────────────────────
        //  INPUT SELECTION
        // ─────────────────────────────────────────────
        private void InputButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not string tag) return;

            ClearAllHighlights();

            var parts = tag.Split(':');
            if (parts.Length != 2 || !int.TryParse(parts[1], out int index)) return;

            _selectedIndex = index;

            btn.Background = new SolidColorBrush(ColorHelper.FromArgb(255, 40, 80, 140));
            btn.BorderBrush = new SolidColorBrush(ColorHelper.FromArgb(255, 80, 140, 220));

            ShowMappingPanel(index);
        }

        private void ClearAllHighlights()
        {
            foreach (var btn in _keyButtons.Values)
            {
                btn.Background = new SolidColorBrush(ColorHelper.FromArgb(255, 37, 37, 37));
                btn.BorderBrush = new SolidColorBrush(ColorHelper.FromArgb(255, 58, 58, 58));
            }
        }

        // ─────────────────────────────────────────────
        //  MAPPING PANEL
        // ─────────────────────────────────────────────
        private void ShowMappingPanel(int index)
        {
            MappingTitle.Text = $"Modificando Tasto {index + 1}";
            MappingTitle.Foreground = new SolidColorBrush(Colors.White);
            EmptyState.Visibility = Visibility.Collapsed;
            MappingPanel.Visibility = Visibility.Visible;

            var binding = _main?.TrussiRingBindings.Profile.Buttons[index];
            if (binding == null)
            {
                ActionTypeCombo.SelectedIndex = 0;
                HideAllSubPanels();
                return;
            }

            string actionTag = binding.Action switch
            {
                ActionType.Media => "media",
                ActionType.LaunchApp => "app",
                ActionType.Shortcut => "shortcut",
                ActionType.OpenWebsite => "website",
                _ => "none"
            };

            foreach (ComboBoxItem item in ActionTypeCombo.Items)
                if (item.Tag?.ToString() == actionTag)
                { ActionTypeCombo.SelectedItem = item; break; }

            HideAllSubPanels();
            switch (binding.Action)
            {
                case ActionType.Media:
                    MediaPanel.Visibility = Visibility.Visible;
                    string mediaTag = binding.MediaCmd switch
                    {
                        MediaCommand.Next => "next",
                        MediaCommand.Previous => "prev",
                        _ => "playpause"
                    };
                    foreach (ComboBoxItem item in MediaCombo.Items)
                        if (item.Tag?.ToString() == mediaTag)
                        { MediaCombo.SelectedItem = item; break; }
                    break;

                case ActionType.LaunchApp:
                    AppPanel.Visibility = Visibility.Visible;
                    AppPathBox.Text = binding.AppPath;
                    break;

                case ActionType.Shortcut:
                    ShortcutPanel.Visibility = Visibility.Visible;
                    ShortcutBox.Text = binding.ShortcutKeys;
                    break;

                case ActionType.OpenWebsite:
                    WebsitePanel.Visibility = Visibility.Visible;
                    WebsiteUrlBox.Text = binding.WebsiteUrl;
                    break;
            }
        }

        private void HideAllSubPanels()
        {
            MediaPanel.Visibility = Visibility.Collapsed;
            AppPanel.Visibility = Visibility.Collapsed;
            ShortcutPanel.Visibility = Visibility.Collapsed;
            WebsitePanel.Visibility = Visibility.Collapsed;
        }

        private void ActionTypeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            HideAllSubPanels();
            if (ActionTypeCombo.SelectedItem is not ComboBoxItem item) return;
            string tag = item.Tag?.ToString() ?? "none";

            switch (tag)
            {
                case "media": MediaPanel.Visibility = Visibility.Visible; break;
                case "app": AppPanel.Visibility = Visibility.Visible; break;
                case "shortcut": ShortcutPanel.Visibility = Visibility.Visible; break;
                case "website": WebsitePanel.Visibility = Visibility.Visible; break;
            }
        }

        // ─────────────────────────────────────────────
        //  APP PICKER
        // ─────────────────────────────────────────────
        private async void SelectApp_Click(object sender, RoutedEventArgs e)
        {
            var picker = new FileOpenPicker();
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_main!);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            picker.SuggestedStartLocation = PickerLocationId.ComputerFolder;
            picker.FileTypeFilter.Add(".exe");
            picker.FileTypeFilter.Add(".lnk");

            var file = await picker.PickSingleFileAsync();
            if (file != null)
                AppPathBox.Text = file.Path;
        }

        // ─────────────────────────────────────────────
        //  SHORTCUT LISTENER
        // ─────────────────────────────────────────────
        private void ListenShortcut_Click(object sender, RoutedEventArgs e)
        {
            if (_listeningForShortcut)
            {
                _listeningForShortcut = false;
                ListenButton.Content = "Ascolta";
                this.KeyDown -= OnShortcutKeyDown;
            }
            else
            {
                _listeningForShortcut = true;
                ListenButton.Content = "Stop";
                ShortcutBox.Text = "";
                this.KeyDown += OnShortcutKeyDown;
            }
        }

        private void OnShortcutKeyDown(object sender,
            Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
        {
            var mods = new System.Text.StringBuilder();
            var ctrlW = Microsoft.UI.Input.InputKeyboardSource
                              .GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control);
            var shiftW = Microsoft.UI.Input.InputKeyboardSource
                              .GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift);
            var altW = Microsoft.UI.Input.InputKeyboardSource
                              .GetKeyStateForCurrentThread(Windows.System.VirtualKey.Menu);

            bool ctrl = ctrlW.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
            bool shift = shiftW.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
            bool alt = altW.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

            var key = e.Key;
            if (key == Windows.System.VirtualKey.Control ||
                key == Windows.System.VirtualKey.Shift ||
                key == Windows.System.VirtualKey.Menu)
                return;

            if (ctrl) mods.Append("Ctrl+");
            if (shift) mods.Append("Shift+");
            if (alt) mods.Append("Alt+");
            mods.Append(key.ToString());

            ShortcutBox.Text = mods.ToString();
            _listeningForShortcut = false;
            ListenButton.Content = "Ascolta";
            this.KeyDown -= OnShortcutKeyDown;
            e.Handled = true;
        }

        // ─────────────────────────────────────────────
        //  BOTTOM BUTTONS
        // ─────────────────────────────────────────────
        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            _main?.CloseDetailPanel();
        }

        private void Apply_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedIndex == null || _main == null) return;

            var binding = new InputBinding();

            if (ActionTypeCombo.SelectedItem is ComboBoxItem actionItem)
            {
                binding.Action = actionItem.Tag?.ToString() switch
                {
                    "media" => ActionType.Media,
                    "app" => ActionType.LaunchApp,
                    "shortcut" => ActionType.Shortcut,
                    "website" => ActionType.OpenWebsite,
                    _ => ActionType.None
                };
            }

            if (binding.Action == ActionType.Media &&
                MediaCombo.SelectedItem is ComboBoxItem mediaItem)
            {
                binding.MediaCmd = mediaItem.Tag?.ToString() switch
                {
                    "next" => MediaCommand.Next,
                    "prev" => MediaCommand.Previous,
                    _ => MediaCommand.PlayPause
                };
            }

            if (binding.Action == ActionType.LaunchApp)
                binding.AppPath = AppPathBox.Text;

            if (binding.Action == ActionType.Shortcut)
                binding.ShortcutKeys = ShortcutBox.Text;

            if (binding.Action == ActionType.OpenWebsite)
                binding.WebsiteUrl = WebsiteUrlBox.Text.Trim();

            _main.TrussiRingBindings.Profile.Buttons[_selectedIndex.Value] = binding;
            _main.TrussiRingBindings.Save();

            Console.WriteLine($"[UI] TrussiRing key {_selectedIndex + 1} saved: {binding.Action}");
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            _main?.CloseDetailPanel();
        }

        private void UpdateFirmware_Click(object sender, RoutedEventArgs e)
        {
            Console.WriteLine("[UI] TrussiRing update firmware clicked.");
        }

        private void FactoryReset_Click(object sender, RoutedEventArgs e)
        {
            Console.WriteLine("[UI] TrussiRing factory reset clicked.");
        }
    }
}