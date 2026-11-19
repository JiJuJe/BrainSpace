using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;

namespace BrainSpace;

public partial class App : Application
{
    private Mutex? instance;
    private bool ownsMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var arguments = e.Args;
        if (arguments.Length > 0 && arguments[0] == "--self-test")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            int result = SmokeTests.Run(arguments.Length > 1 ? arguments[1] : null);
            Environment.ExitCode = result;
            Shutdown(result);
            return;
        }
        string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BrainSpace");
        if (arguments.Length == 2 && arguments[0] == "--data") folder = Path.GetFullPath(arguments[1]);
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(folder.ToUpperInvariant())))[..32];
        try
        {
            instance = new Mutex(false, "Local\\BrainSpace_" + key);
            try { ownsMutex = instance.WaitOne(0); } catch (AbandonedMutexException) { ownsMutex = true; }
            if (!ownsMutex)
            {
                MessageBox.Show("BrainSpace is already open for this data folder.", "BrainSpace");
                Shutdown(1);
                return;
            }
            var store = new Storage(folder);
            try { MainWindow = new Shell(store); }
            catch { store.Dispose(); throw; }
            MainWindow.Show();
        }
        catch (Exception error)
        {
            MessageBox.Show($"BrainSpace couldn’t open its local data.\n\n{error.Message}\n\n{folder}", "BrainSpace", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (ownsMutex) instance?.ReleaseMutex();
        instance?.Dispose();
        base.OnExit(e);
    }
}
