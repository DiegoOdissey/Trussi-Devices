using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;

namespace TrussiApp
{
    public class AudioSessionManager
    {
        // Public entry point — forces execution on an MTA thread-pool thread,
        // since WASAPI session enumeration can silently return empty on WinUI's STA UI thread
        public List<string> GetActiveProcessNames()
        {
            return Task.Run(() => GetActiveProcessNamesInternal()).GetAwaiter().GetResult();
        }

        public void AdjustVolume(string processName, int direction, float step = 0.05f)
        {
            Task.Run(() => AdjustVolumeInternal(processName, direction, step)).Wait();
        }

        public int GetVolumePct(string processName)
        {
            return Task.Run(() => GetVolumePctInternal(processName)).GetAwaiter().GetResult();
        }

        private int GetVolumePctInternal(string processName)
        {
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
                foreach (var device in devices)
                {
                    try
                    {
                        device.AudioSessionManager.RefreshSessions();
                        var sessions = device.AudioSessionManager.Sessions;
                        for (int i = 0; i < sessions.Count; i++)
                        {
                            var session = sessions[i];
                            try
                            {
                                int pid = (int)session.GetProcessID;
                                if (pid == 0) continue;
                                string name = GetProcessName(pid);
                                if (string.Equals(name, processName, StringComparison.OrdinalIgnoreCase))
                                {
                                    device.Dispose();
                                    return (int)(session.SimpleAudioVolume.Volume * 100f);
                                }
                            }
                            catch { }
                        }
                    }
                    catch { }
                    finally { device.Dispose(); }
                }
            }
            catch { }
            return 50;
        }

        private List<string> GetActiveProcessNamesInternal()
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                using var enumerator = new MMDeviceEnumerator();
                var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);

                Console.WriteLine($"[Audio] Found {devices.Count} active render device(s).");

                foreach (var device in devices)
                {
                    try
                    {
                        Console.WriteLine($"[Audio] Checking device: {device.FriendlyName}");
                        device.AudioSessionManager.RefreshSessions();
                        var sessions = device.AudioSessionManager.Sessions;
                        Console.WriteLine($"[Audio]   Session count: {sessions.Count}");

                        for (int i = 0; i < sessions.Count; i++)
                        {
                            var session = sessions[i];
                            try
                            {
                                int pid = (int)session.GetProcessID;
                                if (pid == 0) continue;

                                string name = GetProcessName(pid);
                                if (!string.IsNullOrEmpty(name))
                                {
                                    names.Add(name);
                                    Console.WriteLine($"[Audio]   -> {name} (pid {pid}, state {session.State})");
                                }
                            }
                            catch (Exception exInner)
                            {
                                Console.WriteLine($"[Audio]   Session {i} error: {exInner.Message}");
                            }
                        }
                    }
                    catch (Exception exDevice)
                    {
                        Console.WriteLine($"[Audio] Device error ({device.FriendlyName}): {exDevice.Message}");
                    }
                    finally
                    {
                        device.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Audio] Failed to enumerate devices: {ex.Message}");
            }

            var result = names.OrderBy(n => n).ToList();
            Console.WriteLine($"[Audio] Active sessions (all devices): {string.Join(", ", result)}");
            return result;
        }

        private void AdjustVolumeInternal(string processName, int direction, float step)
        {
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
                bool adjusted = false;

                foreach (var device in devices)
                {
                    try
                    {
                        device.AudioSessionManager.RefreshSessions();
                        var sessions = device.AudioSessionManager.Sessions;

                        for (int i = 0; i < sessions.Count; i++)
                        {
                            var session = sessions[i];
                            try
                            {
                                int pid = (int)session.GetProcessID;
                                if (pid == 0) continue;

                                string name = GetProcessName(pid);
                                if (!string.Equals(name, processName,
                                        StringComparison.OrdinalIgnoreCase))
                                    continue;

                                var volume = session.SimpleAudioVolume;
                                float current = volume.Volume;
                                float updated = Math.Clamp(
                                    current + (direction > 0 ? step : -step), 0f, 1f);
                                volume.Volume = updated;
                                adjusted = true;

                                Console.WriteLine(
                                    $"[Audio] {processName} (pid {pid}) on '{device.FriendlyName}': " +
                                    $"{current:P0} -> {updated:P0}");
                            }
                            catch { }
                        }
                    }
                    catch (Exception exDevice)
                    {
                        Console.WriteLine($"[Audio] Device error during adjust: {exDevice.Message}");
                    }
                    finally
                    {
                        device.Dispose();
                    }
                }

                if (!adjusted)
                    Console.WriteLine(
                        $"[Audio] No active session found for '{processName}' across any device.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Audio] Adjust volume failed: {ex.Message}");
            }
        }

        private static string GetProcessName(int pid)
        {
            try
            {
                using var process = System.Diagnostics.Process.GetProcessById(pid);
                return process.ProcessName;
            }
            catch
            {
                return "";
            }
        }
    }
}