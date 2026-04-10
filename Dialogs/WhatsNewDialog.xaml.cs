using System.Windows;

namespace HdrBridge.Dialogs;

public partial class WhatsNewDialog : Window {
    public WhatsNewDialog(string version, string releaseNotes) {
        InitializeComponent();
        VersionText.Text = version;
        ReleaseNotesText.Text = releaseNotes;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) {
        Close();
    }
}
