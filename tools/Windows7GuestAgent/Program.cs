using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Globalization;

namespace CableRoutePlanner.Windows7GuestAgent;

internal static class Program
{
    private const string Schema = "maps.win7.prototype.v1";

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int GetWindowText(IntPtr window, [Out] char[] text, int length);

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool GetWindowRect(IntPtr window, out Win32Rect rectangle);

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool IsWindowVisible(IntPtr window);

    private static int Main(string[] args)
    {
        AgentOptions? options = null;
        string? diagnosticsPath = null;
        Process? prototype = null;
        try
        {
            options = AgentOptions.Parse(args);
            Directory.CreateDirectory(options.ResultsDirectory);
            diagnosticsPath = Path.Combine(options.ResultsDirectory, "win7-prototype-agent.log");
            string resultPath = Path.Combine(options.ResultsDirectory, "win7-prototype-result.json");
            string screenshotPath = Path.Combine(options.ResultsDirectory, "win7-prototype-screenshot.png");
            string readinessPath = Path.Combine(options.ResultsDirectory, "prototype-ready.json");
            Environment.SetEnvironmentVariable("WIN7_PROTOTYPE_READY_PATH", readinessPath);
            prototype = Process.Start(new ProcessStartInfo
            {
                FileName = options.PrototypePath,
                WorkingDirectory = Path.GetDirectoryName(options.PrototypePath) ?? ".",
                UseShellExecute = true
            });
            if (prototype is null)
            {
                throw new InvalidOperationException("The prototype process could not be started.");
            }

            DateTime deadline = DateTime.UtcNow.AddSeconds(options.TimeoutSeconds);
            while (DateTime.UtcNow < deadline && !prototype.HasExited && prototype.MainWindowHandle == IntPtr.Zero)
            {
                Thread.Sleep(250);
                prototype.Refresh();
            }

            if (prototype.HasExited)
            {
                throw new InvalidOperationException("The prototype exited before creating a window.");
            }

            IntPtr window = prototype.MainWindowHandle;
            if (window == IntPtr.Zero || !IsWindowVisible(window) || !GetWindowRect(window, out Win32Rect bounds))
            {
                throw new InvalidOperationException("The Avalonia window was not visible.");
            }

            char[] titleBuffer = new char[512];
            int titleLength = GetWindowText(window, titleBuffer, titleBuffer.Length);
            string title = new(titleBuffer, 0, titleLength);
            if (title.IndexOf("SQLite=Minsk", StringComparison.Ordinal) < 0
                || title.IndexOf("MapPoint=53.9,27.56", StringComparison.Ordinal) < 0)
            {
                throw new InvalidOperationException("The window did not expose the expected SQLite map point.");
            }

            if (!WindowGeometry.IsUsable(bounds))
            {
                throw new InvalidOperationException("The prototype window is smaller than the smoke-test viewport.");
            }

            string readiness = File.Exists(readinessPath) ? File.ReadAllText(readinessPath) : string.Empty;
            if (readiness.IndexOf("\"mapLayer\":\"SQLite fixture\"", StringComparison.Ordinal) < 0
                || readiness.IndexOf("\"viewport\":\"centered\"", StringComparison.Ordinal) < 0
                || readiness.IndexOf("\"renderMarker\":\"#dc1e1e\"", StringComparison.Ordinal) < 0)
            {
                throw new InvalidOperationException("The prototype did not report a rendered SQLite map layer.");
            }

            bool markerVisible = false;
            DateTime renderDeadline = DateTime.UtcNow.AddSeconds(Math.Min(options.TimeoutSeconds, 30));
            while (DateTime.UtcNow < renderDeadline && !markerVisible)
            {
                using Bitmap screenshot = Capture(bounds);
                markerVisible = RenderAssertions.ContainsRedMarker(screenshot);
                if (!markerVisible)
                    Thread.Sleep(100);
                else
                    screenshot.Save(screenshotPath, ImageFormat.Png);
            }
            if (!markerVisible)
                throw new InvalidOperationException("The Mapsui marker was not observed in the rendered screenshot.");

            prototype.CloseMainWindow();
            if (!prototype.WaitForExit(5000))
            {
                prototype.Kill();
                prototype.WaitForExit();
            }

            WriteResult(resultPath, options.Commit, true, true, true, true, true, true, true, true, true, true);
            File.WriteAllText(diagnosticsPath!, "Prototype GUI smoke test passed; result flushed before shutdown request.\r\n", Encoding.UTF8);
            Process.Start(new ProcessStartInfo("shutdown.exe", "/s /t 0 /f") { UseShellExecute = false });
            return 0;
        }
        catch (Exception exception)
        {
            if (diagnosticsPath is not null)
                File.WriteAllText(diagnosticsPath, exception.ToString(), Encoding.UTF8);
            if (options is not null)
            {
                string resultPath = Path.Combine(options.ResultsDirectory, "win7-prototype-result.json");
                WriteResult(resultPath, options.Commit, false, false, false, false, false, false, false, false, false, false);
            }
            return 1;
        }
        finally
        {
            prototype?.Dispose();
        }
    }

    private static void WriteResult(
        string path,
        string commit,
        bool processStarted,
        bool windowVisible,
        bool sqliteRead,
        bool mapLayer,
        bool nativeWindow,
        bool screenshot,
        bool guiAssertion,
        bool noException,
        bool resultFlushed,
        bool shutdownRequested)
    {
        string status = processStarted && windowVisible && sqliteRead && mapLayer && nativeWindow
            && screenshot && guiAssertion && noException && resultFlushed && shutdownRequested ? "PASS" : "FAIL";
        string json = "{\"schema\":\"" + Schema + "\",\"status\":\"" + status
            + "\",\"commit\":\"" + commit + "\",\"result_flushed\":" + JsonBool(resultFlushed)
            + ",\"shutdown_requested\":" + JsonBool(shutdownRequested)
            + ",\"checks\":{"
            + "\"P-01-process-started\":" + JsonBool(processStarted)
            + ",\"P-02-window-visible\":" + JsonBool(windowVisible)
            + ",\"P-03-sqlite-read\":" + JsonBool(sqliteRead)
            + ",\"P-04-mapsui-layer\":" + JsonBool(mapLayer)
            + ",\"P-05-native-window\":" + JsonBool(nativeWindow)
            + ",\"P-06-screenshot\":" + JsonBool(screenshot)
            + ",\"P-07-gui-point-assertion\":" + JsonBool(guiAssertion)
            + ",\"P-08-no-exception\":" + JsonBool(noException) + "}}\n";
        File.WriteAllText(path, json, Encoding.UTF8);
    }

    private static Bitmap Capture(Win32Rect bounds)
    {
        Bitmap screenshot = new(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
        using Graphics graphics = Graphics.FromImage(screenshot);
        graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, screenshot.Size);
        return screenshot;
    }

    private static string JsonBool(bool value) => value ? "true" : "false";

    private sealed class AgentOptions
    {
        internal string PrototypePath { get; private set; } = string.Empty;

        internal string ResultsDirectory { get; private set; } = string.Empty;

        internal string Commit { get; private set; } = string.Empty;

        internal int TimeoutSeconds { get; private set; } = 120;

        internal static AgentOptions Parse(string[] args)
        {
            AgentOptions options = new();
            int index = 0;
            while (index < args.Length)
            {
                string value = args[index];
                if (index + 1 >= args.Length)
                {
                    throw new ArgumentException("Every option requires a value.");
                }

                string argument = args[index + 1];
                if (value == "--prototype")
                    options.PrototypePath = argument;
                else if (value == "--results")
                    options.ResultsDirectory = argument;
                else if (value == "--commit")
                    options.Commit = argument;
                else if (value == "--timeout-seconds")
                    options.TimeoutSeconds = int.Parse(argument, CultureInfo.InvariantCulture);
                else
                    throw new ArgumentException("Unknown option: " + value);
                index += 2;
            }

            if (string.IsNullOrWhiteSpace(options.PrototypePath)
                || string.IsNullOrWhiteSpace(options.ResultsDirectory)
                || string.IsNullOrWhiteSpace(options.Commit)
                || options.TimeoutSeconds < 10 || options.TimeoutSeconds > 600)
            {
                throw new ArgumentException("Required: --prototype, --results, --commit and --timeout-seconds (10..600).");
            }

            return options;
        }
    }
}
