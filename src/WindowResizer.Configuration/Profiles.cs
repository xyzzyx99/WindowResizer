using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;

namespace WindowResizer.Configuration;

public class Profiles
{
    public Profiles()
    {
    }

    [JsonConstructor]
    public Profiles(string currentProfileId, List<ProfileConfig> configs)
    {
        CurrentProfileId = currentProfileId;
        Configs = configs;
    }

    public string CurrentProfileId { get; private set; } = string.Empty;

    public List<ProfileConfig> Configs { get; private set; } = new();

    [JsonIgnore] public const string DefaultProfileName = "default";

    [JsonIgnore] public ProfileConfig? Current => Get(CurrentProfileId);

    [JsonIgnore] public readonly ProfileEvents ProfileEvents = new();

    public ProfileConfig UseDefault()
    {
        Configs.Clear();
        var defaultConfig = Add(DefaultProfileName);
        CurrentProfileId = defaultConfig.ProfileId;
        return defaultConfig;
    }

    public bool Rename(string profileId, string profileName)
    {
        var profile = Get(profileId);
        if (profile is null)
        {
            return false;
        }

        profile.ProfileName = profileName;
        ProfileEvents.ProfileRename?.Invoke(profileId, profileName);
        return true;
    }

    public ProfileConfig Add(string profileName)
    {
        var newConfig = ProfileConfig.NewConfig(profileName);
        Configs.Add(newConfig);
        ProfileEvents.ProfileAdd?.Invoke(newConfig.ProfileId, newConfig.ProfileName);
        return newConfig;
    }

    public bool Remove(string profileId)
    {
        var config = Get(profileId);
        if (config is null)
        {
            return false;
        }

        Configs.Remove(config);
        ProfileEvents.ProfileRemove?.Invoke(config.ProfileId);
        return true;
    }


    public bool Switch(string profileId)
    {
        if (profileId.Equals(CurrentProfileId, StringComparison.Ordinal))
        {
            return true;
        }

        var p = Get(profileId);
        if (p is null)
        {
            return false;
        }

        CurrentProfileId = p.ProfileId;
        ProfileEvents.ProfileSwitch?.Invoke(p.ProfileId);
        return true;
    }

    public void Updated()
    {
        ProfileEvents.ProfileConfigUpdated?.Invoke();
    }

    private ProfileConfig? Get(string profileId) =>
        Configs.FirstOrDefault(i => i.ProfileId.Equals(profileId, StringComparison.Ordinal));
}
