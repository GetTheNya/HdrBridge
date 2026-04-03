using System.Diagnostics;
using System.IO;
using System.Windows;
using Windows.UI.Notifications;
using Microsoft.Toolkit.Uwp.Notifications;

namespace HdrBridge.Services;

public sealed class NotificationService {
    private readonly string? _iconPath;

    public NotificationService() {
        try {
            // Toast notifications need a file:// URI, not a pack:// URI.
            // Extract the app icon to a temp location once at startup.
            var tempDir = Path.Combine(Path.GetTempPath(), "HdrBridge");
            Directory.CreateDirectory(tempDir);
            _iconPath = Path.Combine(tempDir, "icon.ico");

            if (!File.Exists(_iconPath)) {
                var resourceStream = Application.GetResourceStream(
                    new Uri("pack://application:,,,/Resources/app_hires.ico"))?.Stream;
                if (resourceStream != null) {
                    using var fileStream = File.Create(_iconPath);
                    resourceStream.CopyTo(fileStream);
                    resourceStream.Dispose();
                }
            }
        } catch (Exception ex) {
            Debug.WriteLine($"Icon extraction failed: {ex.Message}");
            _iconPath = null;
        }
    }

    public void Show(string title, string body) {
        try {
            var builder = new ToastContentBuilder()
                .AddText(title)
                .AddText(body);

            if (_iconPath != null && File.Exists(_iconPath)) {
                builder.AddAppLogoOverride(new Uri(_iconPath));
            }

            var toastContent = builder.GetToastContent();
            ToastNotificationManagerCompat.CreateToastNotifier()
                .Show(new ToastNotification(toastContent.GetXml()));
        } catch (Exception ex) {
            Debug.WriteLine($"Toast failed: {ex.Message}");
        }
    }
}
