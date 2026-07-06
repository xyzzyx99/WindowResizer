using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using WindowResizer.Common.Windows;
using WindowResizer.Configuration;
using WindowResizer.Core.WindowControl;
using static WindowResizer.Base.WindowUtils;

namespace WindowResizer.Base;

public static class WindowCmd
{
    public static bool Resize(string? configPath, string? profileName, string? process, string? title,
        Action<string>? onError = null,
        Action<List<TargetWindow>>? onDebug = null)
    {
        var profile = LoadConfig(configPath, profileName, onError);
        if (profile is null)
        {
            return false;
        }

        var windows = Resizer.GetOpenWindows();
        windows.Reverse();

        var targets = new List<TargetWindow>();

        foreach (var handler in windows)
        {
            if (!IsProcessAvailable(handler, out string processName, null))
            {
                continue;
            }

            var t = Resizer.GetWindowTitle(handler);

            targets.Add(CreateTargetWindow(handler, processName));
        }

        bool resizeAllProcesses = string.IsNullOrEmpty(process);

        if (!resizeAllProcesses)
        {
            targets = targets.Where(i => i.ProcessName.Equals(process, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        if (!string.IsNullOrEmpty(title))
        {
            var regex = new Regex(title);
            targets = targets.Where(i => !string.IsNullOrEmpty(i.Title) && regex.IsMatch(i.Title!)).ToList();
        }

        foreach (var tp in targets)
        {
            ResizeWindow(tp.Handle, profile, (p, e) =>
            {
                tp.Result = "Elevated privileges may be required.";
                if (!resizeAllProcesses)
                {
                    onError?.Invoke($"Unable to resize process <{p}>, elevated privileges may be required.");
                }
            }, (p, t) =>
            {
                var message = $"No saved settings.";
                tp.Result = message;
                if (!resizeAllProcesses)
                {
                    onError?.Invoke($"No saved settings for <{p} :: {t}>.");
                }
            });
        }

        onDebug?.Invoke(targets);

        return true;
    }

    public static bool ResizeSelected(string? configPath, string? profileName, TargetWindow target,
        Action<string>? onError = null,
        Action<List<TargetWindow>>? onDebug = null)
    {
        var profile = LoadConfig(configPath, profileName, onError);
        if (profile is null)
        {
            return false;
        }

        ResizeWindow(target.Handle, profile, (p, e) =>
        {
            target.Result = "Elevated privileges may be required.";
            onError?.Invoke($"Unable to resize process <{p}>, elevated privileges may be required.");
        }, (p, t) =>
        {
            target.Result = "No saved settings.";
            onError?.Invoke($"No saved settings for <{p} :: {t}>.");
        });

        var targets = new List<TargetWindow> { target };
        onDebug?.Invoke(targets);
        return string.IsNullOrEmpty(target.Result);
    }

    public static bool ResizeDirect(TargetWindow target, IReadOnlyList<int> windowArguments,
        Action<string>? onError = null,
        Action<List<TargetWindow>>? onDebug = null)
    {
        if (windowArguments.Count > 4)
        {
            onError?.Invoke("The -w/--window option accepts at most four numbers: left top right bottom.");
            return false;
        }

        var targets = new List<TargetWindow> { target };
        var success = ApplyDirectResize(targets, windowArguments, onError);
        onDebug?.Invoke(targets);
        return success;
    }

    public static bool ResizeDirect(string? process, string? title, IReadOnlyList<int> windowArguments,
        Action<string>? onError = null,
        Action<List<TargetWindow>>? onDebug = null)
    {
        if (windowArguments.Count > 4)
        {
            onError?.Invoke("The -w/--window option accepts at most four numbers: left top right bottom.");
            return false;
        }

        var targets = GetTargets(process, title, onError);
        if (!targets.Any())
        {
            onError?.Invoke(string.IsNullOrWhiteSpace(process)
                ? "No foreground window found."
                : $"No matching windows found for process <{process}>.");
            onDebug?.Invoke(targets);
            return false;
        }

        var success = ApplyDirectResize(targets, windowArguments, onError);
        onDebug?.Invoke(targets);
        return success;
    }

    public class TargetWindow
    {
        public TargetWindow(IntPtr handle, string processName, string? title, int processId = 0)
        {
            Handle = handle;
            ProcessName = processName;
            Title = title;
            ProcessId = processId;
        }

        public IntPtr Handle { get; }

        public string ProcessName { get; }

        public int ProcessId { get; }

        public bool IsTopForProcess { get; set; }

        public string? Title { get; }

        public string Result { get; set; } = string.Empty;
    }

    public static List<TargetWindow> GetSelectableTargets(string? process, string? title, Action<string>? onError)
    {
        var targets = GetOpenWindowTargets();

        if (!string.IsNullOrWhiteSpace(process))
        {
            targets = targets.Where(i => i.ProcessName.Equals(process, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        Regex? titleRegex = null;
        if (!string.IsNullOrWhiteSpace(title))
        {
            try
            {
                titleRegex = new Regex(title);
            }
            catch (Exception e)
            {
                onError?.Invoke($"Invalid title regex <{title}>: {e.Message}");
                return new List<TargetWindow>();
            }
        }

        if (titleRegex != null)
        {
            targets = targets.Where(i => !string.IsNullOrEmpty(i.Title) && titleRegex.IsMatch(i.Title!)).ToList();
        }

        MarkTopForProcess(targets);
        return targets;
    }

    public static List<TargetWindow> GetOpenWindowTargets()
    {
        var windows = Resizer.GetOpenWindows();
        var targets = new List<TargetWindow>();

        foreach (var handler in windows)
        {
            if (!IsProcessAvailable(handler, out string processName, null))
            {
                continue;
            }

            targets.Add(CreateTargetWindow(handler, processName));
        }

        return targets;
    }

    private static TargetWindow CreateTargetWindow(IntPtr handle, string processName)
    {
        return new TargetWindow(handle, processName, Resizer.GetWindowTitle(handle), GetProcessId(handle));
    }

    private static int GetProcessId(IntPtr handle)
    {
        try
        {
            return Resizer.GetRealProcess(handle)?.Id ?? 0;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private static void MarkTopForProcess(List<TargetWindow> targets)
    {
        var keys = targets.ToDictionary(target => target, GetProcessKey);
        var counts = keys.Values
                         .GroupBy(key => key, StringComparer.OrdinalIgnoreCase)
                         .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var target in targets)
        {
            target.IsTopForProcess = false;
            var key = keys[target];
            if (counts[key] > 1 && seen.Add(key))
            {
                target.IsTopForProcess = true;
            }
        }
    }

    private static string GetProcessKey(TargetWindow target)
    {
        return target.ProcessId > 0 ? $"pid:{target.ProcessId}" : $"name:{target.ProcessName}";
    }

    private static List<TargetWindow> GetTargets(string? process, string? title, Action<string>? onError)
    {
        var targets = new List<TargetWindow>();
        if (string.IsNullOrWhiteSpace(process))
        {
            var foreground = Resizer.GetForegroundHandle();
            if (IsProcessAvailable(foreground, out string processName, null))
            {
                targets.Add(CreateTargetWindow(foreground, processName));
            }

            MarkTopForProcess(targets);
            return targets;
        }

        targets = GetSelectableTargets(process, title, onError);
        targets.Reverse();
        return targets;
    }

    private static bool ApplyDirectResize(List<TargetWindow> targets, IReadOnlyList<int> windowArguments, Action<string>? onError)
    {
        foreach (var tp in targets)
        {
            try
            {
                var currentRect = Resizer.GetRect(tp.Handle);
                var targetRect = BuildDirectRect(tp.Handle, currentRect, windowArguments);
                var placement = Resizer.GetPlacement(tp.Handle);
                if (!Resizer.SetPlacement(tp.Handle, targetRect, placement.MaximizedPosition, WindowState.Normal))
                {
                    tp.Result = "Unable to move or resize the window.";
                    onError?.Invoke($"Unable to move or resize process <{tp.ProcessName}>.");
                }
            }
            catch (Exception e)
            {
                tp.Result = e.Message;
                onError?.Invoke($"Unable to move or resize process <{tp.ProcessName}>: {e.Message}");
            }
        }

        return targets.Any(t => string.IsNullOrEmpty(t.Result));
    }

    private static Rect BuildDirectRect(IntPtr handle, Rect currentRect, IReadOnlyList<int> windowArguments)
    {
        var currentWidth = currentRect.Right - currentRect.Left;
        var currentHeight = currentRect.Bottom - currentRect.Top;

        if (windowArguments.Count == 0)
        {
            var workingArea = Screen.FromHandle(handle).WorkingArea;
            var width = workingArea.Width * 2 / 3;
            var height = workingArea.Height * 3 / 4;
            var left = workingArea.Left + (workingArea.Width - width) / 2;
            var top = workingArea.Top + (workingArea.Height - height) / 2;
            return new Rect(left, top, left + width, top + height);
        }

        var targetLeft = windowArguments[0];
        var targetTop = windowArguments.Count >= 2 ? windowArguments[1] : currentRect.Top;
        var targetRight = windowArguments.Count >= 3 ? windowArguments[2] : targetLeft + currentWidth;
        var targetBottom = windowArguments.Count >= 4 ? windowArguments[3] : targetTop + currentHeight;

        if (targetRight <= targetLeft)
        {
            targetRight = targetLeft + currentWidth;
        }

        if (targetBottom <= targetTop)
        {
            targetBottom = targetTop + currentHeight;
        }

        return new Rect(targetLeft, targetTop, targetRight, targetBottom);
    }

    private static Config? LoadConfig(string? configPath, string? profileName, Action<string>? onError)
    {
        if (!ConfigUtils.Load(configPath, onError))
        {
            return null;
        }

        if (string.IsNullOrEmpty(profileName))
        {
            return ConfigFactory.Current;
        }

        var p = ConfigFactory.Profiles.Configs.FirstOrDefault(i =>
            i.ProfileName.Equals(profileName, StringComparison.OrdinalIgnoreCase));
        if (p is null)
        {
            onError?.Invoke($"Profile <{profileName}> not exists");
        }

        return p;
    }
}
