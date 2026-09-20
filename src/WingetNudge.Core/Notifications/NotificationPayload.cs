using System.Security;
using System.Text;
using WingetNudge.Core.Actions;

namespace WingetNudge.Core.Notifications;

/// <summary>Builds the toast XML for the update notification.</summary>
/// <remarks>
/// The App SDK builder has no system-dismiss button, and that button is what lets the toast
/// close without launching the app, so the payload is assembled by hand.
/// </remarks>
public static class NotificationPayload
{
    /// <summary>Builds the toast XML.</summary>
    /// <param name="text">Title and body.</param>
    /// <param name="logoPath">Local PNG for the app logo override.</param>
    /// <returns>Toast XML payload.</returns>
    public static string Build(UpdateNotificationText text, string logoPath)
    {
        StringBuilder xml = new();
        xml.Append("<toast launch=\"")
            .Append(SecurityElement.Escape(UpdateActions.LaunchArgument))
            .Append("\" activationType=\"foreground\">");
        xml.Append("<visual><binding template=\"ToastGeneric\">");
        xml.Append("<text>").Append(SecurityElement.Escape(text.Title)).Append("</text>");
        xml.Append("<text>").Append(SecurityElement.Escape(text.Body)).Append("</text>");
        xml.Append("<image placement=\"appLogoOverride\" src=\"")
            .Append(SecurityElement.Escape(new Uri(logoPath).AbsoluteUri))
            .Append("\"/>");
        xml.Append("</binding></visual><actions>");
        foreach (UpdateAction action in UpdateActions.Buttons)
        {
            xml.Append("<action content=\"").Append(SecurityElement.Escape(action.Label)).Append('"');
            if (action.IsDismiss)
            {
                xml.Append(" arguments=\"dismiss\" activationType=\"system\"");
            }
            else
            {
                xml.Append(" arguments=\"")
                    .Append(SecurityElement.Escape($"{UpdateActions.ActionKey}={action.Argument}"))
                    .Append("\" activationType=\"foreground\"");
            }

            xml.Append("/>");
        }

        xml.Append("</actions></toast>");
        return xml.ToString();
    }
}
