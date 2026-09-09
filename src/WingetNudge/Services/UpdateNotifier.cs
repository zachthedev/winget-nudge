using Microsoft.Windows.AppNotifications;
using WingetNudge.Core.Notifications;

namespace WingetNudge.Services;

/// <summary>Shows the update notification.</summary>
public static class UpdateNotifier
{
    /// <summary>
    /// Registers this process with the notification platform. Must run before a notification is
    /// shown and before activation arguments are read.
    /// </summary>
    public static void Register()
    {
        AppNotificationManager.Default.Register();
    }

    /// <summary>Shows the update notification.</summary>
    /// <param name="names">Package names followed by tool names.</param>
    /// <returns><c>true</c> when a notification was shown.</returns>
    public static bool Show(IReadOnlyList<string> names)
    {
        UpdateNotificationText? text = UpdateNotificationText.Compose(names);
        if (text is null)
        {
            return false;
        }

        string payload = NotificationPayload.Build(text, AppServices.Current.IconPngPath);
        AppNotificationManager.Default.Show(new AppNotification(payload));
        return true;
    }
}
