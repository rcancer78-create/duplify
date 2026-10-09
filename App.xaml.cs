using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Duplify.Services;
using Duplify.ViewModels;

namespace Duplify;

public partial class App : Application
{
    private MainViewModel? _mainViewModel;

    public App()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            if (e.Args.Length > 0 && e.Args[0] == "--test")
            {
                int exitCode = await Tests.TestRunner.RunTestsAsync();
                Shutdown(exitCode);
                return;
            }

            var settingsService = new SettingsService();
            var scannerService = new ScannerService();
            var journal = new ActionJournal();
            var fileActionService = new FileActionService(journal);
            var thumbnailService = new ImageThumbnailService();
            var dialogService = new DialogService();

            _mainViewModel = new MainViewModel(
                scannerService,
                fileActionService,
                settingsService,
                thumbnailService,
                dialogService,
                journal);

            var mainWindow = new MainWindow
            {
                DataContext = _mainViewModel
            };

            MainWindow = mainWindow;
            mainWindow.Show();
        }
        catch (Exception ex)
        {
            HandleFatal("OnStartup", ex);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _mainViewModel?.SaveSettings();
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        HandleFatal("DispatcherUnhandledException", e.Exception);

        // Keep the process alive so the user still sees a message instead of a silent disappearance.
        e.Handled = true;
    }

    private void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        HandleFatal("AppDomain.UnhandledException", e.ExceptionObject as Exception);
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        HandleFatal("TaskScheduler.UnobservedTaskException", e.Exception);
        e.SetObserved();
    }

    private static void HandleFatal(string source, Exception? exception)
    {
        string logPath = CrashLogger.LogException(source, exception);
        ShowFatalMessage(logPath);
    }

    private static void ShowFatalMessage(string logPath)
    {
        string message = "Произошла непредвиденная ошибка Duplify.\n";

        if (!string.IsNullOrEmpty(logPath))
        {
            message += $"\nПодробности сохранены в журнале:\n{logPath}";
        }
        else
        {
            message += "\nНе удалось записать журнал ошибки.";
        }

        try
        {
            var dispatcher = Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess())
            {
                dispatcher.Invoke(new Action(() => ShowMessageBox(message)));
            }
            else
            {
                ShowMessageBox(message);
            }
        }
        catch
        {
            try
            {
                ShowMessageBox(message);
            }
            catch
            {
                // Nothing more we can safely do.
            }
        }
    }

    private static void ShowMessageBox(string message)
    {
        MessageBox.Show(message, "Duplify — ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
