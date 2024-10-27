using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using WindowResizer.Common.Shortcuts;
using WindowResizer.Common.Utils;

namespace WindowResizer.Configuration;

public class ProfileConfig
{
    public string ProfileId { get; set; } = string.Empty;

    public string ProfileName { get; set; } = string.Empty;

    public bool DisableInFullScreen { get; set; } = true;

    public bool RestoreAllIncludeMinimized { get; set; } = false;

    public bool NotifyOnSaved { get; set; } = false;

    public bool EnableResizeByTitle { get; set; } = true;

    public bool EnableAutoResizeDelay { get; set; } = false;

    public bool CheckUpdate { get; set; } = true;

    public Dictionary<HotkeysType, Hotkeys> Keys { get; } = new();

    public List<WindowSize> WindowSizes { get; set; } = new();

    [JsonIgnore] public readonly ProfileConfigEvents ProfileConfigEvents = new();

    public List<WindowSize> GetWindowSizes()
    {
        return EnableResizeByTitle
            ? WindowSizes
            : WindowSizes.Where(w => w.Title.Equals("*")).ToList();
    }

    public void RemoveWindowSize(string windowSizeId)
    {
        var windowSize = WindowSizes.FirstOrDefault(w => w.WindowSizeId == windowSizeId);
        if (windowSize is null)
        {
            return;
        }

        if (EnableResizeByTitle)
        {
            WindowSizes.Remove(windowSize);
        }
        else // remove all windowSize with same process Name
        {
            WindowSizes.RemoveAll(i => i.Name.Equals(windowSize.Name, StringComparison.OrdinalIgnoreCase));
        }
    }

    public void UpdateWindowSize(WindowSize windowSize)
    {
        var exists = WindowSizes.FirstOrDefault(w => w.WindowSizeId == windowSize.WindowSizeId);
        if (exists is null || exists == windowSize)
        {
            return;
        }

        exists.AutoResize = windowSize.AutoResize;
        exists.AutoResizeDelay = windowSize.AutoResizeDelay;
        exists.Name = windowSize.Name;
        exists.Title = windowSize.Title;
        exists.Rect = windowSize.Rect;
        exists.State = windowSize.State;
        exists.MaximizedPosition = windowSize.MaximizedPosition;
    }

    /// <summary>
    /// Update auto resize delay for process
    /// </summary>
    public void UpdateAutoResize(string? windowSizeId, bool? enable, int? delayMilliseconds)
    {
        var windowSize = WindowSizes.FirstOrDefault(w => w.WindowSizeId == windowSizeId);
        if (windowSize is null)
        {
            return;
        }

        var processWindowSize =
            WindowSizes.Where(i => i.Name.Equals(windowSize.Name, StringComparison.OrdinalIgnoreCase));
        foreach (var ps in processWindowSize)
        {
            if (enable.HasValue)
            {
                ps.AutoResize = enable.Value;
            }

            if (delayMilliseconds.HasValue)
            {
                ps.AutoResizeDelay = delayMilliseconds.Value;
            }
        }

        ProfileConfigEvents.AutoResizeChanged?.Invoke();
    }

    public static Dictionary<HotkeysType, Hotkeys> DefaultKeys => new()
    {
        { HotkeysType.Save, new Hotkeys { ModifierKeys = ["Ctrl", "Alt"], Key = "S" } },
        { HotkeysType.Restore, new Hotkeys { ModifierKeys = ["Ctrl", "Alt"], Key = "R" } }
    };

    public static ProfileConfig NewConfig(string profileName)
    {
        var c = new ProfileConfig
        {
            ProfileId = ConfigHelper.GenerateConfigId(),
            DisableInFullScreen = true,
            CheckUpdate = true,
            ProfileName = profileName,
        };

        foreach (var key in DefaultKeys)
        {
            c.Keys.Add(key.Key, key.Value);
        }

        return c;
    }

    public bool Equals(ProfileConfig? x, ProfileConfig? y)
    {
        if (x is null || y is null)
        {
            return false;
        }

        return x.ProfileId.Equals(y.ProfileId, StringComparison.Ordinal);
    }

    public int GetHashCode(ProfileConfig obj)
    {
        return obj.ProfileId.GetHashCode();
    }
}
