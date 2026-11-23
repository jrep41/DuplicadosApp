using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;

namespace DuplicadosApp;

public partial class App : Application
{
    private const int DwmwaUseImmersiveDarkMode = 20;

    public bool IsDarkTheme { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        ApplyTheme(DetectWindowsDarkMode());
    }

    /// <summary>Cambia dinámicamente entre tema claro y oscuro y sincroniza la barra de título de Windows.</summary>
    public void ApplyTheme(bool dark)
    {
        IsDarkTheme = dark;
        var source = new Uri(dark ? "Themes/DarkPalette.xaml" : "Themes/LightPalette.xaml", UriKind.Relative);
        var dictionaries = Resources.MergedDictionaries;
        if (dictionaries.Count > 0)
        {
            dictionaries[0] = new ResourceDictionary { Source = source };
        }
        else
        {
            dictionaries.Add(new ResourceDictionary { Source = source });
        }

        foreach (Window window in Windows)
        {
            ApplyWindowTitleBarTheme(window, dark);
        }
    }

    public static void ApplyWindowTitleBarTheme(Window window, bool dark)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var useDark = dark ? 1 : 0;
        _ = DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref useDark, sizeof(int));
    }

    private static bool DetectWindowsDarkMode()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch
        {
            return false;
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(
            $"Se produjo un error inesperado:\n\n{e.Exception.Message}",
            "Error",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd,
        int attr,
        ref int attrValue,
        int attrSize);
}
