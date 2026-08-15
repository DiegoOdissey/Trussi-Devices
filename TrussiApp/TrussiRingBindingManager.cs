using System;
using System.IO;
using System.Text.Json;

namespace TrussiApp
{
    public class TrussiRingBindingProfile
    {
        public InputBinding[] Buttons { get; set; } = new InputBinding[4];

        public TrussiRingBindingProfile()
        {
            for (int i = 0; i < 4; i++) Buttons[i] = new InputBinding();
        }
    }

    public class TrussiRingBindingManager
    {
        private static string SavePath =>
            Path.Combine(AppPaths.GetLocalFolder(), "trussiring_bindings.json");

        public TrussiRingBindingProfile Profile { get; private set; } = new();

        public void Load()
        {
            try
            {
                if (!File.Exists(SavePath))
                {
                    Console.WriteLine("[TrussiRingBindings] No save file found — using defaults.");
                    return;
                }
                string json = File.ReadAllText(SavePath);
                var loaded = JsonSerializer.Deserialize<TrussiRingBindingProfile>(json);
                if (loaded != null)
                {
                    Profile = loaded;
                    Console.WriteLine($"[TrussiRingBindings] Loaded from {SavePath}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TrussiRingBindings] Load failed: {ex.Message}");
            }
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SavePath)!);
                string json = JsonSerializer.Serialize(Profile,
                    new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(SavePath, json);
                Console.WriteLine($"[TrussiRingBindings] Saved to {SavePath}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TrussiRingBindings] Save failed: {ex.Message}");
            }
        }

        public void ExecuteButton(int index)
        {
            if (index < 0 || index >= 4) return;
            var binding = Profile.Buttons[index];
            Console.WriteLine($"[TrussiRingBindings] Execute button {index + 1}: {binding.Action}");

            switch (binding.Action)
            {
                case ActionType.Media:
                    ActionExecutor.ExecuteMedia(binding.MediaCmd);
                    break;
                case ActionType.LaunchApp:
                    if (!string.IsNullOrEmpty(binding.AppPath))
                        ActionExecutor.ExecuteLaunchApp(binding.AppPath);
                    break;
                case ActionType.Shortcut:
                    if (!string.IsNullOrEmpty(binding.ShortcutKeys))
                        ActionExecutor.ExecuteShortcut(binding.ShortcutKeys);
                    break;
                case ActionType.OpenWebsite:
                    if (!string.IsNullOrWhiteSpace(binding.WebsiteUrl))
                        ActionExecutor.ExecuteOpenWebsite(binding.WebsiteUrl);
                    break;
            }
        }
    }
}