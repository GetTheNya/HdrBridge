using System.Diagnostics;
using Microsoft.Toolkit.Uwp.Notifications;
using Windows.UI.Notifications;

namespace SyncLightBridge.Services;

public sealed class NotificationService {
    public void Show(string title, string body) {
        try {
            var toastContent = new ToastContentBuilder()
                .AddText(title)
                .AddText(body)
                .GetToastContent();

            ToastNotificationManagerCompat.CreateToastNotifier().Show(new ToastNotification(toastContent.GetXml()));
        } catch (Exception ex) {
            Debug.WriteLine($"Toast failed: {ex.Message}");
        }
    }
}
