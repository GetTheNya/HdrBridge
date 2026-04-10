using System;
using System.Windows;
using Velopack;

namespace HdrBridge;

public static class Program {
    [STAThread]
    public static void Main() {
        VelopackApp.Build().Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
