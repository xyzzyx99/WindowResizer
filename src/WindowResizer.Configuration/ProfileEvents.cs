namespace WindowResizer.Configuration;

public class ProfileEvents
{
    public delegate void ProfileAddEvent(string profileId, string profileName);

    public ProfileAddEvent? ProfileAdd;

    public delegate void ProfileRenameEvent(string profileId, string profileName);

    public ProfileRenameEvent? ProfileRename;

    public delegate void ProfileRemoveEvent(string profileId);

    public ProfileRemoveEvent? ProfileRemove;

    public delegate void ProfileSwitchEvent(string profileId);

    public ProfileSwitchEvent? ProfileSwitch;

    public delegate void ProfileConfigUpdatedEvent();

    public ProfileConfigUpdatedEvent? ProfileConfigUpdated;
}

public class ProfileConfigEvents
{
    public delegate void AutoResizeChangedEvent();

    public AutoResizeChangedEvent? AutoResizeChanged;

    public delegate void KeysChangedEvent();

    public KeysChangedEvent? KeysChanged;
}
