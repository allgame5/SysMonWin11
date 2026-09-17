using System;
using System.Windows;

namespace Win11SysDash
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Globale Fehlerbehandlung aktivieren, damit Fehler nicht stumm verschluckt werden
            AppDomain.CurrentDomain.UnhandledException += (s, args) =>
            {
                MessageBox.Show($"Schwerwiegender Fehler beim Start:\n{args.ExceptionObject}", 
                                "App Absturz", MessageBoxButton.OK, MessageBoxImage.Error);
            };

            try
            {
                // Fenster manuell erstellen und anzeigen
                MainWindow window = new MainWindow();
                window.Show();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Fehler beim Erstellen von MainWindow:\n{ex.Message}\n\n{ex.StackTrace}", 
                                "MainWindow Fehler", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown();
            }
        }
    }
}