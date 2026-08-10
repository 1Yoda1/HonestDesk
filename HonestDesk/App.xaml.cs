using System.Windows;
using System.Windows.Threading;
using HonestDesk.Services;

namespace HonestDesk;

public partial class App : System.Windows.Application
{
    private const string SingleInstanceMutexName = @"Local\HonestDesk.SingleInstance";
    private readonly AppLogger _logger = new();
    private Mutex? _singleInstanceMutex;
    private bool _ownsSingleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        RegisterExceptionBoundaries();
        _logger.Info("Startup stage: Application.OnStartup");
        try
        {
            _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName,
                out _ownsSingleInstanceMutex);
            if (!_ownsSingleInstanceMutex)
            {
                _logger.Info("Startup stopped: another HonestDesk instance is already running.");
                System.Windows.MessageBox.Show("HonestDesk уже запущен.", "HonestDesk",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown();
                return;
            }

            base.OnStartup(e);
            _logger.Info("Startup stage: Creating MainWindow");
            MainWindow = new MainWindow();
            MainWindow.Show();
        }
        catch (Exception ex)
        {
            _logger.Critical("Startup stage: Application.OnStartup/MainWindow construction", ex);
            System.Windows.MessageBox.Show("HonestDesk не удалось запустить. Подробности записаны в журнал:\n" +
                            AppPaths.LogPath, "Ошибка запуска HonestDesk",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(-1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_ownsSingleInstanceMutex)
            _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }

    private void RegisterExceptionBoundaries()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            var exception = args.ExceptionObject as Exception
                ?? new InvalidOperationException("Unhandled non-Exception object: " + args.ExceptionObject);
            _logger.Critical("AppDomain.CurrentDomain.UnhandledException; terminating=" + args.IsTerminating,
                exception);
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            _logger.Error("TaskScheduler.UnobservedTaskException", args.Exception);
            args.SetObserved();
        };
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _logger.Critical("Application.DispatcherUnhandledException", e.Exception);
        e.Handled = true;
        System.Windows.MessageBox.Show("Произошла критическая ошибка. HonestDesk будет закрыт. Подробности записаны в журнал:\n" +
                        AppPaths.LogPath, "Критическая ошибка HonestDesk",
            MessageBoxButton.OK, MessageBoxImage.Error);
        Shutdown(-1);
    }
}
