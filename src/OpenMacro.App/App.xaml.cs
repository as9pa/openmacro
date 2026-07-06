using System.Windows;

namespace OpenMacro.App;

public partial class App : Application
{
    // Two instances would fight over the hook and the config file.
    private static Mutex? instanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        instanceMutex = new Mutex(initiallyOwned: true, @"Local\openmacro-app", out var createdNew);
        if (!createdNew)
        {
            MessageBox.Show("openmacro is already running.", "openmacro");
            Shutdown();
            return;
        }

        base.OnStartup(e);
    }
}
