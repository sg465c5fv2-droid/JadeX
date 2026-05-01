using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;
using SpashAPIMadium;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using IOPath = System.IO.Path;

namespace JadeX
{
    public partial class MainWindow : Window
    {
        // ── Core fields ──
        private readonly List<EditorTab> _tabs = new();
        private int _activeTabIndex = -1;
        private int _tabCounter = 1;
        private bool _monacoReady = false;
        private bool _injected = false;

        private DateTime? _lastInjectionTime;
        private bool _lastInjectionConfirmed = false;
        private string? _lastDetectedModuleName;
        private int _moduleMissingCount = 0;

        private Process? _robloxProcess;
        private readonly DispatcherTimer _robloxMonitorTimer;

        private Dictionary<int, System.Timers.Timer> _processWatchers = new();

        private readonly SettingsWindow _settingsWindow = new();
        private bool _autoInjectEnabled = false;
        private bool _suppressStartupLogs = true;

        // ── Multi Instance fields ──
        private readonly List<InstanceInfo> _selectedInstances = new();
        private HashSet<int> _injectedPids = new();
        private readonly List<InstanceInfo> _activeInstances = new();

        // ── Panel toggle states ──
        private bool _scriptsPanelVisible = true;
        private bool _consolePanelVisible = true;
        private double _scriptsPanelSavedWidth = 168;
        private double _consolePanelSavedHeight = 140;

        private readonly string _monacoPath =
            IOPath.Combine(AppDomain.CurrentDomain.BaseDirectory, "Monaco", "index.html");
        private readonly string _scriptsFolder =
            IOPath.Combine(AppDomain.CurrentDomain.BaseDirectory, "Scripts");
        private readonly string _workspaceFolder =
            IOPath.Combine(AppDomain.CurrentDomain.BaseDirectory, "Workspace");

        // ── Safe API invoke helper ──
        // Wraps ALL SpashAPIMadium calls so a TypeLoadException / TypeInitializationException
        // never bubbles up and crashes the UI thread.
        private static T? SafeApiCall<T>(Func<T> fn, T? fallback = default)
        {
            try { return fn(); }
            catch (TypeInitializationException) { return fallback; }
            catch (TypeLoadException) { return fallback; }
            catch { return fallback; }
        }

        private static void SafeApiCall(Action fn)
        {
            try { fn(); }
            catch (TypeInitializationException) { }
            catch (TypeLoadException) { }
            catch { }
        }

        public MainWindow()
        {
            try
            {
                InitializeComponent();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Failed to initialize window UI.\n\n{ex.Message}\n\nInner: {ex.InnerException?.Message}",
                    "JadeX - Init Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }

            try
            {
                Directory.SetCurrentDirectory(AppDomain.CurrentDomain.BaseDirectory);
                _suppressStartupLogs = true;

                EnsureScriptsFolder();
                EnsureWorkspaceFolder();
                LoadScriptsList();

                _settingsWindow.SettingsApplied += ApplySettings;
                _settingsWindow.LogRequested += (msg) => Log(msg, "#888");

                UpdateToggleButtonState(ToggleScriptsBtn, true);
                UpdateToggleButtonState(ToggleConsoleBtn, true);

                InitWebView();

                _robloxMonitorTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                _robloxMonitorTimer.Tick += RobloxMonitorTimer_Tick;
                _robloxMonitorTimer.Start();

                Log("JadeX started.", "#444");
                _suppressStartupLogs = false;

                Loaded += MainWindow_Loaded;
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Startup error: {ex.Message}\n\nInner: {ex.InnerException?.Message}",
                    "JadeX", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                ApplySettings(_settingsWindow.Current);
            }
            catch { }

            // Load AutoInject setting via reflection — off-thread, guarded
            Task.Run(() =>
            {
                bool result = SafeApiCall(() =>
                {
                    var mi = typeof(SpashAPIMadium.API).GetMethod("GetAutoInject");
                    if (mi != null && mi.ReturnType == typeof(bool))
                    {
                        var res = mi.Invoke(null, null);
                        if (res is bool b) return b;
                    }
                    return false;
                }, false);

                Dispatcher.Invoke(() => _autoInjectEnabled = result);
            });
        }

        // ══════════════════════════════════════════════════════════════════
        //  PANEL TOGGLE — Scripts
        // ══════════════════════════════════════════════════════════════════

        private void ToggleScripts_Click(object sender, RoutedEventArgs e)
        {
            _scriptsPanelVisible = !_scriptsPanelVisible;
            UpdateToggleButtonState(ToggleScriptsBtn, _scriptsPanelVisible);

            var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };

            if (_scriptsPanelVisible)
            {
                ScriptsPanelBorder.Visibility = Visibility.Visible;
                ScriptsSplitter.Visibility = Visibility.Visible;

                var widthAnim = new GridLengthAnimation
                {
                    From = new GridLength(0),
                    To = new GridLength(_scriptsPanelSavedWidth),
                    Duration = new Duration(TimeSpan.FromMilliseconds(240)),
                    EasingFunction = ease
                };
                ScriptsColumn.BeginAnimation(ColumnDefinition.WidthProperty, widthAnim);

                var gapAnim = new GridLengthAnimation
                {
                    From = new GridLength(0),
                    To = new GridLength(8),
                    Duration = new Duration(TimeSpan.FromMilliseconds(240)),
                    EasingFunction = ease
                };
                ScriptsGapColumn.BeginAnimation(ColumnDefinition.WidthProperty, gapAnim);

                var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200));
                ScriptsPanelBorder.BeginAnimation(OpacityProperty, fadeIn);
            }
            else
            {
                _scriptsPanelSavedWidth = ScriptsColumn.ActualWidth > 0
                    ? ScriptsColumn.ActualWidth
                    : _scriptsPanelSavedWidth;

                var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(160));
                ScriptsPanelBorder.BeginAnimation(OpacityProperty, fadeOut);

                var widthAnim = new GridLengthAnimation
                {
                    From = new GridLength(ScriptsColumn.ActualWidth),
                    To = new GridLength(0),
                    Duration = new Duration(TimeSpan.FromMilliseconds(220)),
                    EasingFunction = ease
                };
                widthAnim.Completed += (_, _) =>
                {
                    ScriptsPanelBorder.Visibility = Visibility.Collapsed;
                    ScriptsSplitter.Visibility = Visibility.Collapsed;
                };
                ScriptsColumn.BeginAnimation(ColumnDefinition.WidthProperty, widthAnim);

                var gapAnim = new GridLengthAnimation
                {
                    From = new GridLength(8),
                    To = new GridLength(0),
                    Duration = new Duration(TimeSpan.FromMilliseconds(220)),
                    EasingFunction = ease
                };
                ScriptsGapColumn.BeginAnimation(ColumnDefinition.WidthProperty, gapAnim);
            }
        }

        // ══════════════════════════════════════════════════════════════════
        //  PANEL TOGGLE — Console
        // ══════════════════════════════════════════════════════════════════

        private void ToggleConsole_Click(object sender, RoutedEventArgs e)
        {
            _consolePanelVisible = !_consolePanelVisible;
            UpdateToggleButtonState(ToggleConsoleBtn, _consolePanelVisible);

            var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };

            if (_consolePanelVisible)
            {
                ConsolePanelBorder.Visibility = Visibility.Visible;
                ConsoleSplitter.Visibility = Visibility.Visible;

                var heightAnim = new GridLengthAnimation
                {
                    From = new GridLength(0),
                    To = new GridLength(_consolePanelSavedHeight),
                    Duration = new Duration(TimeSpan.FromMilliseconds(240)),
                    EasingFunction = ease
                };
                ConsoleRow.BeginAnimation(RowDefinition.HeightProperty, heightAnim);

                var splitterAnim = new GridLengthAnimation
                {
                    From = new GridLength(0),
                    To = new GridLength(8),
                    Duration = new Duration(TimeSpan.FromMilliseconds(240)),
                    EasingFunction = ease
                };
                ConsoleSplitterRow.BeginAnimation(RowDefinition.HeightProperty, splitterAnim);

                var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200));
                ConsolePanelBorder.BeginAnimation(OpacityProperty, fadeIn);
            }
            else
            {
                _consolePanelSavedHeight = ConsoleRow.ActualHeight > 0
                    ? ConsoleRow.ActualHeight
                    : _consolePanelSavedHeight;

                var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(160));
                ConsolePanelBorder.BeginAnimation(OpacityProperty, fadeOut);

                var heightAnim = new GridLengthAnimation
                {
                    From = new GridLength(ConsoleRow.ActualHeight),
                    To = new GridLength(0),
                    Duration = new Duration(TimeSpan.FromMilliseconds(220)),
                    EasingFunction = ease
                };
                heightAnim.Completed += (_, _) =>
                {
                    ConsolePanelBorder.Visibility = Visibility.Collapsed;
                    ConsoleSplitter.Visibility = Visibility.Collapsed;
                };
                ConsoleRow.BeginAnimation(RowDefinition.HeightProperty, heightAnim);

                var splitterAnim = new GridLengthAnimation
                {
                    From = new GridLength(8),
                    To = new GridLength(0),
                    Duration = new Duration(TimeSpan.FromMilliseconds(220)),
                    EasingFunction = ease
                };
                ConsoleSplitterRow.BeginAnimation(RowDefinition.HeightProperty, splitterAnim);
            }
        }

        private void UpdateToggleButtonState(Button btn, bool active)
        {
            if (btn == null) return;

            if (btn == ToggleScriptsBtn)
                btn.Content = active ? "■ Scripts" : "□ Scripts";
            else if (btn == ToggleConsoleBtn)
                btn.Content = active ? "■ Console" : "□ Console";

            btn.Foreground = active
                ? new SolidColorBrush(Color.FromRgb(0x5c, 0xdb, 0x7f))
                : new SolidColorBrush(Color.FromRgb(0x40, 0x42, 0x4a));
        }

        // ══════════════════════════════════════════════════════════════════
        //  ROBLOX MONITOR
        // ══════════════════════════════════════════════════════════════════

        private Process? FindRobloxProcess()
        {
            try
            {
                foreach (var p in Process.GetProcesses())
                {
                    try
                    {
                        string name = p.ProcessName ?? string.Empty;
                        if (name.Equals("RobloxPlayerBeta", StringComparison.OrdinalIgnoreCase))
                            return p;
                    }
                    catch { }
                }
            }
            catch { }
            return null;
        }

        private void EnsureWorkspaceFolder()
        {
            try
            {
                if (!Directory.Exists(_workspaceFolder))
                    Directory.CreateDirectory(_workspaceFolder);
            }
            catch { }

            try
            {
                string madiumWorkspace = IOPath.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Madium", "Workspace");

                string madiumBase = IOPath.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Madium");

                if (!Directory.Exists(madiumBase))
                    Directory.CreateDirectory(madiumBase);

                if (!Directory.Exists(madiumWorkspace))
                {
                    var psi = new ProcessStartInfo("cmd.exe",
                        $"/c mklink /J \"{madiumWorkspace}\" \"{_workspaceFolder}\"")
                    {
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                    };
                    var proc = Process.Start(psi);
                    proc?.WaitForExit(3000);
                    Log($"Workspace junction created: {madiumWorkspace} → {_workspaceFolder}", "#6ddb6d");
                }
                else
                {
                    Log($"Madium workspace exists at: {madiumWorkspace}", "#888");
                }
            }
            catch (Exception ex)
            {
                Log($"[WARN] Junction failed: {ex.Message}", "#e0c060");
            }
        }

        private async void RobloxMonitorTimer_Tick(object? sender, EventArgs e)
        {
            try
            {
                var exitedPids = new List<int>();
                foreach (var pid in _injectedPids.ToList())
                {
                    try
                    {
                        var check = Process.GetProcessById(pid);
                        if (check.HasExited) exitedPids.Add(pid);
                        check.Dispose();
                    }
                    catch (ArgumentException) { exitedPids.Add(pid); }
                    catch { }
                }

                foreach (var pid in exitedPids)
                    ClearInstanceState(pid);

                var allRoblox = Process.GetProcesses()
                    .Where(p => p.ProcessName.Equals("RobloxPlayerBeta", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (allRoblox.Count == 0)
                {
                    if (_injected)
                    {
                        foreach (var pid in _injectedPids.ToList())
                            ClearInstanceState(pid);
                    }
                    return;
                }

                if (_injected && _injectedPids.Count > 0)
                {
                    foreach (var roblox in allRoblox)
                    {
                        if (!_injectedPids.Contains(roblox.Id)) continue;

                        try
                        {
                            int probe = ProbeInjectorModule(roblox);

                            if (probe == 1)
                            {
                                _moduleMissingCount = 0;
                                _lastInjectionConfirmed = true;
                                _ = Dispatcher.BeginInvoke(new Action(() =>
                                {
                                    SetStatus(true, !string.IsNullOrEmpty(_lastDetectedModuleName)
                                        ? _lastDetectedModuleName
                                        : $"{_injectedPids.Count} instance(s) active");
                                }));
                            }
                            else if (probe == -1)
                            {
                                _ = Dispatcher.BeginInvoke(new Action(() =>
                                    SetStatus(true, "Module detection unavailable")));
                            }
                            else
                            {
                                bool recentlyInjected = false;
                                try
                                {
                                    recentlyInjected = _lastInjectionTime.HasValue &&
                                        (DateTime.Now - _lastInjectionTime.Value) < TimeSpan.FromSeconds(10);
                                }
                                catch { }

                                if (recentlyInjected && !_lastInjectionConfirmed)
                                {
                                    _moduleMissingCount++;
                                    if (_moduleMissingCount < 3)
                                        _ = Dispatcher.BeginInvoke(new Action(() => SetStatus(true, "Awaiting module...")));
                                    else
                                        ClearInstanceState(roblox.Id);
                                }
                                else
                                {
                                    _ = Dispatcher.BeginInvoke(new Action(() => SetStatus(false, "Injector module missing")));
                                }
                            }
                        }
                        catch { }
                    }
                }

                try
                {
                    if (!_injected && _autoInjectEnabled)
                    {
                        foreach (var roblox in allRoblox)
                        {
                            if (!_injectedPids.Contains(roblox.Id))
                                await TryInjectAsync(roblox, showMessages: false);
                        }
                    }
                }
                catch { }
            }
            catch { }
        }

        private int ProbeInjectorModule(Process proc)
        {
            if (proc == null) return 0;
            try
            {
                var refreshed = Process.GetProcessById(proc.Id);
                foreach (ProcessModule mod in refreshed.Modules)
                {
                    string mn = mod.ModuleName ?? string.Empty;
                    if (mn.IndexOf("Spas", StringComparison.OrdinalIgnoreCase) >= 0
                        || mn.IndexOf("Sapi", StringComparison.OrdinalIgnoreCase) >= 0)
                        return 1;
                }
                return 0;
            }
            catch { return -1; }
        }

        private async Task<bool> TryInjectAsync(Process roblox, bool showMessages)
        {
            if (roblox == null) return false;

            try
            {
                bool injectCallSucceeded = false;

                // FIX: Wrap the entire API call in SafeApiCall to catch TypeLoadException
                await Task.Run(() =>
                {
                    SafeApiCall(() =>
                    {
                        var apiType = typeof(SpashAPIMadium.API);
                        var mi = apiType.GetMethod("AttachAPI", Type.EmptyTypes);
                        if (mi != null)
                        {
                            mi.Invoke(null, null);
                            injectCallSucceeded = true;
                        }
                        else
                        {
                            var mi2 = apiType.GetMethod("AttachAPI", new Type[] { typeof(string) });
                            if (mi2 != null)
                            {
                                mi2.Invoke(null, new object[] { string.Empty });
                                injectCallSucceeded = true;
                            }
                        }
                    });
                });

                bool injectionSucceeded = false;
                const int attempts = 6;
                string? detectedModuleName = null;

                for (int attempt = 0; attempt < attempts && !injectionSucceeded; attempt++)
                {
                    await Task.Delay(300);
                    try
                    {
                        var refreshed = Process.GetProcessById(roblox.Id);
                        try
                        {
                            foreach (ProcessModule mod in refreshed.Modules)
                            {
                                string mn = mod.ModuleName ?? string.Empty;
                                if (mn.IndexOf("SapiX", StringComparison.OrdinalIgnoreCase) >= 0
                                    || mn.IndexOf("Sapi", StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    injectionSucceeded = true;
                                    detectedModuleName = mn;
                                    break;
                                }
                            }
                        }
                        catch { }
                    }
                    catch { }
                }

                if (injectionSucceeded && !string.IsNullOrEmpty(detectedModuleName) && showMessages)
                    Log($"Injector module detected: {detectedModuleName}", "#6ddb6d");

                if (!injectionSucceeded)
                {
                    bool apiResult = await Task.Run(() =>
                    {
                        return SafeApiCall(() =>
                        {
                            var mi = typeof(SpashAPIMadium.API).GetMethod("AttachAPI");
                            if (mi != null && mi.ReturnType == typeof(bool))
                            {
                                var res = mi.Invoke(null, null);
                                return res is bool b && b;
                            }
                            return false;
                        }, false);
                    });

                    if (apiResult) injectionSucceeded = true;
                }

                if (!injectionSucceeded && injectCallSucceeded)
                {
                    detectedModuleName = "Injected (assumed)";
                    injectionSucceeded = true;
                    if (showMessages) Log("Injector call completed but module not detected — assuming injected.", "#e0c060");
                }

                if (injectionSucceeded)
                {
                    try
                    {
                        var refreshed = Process.GetProcessById(roblox.Id);
                        if (refreshed.HasExited) injectionSucceeded = false;
                    }
                    catch { injectionSucceeded = false; }
                }

                if (injectionSucceeded)
                {
                    _lastInjectionTime = DateTime.Now;
                    _lastDetectedModuleName = detectedModuleName;
                    _lastInjectionConfirmed = !string.IsNullOrEmpty(detectedModuleName);
                    _moduleMissingCount = 0;
                    SetStatus(true, detectedModuleName ?? "Injected");
                    _robloxProcess = roblox;

                    try
                    {
                        if (!_robloxProcess.HasExited)
                        {
                            _robloxProcess.EnableRaisingEvents = true;
                            _robloxProcess.Exited -= Roblox_Exited;
                            _robloxProcess.Exited += Roblox_Exited;
                        }
                    }
                    catch { }

                    if (showMessages) Log("Injected successfully.", "#6ddb6d");
                    return true;
                }
                else
                {
                    SetStatus(false, "Injection failed");
                    if (showMessages)
                    {
                        Log("Injection attempted but no injector module detected in Roblox process.", "#cc3333");
                        MessageBox.Show(
                            "Injection failed or injector not detected in the Roblox process.",
                            "JadeX", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
            }
            catch (Exception ex)
            {
                if (showMessages) Log($"[ERROR] Injection failed: {ex.Message}", "#cc3333");
            }

            return false;
        }

        // ══════════════════════════════════════════════════════════════════
        //  WEBVIEW2
        // ══════════════════════════════════════════════════════════════════

        private async void InitWebView()
        {
            try
            {
                string userDataDir = IOPath.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "JadeX", "WebView2");

                Directory.CreateDirectory(userDataDir);

                Log($"Base dir: {AppDomain.CurrentDomain.BaseDirectory}", "#888");
                Log($"Monaco path: {_monacoPath}", "#888");
                Log($"Monaco file exists: {File.Exists(_monacoPath)}", "#888");

                try
                {
                    string? avail = CoreWebView2Environment.GetAvailableBrowserVersionString(null);
                    Log($"WebView2 runtime: {avail ?? "(none — NOT INSTALLED)"}", "#888");
                    Log($"Process is 64-bit: {Environment.Is64BitProcess}", "#888");
                }
                catch (Exception ex)
                {
                    Log($"WebView2 version probe failed: {ex.Message}", "#cc3333");
                }

                string monacoFolder = IOPath.Combine(AppDomain.CurrentDomain.BaseDirectory, "Monaco");

                if (!Directory.Exists(monacoFolder) || !File.Exists(IOPath.Combine(monacoFolder, "index.html")))
                {
                    Log($"[ERROR] Monaco folder or index.html not found: {monacoFolder}", "#cc3333");
                    MessageBox.Show(
                        "Monaco editor files not found. The editor will be disabled.\n\nPlease add the 'Monaco' folder (with index.html) to the project and set CopyToOutputDirectory=PreserveNewest.",
                        "JadeX - Missing Monaco", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var env = await CoreWebView2Environment.CreateAsync(null, userDataDir);
                await MonacoEditor.EnsureCoreWebView2Async(env);

                MonacoEditor.CoreWebView2.Settings.IsWebMessageEnabled = true;
                MonacoEditor.CoreWebView2.Settings.AreDefaultScriptDialogsEnabled = false;
                MonacoEditor.CoreWebView2.Settings.IsStatusBarEnabled = false;
                MonacoEditor.CoreWebView2.Settings.AreDevToolsEnabled = false;

                MonacoEditor.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;

                MonacoEditor.CoreWebView2.SetVirtualHostNameToFolderMapping(
                    "monaco.local", monacoFolder,
                    CoreWebView2HostResourceAccessKind.Allow);

                MonacoEditor.CoreWebView2.Navigate("https://monaco.local/index.html");
            }
            catch (Exception ex)
            {
                Log($"[ERROR] WebView2: {ex.Message}", "#cc3333");
                Log($"[ERROR] Stack: {ex.StackTrace}", "#cc3333");
                MessageBox.Show(
                    $"WebView2 could not start.\n\n{ex.Message}",
                    "JadeX", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void MonacoEditor_NavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            if (!e.IsSuccess)
            {
                Log("[ERROR] Monaco navigation failed.", "#cc3333");
                return;
            }
            if (!_monacoReady)
            {
                _monacoReady = true;
                Log("Monaco ready (nav fallback).", "#444");
                if (_tabs.Count == 0) Dispatcher.Invoke(() => AddNewTab());
            }
        }

        private void CoreWebView2_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            string msg = e.TryGetWebMessageAsString();

            if (msg == "monaco_ready")
            {
                _monacoReady = true;
                Dispatcher.Invoke(async () =>
                {
                    Log("Monaco ready.", "#444");
                    if (_tabs.Count == 0)
                    {
                        await AddNewTab();
                    }
                    else
                    {
                        if (_activeTabIndex >= 0 && _activeTabIndex < _tabs.Count)
                        {
                            string esc = System.Text.Json.JsonSerializer.Serialize(_tabs[_activeTabIndex].Content);
                            try { await MonacoEditor.CoreWebView2.ExecuteScriptAsync($"SetText({esc})"); }
                            catch { }
                        }
                    }

                    try { _settingsWindow.ApplyOnStartup(); } catch { }
                });
            }
        }

        // ══════════════════════════════════════════════════════════════════
        //  CONSOLE
        // ══════════════════════════════════════════════════════════════════

        private void Log(string message, string hexColor = "#aaaaaa")
        {
            try
            {
                if (_suppressStartupLogs && !(message?.Contains("JadeX started") == true))
                    return;
            }
            catch { }

            Dispatcher.Invoke(() =>
            {
                try
                {
                    string time = DateTime.Now.ToString("HH:mm:ss");
                    var run = new System.Windows.Documents.Run($"[{time}]  {message}\n")
                    {
                        Foreground = new SolidColorBrush(
                            (Color)ColorConverter.ConvertFromString(hexColor))
                    };
                    ConsoleOutput.Inlines.Add(run);
                    ConsoleScroll.ScrollToBottom();
                }
                catch { }
            });
        }

        private void ClearConsole_Click(object sender, RoutedEventArgs e)
            => ConsoleOutput.Inlines.Clear();

        // ══════════════════════════════════════════════════════════════════
        //  STATUS
        // ══════════════════════════════════════════════════════════════════

        private void SetStatus(bool injected, string detail = "")
        {
            _injected = injected;

            try
            {
                var dotColor = injected ? Color.FromRgb(0x6d, 0xdb, 0x6d) : Color.FromRgb(0xcc, 0x33, 0x33);
                var textColor = injected ? Color.FromRgb(0x6d, 0xdb, 0x6d) : Color.FromRgb(0x55, 0x55, 0x55);

                var ca = new ColorAnimation(dotColor, TimeSpan.FromMilliseconds(300));
                StatusDotBrush.BeginAnimation(SolidColorBrush.ColorProperty, ca);

                var glowColor = new ColorAnimation(dotColor, TimeSpan.FromMilliseconds(300));
                StatusGlow.BeginAnimation(DropShadowEffect.ColorProperty, glowColor);

                StatusText.Text = injected ? "Injected" : "Not Injected";
                StatusText.Foreground = new SolidColorBrush(textColor);

                if (!string.IsNullOrEmpty(detail))
                    StatusDetail.Text = detail;

                if (injected)
                {
                    var pulse = new DoubleAnimation(0.0, 0.9, TimeSpan.FromMilliseconds(800))
                    { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
                    StatusGlow.BeginAnimation(DropShadowEffect.OpacityProperty, pulse);

                    var blur = new DoubleAnimation(6, 14, TimeSpan.FromMilliseconds(800))
                    { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
                    StatusGlow.BeginAnimation(DropShadowEffect.BlurRadiusProperty, blur);
                }
                else
                {
                    StatusGlow.BeginAnimation(DropShadowEffect.OpacityProperty,
                        new DoubleAnimation(0, TimeSpan.FromMilliseconds(300)));
                    StatusGlow.BeginAnimation(DropShadowEffect.BlurRadiusProperty,
                        new DoubleAnimation(0, TimeSpan.FromMilliseconds(300)));
                }
            }
            catch { }
        }

        // ══════════════════════════════════════════════════════════════════
        //  TABS
        // ══════════════════════════════════════════════════════════════════

        private async Task AddNewTab(string? title = null, string content = "")
        {
            int index = _tabs.Count;
            string tabTitle = title ?? $"Script {_tabCounter++}";
            _tabs.Add(new EditorTab { Title = tabTitle, Content = content });

            var header = BuildTabHeader(index, tabTitle);
            TabsPanel.Children.Add(header);

            header.RenderTransform = new TranslateTransform(16, 0);
            header.Opacity = 0;

            var slideIn = new DoubleAnimation(0, TimeSpan.FromMilliseconds(160))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            var fadeIn = new DoubleAnimation(1, TimeSpan.FromMilliseconds(160));
            header.RenderTransform.BeginAnimation(TranslateTransform.XProperty, slideIn);
            header.BeginAnimation(OpacityProperty, fadeIn);

            await ActivateTab(index);
        }

        private Border BuildTabHeader(int index, string title)
        {
            var root = new Border
            {
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Margin = new Thickness(1, 0, 1, 0),
                Padding = new Thickness(0),
                MinWidth = 90,
                MaxWidth = 200,
                Height = 34,
                Cursor = Cursors.Hand,
                ClipToBounds = false,
                SnapsToDevicePixels = true,
            };

            var inner = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0x00, 0x18, 0x18, 0x18)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x00, 0x38, 0x38, 0x38)),
                BorderThickness = new Thickness(1, 1, 1, 0),
                CornerRadius = new CornerRadius(5, 5, 0, 0),
                Padding = new Thickness(0),
                SnapsToDevicePixels = true,
            };

            var accent = new Border
            {
                Height = 2,
                Background = new LinearGradientBrush(
                    Color.FromRgb(0x4e, 0xc9, 0x4e),
                    Color.FromRgb(0x2a, 0x9d, 0x2a),
                    0),
                VerticalAlignment = VerticalAlignment.Bottom,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Opacity = 0,
                CornerRadius = new CornerRadius(1, 1, 0, 0),
                Effect = new DropShadowEffect
                {
                    Color = Color.FromRgb(0x4e, 0xc9, 0x4e),
                    BlurRadius = 10,
                    ShadowDepth = 0,
                    Opacity = 0.7,
                },
            };

            var dot = new Ellipse
            {
                Width = 4,
                Height = 4,
                Fill = new SolidColorBrush(Color.FromRgb(0x4e, 0xc9, 0x4e)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 0, 0),
                Opacity = 0,
                SnapsToDevicePixels = true,
            };

            var lbl = new TextBlock
            {
                Text = title,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 11.5,
                Foreground = new SolidColorBrush(Color.FromRgb(0x60, 0x60, 0x60)),
                VerticalAlignment = VerticalAlignment.Center,
                Padding = new Thickness(7, 0, 5, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 130,
            };
            TextOptions.SetTextFormattingMode(lbl, System.Windows.Media.TextFormattingMode.Display);

            var box = new TextBox
            {
                Visibility = Visibility.Collapsed,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 11.5,
                Foreground = Brushes.White,
                Background = Brushes.Transparent,
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x4e, 0xc9, 0x4e)),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(2, 0, 2, 0),
                MinWidth = 50,
                MaxWidth = 130,
                Height = 18,
                VerticalAlignment = VerticalAlignment.Center,
                CaretBrush = new SolidColorBrush(Color.FromRgb(0x4e, 0xc9, 0x4e)),
                Margin = new Thickness(7, 0, 5, 0),
            };

            void CommitRename()
            {
                string n = box.Text.Trim();
                if (string.IsNullOrEmpty(n)) n = _tabs[index].Title;
                _tabs[index].Title = n;
                lbl.Text = n;
                lbl.Visibility = Visibility.Visible;
                box.Visibility = Visibility.Collapsed;
            }

            box.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter) { CommitRename(); e.Handled = true; }
                if (e.Key == Key.Escape) { box.Text = _tabs[index].Title; CommitRename(); e.Handled = true; }
            };
            box.LostFocus += (_, _) => CommitRename();

            void StartRename()
            {
                box.Text = _tabs[index].Title;
                lbl.Visibility = Visibility.Collapsed;
                box.Visibility = Visibility.Visible;
                box.SelectAll();
                box.Focus();
            }

            var closeBtn = new Button
            {
                Content = "×",
                FontFamily = new FontFamily("Segoe UI Symbol"),
                FontSize = 13,
                FontWeight = FontWeights.Light,
                Foreground = new SolidColorBrush(Color.FromRgb(0x50, 0x50, 0x50)),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Width = 17,
                Height = 17,
                Padding = new Thickness(0),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 5, 0),
                Opacity = 0,
                Cursor = Cursors.Hand,
                Tag = index,
                Focusable = false,
            };

            closeBtn.MouseEnter += (_, _) =>
            {
                ((SolidColorBrush)closeBtn.Foreground).BeginAnimation(
                    SolidColorBrush.ColorProperty,
                    new ColorAnimation(Color.FromRgb(0xff, 0x60, 0x60), TimeSpan.FromMilliseconds(80)));
            };
            closeBtn.MouseLeave += (_, _) =>
            {
                ((SolidColorBrush)closeBtn.Foreground).BeginAnimation(
                    SolidColorBrush.ColorProperty,
                    new ColorAnimation(Color.FromRgb(0x50, 0x50, 0x50), TimeSpan.FromMilliseconds(120)));
            };
            closeBtn.Click += (s, ev) =>
            {
                ev.Handled = true;
                if (root.Tag is TabHeaderRefs r) RemoveTab(r.Index);
            };

            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
            };
            row.Children.Add(dot);
            row.Children.Add(lbl);
            row.Children.Add(box);
            row.Children.Add(closeBtn);

            var grid = new Grid();
            grid.Children.Add(row);
            grid.Children.Add(accent);

            inner.Child = grid;
            root.Child = inner;

            var refs = new TabHeaderRefs { Index = index, Label = lbl, Accent = accent, Dot = dot, Inner = inner };
            root.Tag = refs;

            root.MouseEnter += (_, _) =>
            {
                if (root.Tag is TabHeaderRefs r && r.Index != _activeTabIndex)
                {
                    ((SolidColorBrush)inner.Background).BeginAnimation(SolidColorBrush.ColorProperty,
                        new ColorAnimation(Color.FromArgb(0x20, 0x25, 0x25, 0x25), TimeSpan.FromMilliseconds(100)));
                    ((SolidColorBrush)inner.BorderBrush).BeginAnimation(SolidColorBrush.ColorProperty,
                        new ColorAnimation(Color.FromArgb(0x40, 0x4e, 0xc9, 0x4e), TimeSpan.FromMilliseconds(100)));
                }
                closeBtn.BeginAnimation(OpacityProperty, new DoubleAnimation(0.65, TimeSpan.FromMilliseconds(100)));
            };
            root.MouseLeave += (_, _) =>
            {
                if (root.Tag is TabHeaderRefs r && r.Index != _activeTabIndex)
                {
                    ((SolidColorBrush)inner.Background).BeginAnimation(SolidColorBrush.ColorProperty,
                        new ColorAnimation(Color.FromArgb(0x00, 0x18, 0x18, 0x18), TimeSpan.FromMilliseconds(160)));
                    ((SolidColorBrush)inner.BorderBrush).BeginAnimation(SolidColorBrush.ColorProperty,
                        new ColorAnimation(Color.FromArgb(0x00, 0x38, 0x38, 0x38), TimeSpan.FromMilliseconds(160)));
                }
                closeBtn.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(140)));
            };

            root.MouseDown += async (s, e) =>
            {
                if (e.ChangedButton == MouseButton.Left)
                {
                    if (e.ClickCount == 2)
                    {
                        StartRename();
                        e.Handled = true;
                    }
                    else if (e.ClickCount == 1)
                    {
                        if (root.Tag is TabHeaderRefs r) await ActivateTab(r.Index);
                        e.Handled = true;
                    }
                }
            };

            return root;
        }

        private async Task ActivateTab(int index)
        {
            if (index < 0 || index >= _tabs.Count) return;

            if (_monacoReady && _activeTabIndex >= 0 && _activeTabIndex < _tabs.Count
                && MonacoEditor?.CoreWebView2 != null)
            {
                try
                {
                    var cur = await MonacoEditor.CoreWebView2.ExecuteScriptAsync("GetText()");
                    _tabs[_activeTabIndex].Content =
                        System.Text.Json.JsonSerializer.Deserialize<string>(cur) ?? "";
                }
                catch { }
            }

            _activeTabIndex = index;

            if (_monacoReady && MonacoEditor?.CoreWebView2 != null)
            {
                try
                {
                    string esc = System.Text.Json.JsonSerializer.Serialize(_tabs[index].Content);
                    await MonacoEditor.CoreWebView2.ExecuteScriptAsync($"SetText({esc})");
                }
                catch { }
            }

            for (int i = 0; i < TabsPanel.Children.Count; i++)
            {
                if (TabsPanel.Children[i] is not Border root) continue;
                if (root.Tag is not TabHeaderRefs refs) continue;

                bool active = refs.Index == index;

                ((SolidColorBrush)refs.Inner.Background).BeginAnimation(SolidColorBrush.ColorProperty,
                    new ColorAnimation(
                        active ? Color.FromArgb(0xff, 0x0c, 0x0c, 0x0c) : Color.FromArgb(0x00, 0x18, 0x18, 0x18),
                        TimeSpan.FromMilliseconds(160)));

                ((SolidColorBrush)refs.Inner.BorderBrush).BeginAnimation(SolidColorBrush.ColorProperty,
                    new ColorAnimation(
                        active ? Color.FromArgb(0xff, 0x28, 0x28, 0x28) : Color.FromArgb(0x00, 0x38, 0x38, 0x38),
                        TimeSpan.FromMilliseconds(160)));

                ((SolidColorBrush)refs.Label.Foreground).BeginAnimation(SolidColorBrush.ColorProperty,
                    new ColorAnimation(
                        active ? Color.FromRgb(0xe2, 0xe2, 0xe2) : Color.FromRgb(0x60, 0x60, 0x60),
                        TimeSpan.FromMilliseconds(160)));

                refs.Accent.BeginAnimation(OpacityProperty,
                    new DoubleAnimation(active ? 1.0 : 0.0, TimeSpan.FromMilliseconds(180)));

                refs.Dot.BeginAnimation(OpacityProperty,
                    new DoubleAnimation(active ? 1.0 : 0.0, TimeSpan.FromMilliseconds(180)));

                root.BeginAnimation(MarginProperty,
                    new ThicknessAnimation(
                        active ? new Thickness(1, 0, 1, 0) : new Thickness(1, 3, 1, 0),
                        TimeSpan.FromMilliseconds(160))
                    { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            }
        }

        private void RemoveTab(int idx)
        {
            if (_tabs.Count <= 1) return;
            if (idx < 0 || idx >= _tabs.Count) return;

            var header = TabsPanel.Children[idx];
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(130));
            fade.Completed += async (_, _) =>
            {
                _tabs.RemoveAt(idx);
                TabsPanel.Children.RemoveAt(idx);

                for (int i = 0; i < TabsPanel.Children.Count; i++)
                {
                    if (TabsPanel.Children[i] is Border brd && brd.Tag is TabHeaderRefs refs)
                    {
                        refs.Index = i;
                        brd.Tag = refs;
                    }
                }

                _activeTabIndex = -1;
                await ActivateTab(Math.Min(idx, _tabs.Count - 1));
            };
            header.BeginAnimation(OpacityProperty, fade);
        }

        private async void AddTab_Click(object sender, RoutedEventArgs e) => await AddNewTab();

        private void EnsureScriptsFolder()
        {
            try
            {
                if (!Directory.Exists(_scriptsFolder))
                    Directory.CreateDirectory(_scriptsFolder);
            }
            catch { }
        }

        private void LoadScriptsList()
        {
            try
            {
                ScriptsList.Items.Clear();
                if (!Directory.Exists(_scriptsFolder)) return;

                var files = Directory.GetFiles(_scriptsFolder, "*.lua")
                            .Concat(Directory.GetFiles(_scriptsFolder, "*.txt"))
                            .OrderBy(f => f);

                foreach (var file in files)
                    ScriptsList.Items.Add(new ScriptItem { FileName = IOPath.GetFileName(file), FullPath = file });
            }
            catch { }
        }

        private void RefreshScripts_Click(object sender, RoutedEventArgs e)
        {
            LoadScriptsList();
            Log("Scripts refreshed.", "#444");
        }

        private async void ScriptsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ScriptsList.SelectedItem is not ScriptItem item) return;
            try
            {
                string text = await File.ReadAllTextAsync(item.FullPath);
                await AddNewTab(item.FileName, text);
                Log($"Opened: {item.FileName}", "#6d9ddb");
            }
            catch (Exception ex) { Log($"[ERROR] {ex.Message}", "#cc3333"); }
            finally { ScriptsList.SelectedItem = null; }
        }

        // ══════════════════════════════════════════════════════════════════
        //  BOTTOM BUTTONS
        // ══════════════════════════════════════════════════════════════════

        private async void InjectBtn_Click(object? sender, RoutedEventArgs? e)
        {
            try
            {
                var robloxProcesses = Process.GetProcessesByName("RobloxPlayerBeta");
                if (robloxProcesses.Length == 0)
                {
                    Log("Roblox not found.", "#cc3333");
                    return;
                }

                string loaderPath = IOPath.Combine(AppDomain.CurrentDomain.BaseDirectory, "Loader.exe");
                if (!File.Exists(loaderPath))
                {
                    Log("Loader.exe not found — falling back to API injection...", "#e0c060");
                    foreach (var roblox in robloxProcesses)
                    {
                        try
                        {
                            bool injected = await TryInjectAsync(roblox, showMessages: true);
                            if (injected) _injectedPids.Add(roblox.Id);
                        }
                        catch (Exception ex)
                        {
                            Log($"API inject failed for PID {roblox.Id}: {ex.Message}", "#cc3333");
                        }
                    }

                    if (_injectedPids.Count > 0)
                    {
                        _injected = true;
                        _lastInjectionTime = DateTime.Now;
                        _lastInjectionConfirmed = true;
                        SetStatus(true, $"{_injectedPids.Count} instance(s) injected via API");
                    }
                    else
                    {
                        SetStatus(false, "Injection failed");
                        Log("All API injection attempts failed.", "#cc3333");
                    }
                    return;
                }

                Log("Starting Loader...", "#e0c060");
                Process.Start(new ProcessStartInfo
                {
                    FileName = loaderPath,
                    UseShellExecute = true,
                    Verb = "runas"
                });

                // Loader'ın inject etmesi için bekle
                Log("Waiting for injection...", "#e0c060");
                await Task.Delay(5000);

                // Verify
                int verifiedCount = 0;
                foreach (var roblox in robloxProcesses)
                {
                    try
                    {
                        var proc = Process.GetProcessById(roblox.Id);
                        foreach (ProcessModule mod in proc.Modules)
                        {
                            string name = mod.ModuleName ?? "";
                            if (name.IndexOf("Sapi", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                name.IndexOf("Spas", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                name.IndexOf("Madium", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                verifiedCount++;
                                _injectedPids.Add(roblox.Id);
                                Log($"Verified PID {roblox.Id}", "#6ddb6d");

                                roblox.EnableRaisingEvents = true;
                                roblox.Exited -= Roblox_Exited;
                                roblox.Exited += Roblox_Exited;
                                StartProcessWatcher(roblox);
                                break;
                            }
                        }
                    }
                    catch { }
                }

                if (verifiedCount > 0)
                {
                    _injected = true;
                    _lastInjectionTime = DateTime.Now;
                    _lastInjectionConfirmed = true;
                    SetStatus(true, $"Injected {verifiedCount} instance(s)");
                    Log($"Injection complete: {verifiedCount} verified.", "#6ddb6d");
                }
                else
                {
                    // Loader çalıştı ama verify edemedik, yine de injected say
                    _injected = true;
                    _lastInjectionTime = DateTime.Now;
                    _lastInjectionConfirmed = true;
                    _injectedPids.UnionWith(robloxProcesses.Select(p => p.Id));
                    SetStatus(true, "Injected (via Loader)");
                    Log("Loader ran successfully. Assuming injected.", "#e0c060");

                    foreach (var roblox in robloxProcesses)
                    {
                        try
                        {
                            roblox.EnableRaisingEvents = true;
                            roblox.Exited -= Roblox_Exited;
                            roblox.Exited += Roblox_Exited;
                            StartProcessWatcher(roblox);
                        }
                        catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"Injection error: {ex.Message}", "#cc3333");
            }
        }

        //-------------------------------------------------------------------------
        private void StartProcessWatcher(Process roblox)
        {
            int pid = roblox.Id;

            if (_processWatchers.ContainsKey(pid))
            {
                try { _processWatchers[pid].Stop(); _processWatchers[pid].Dispose(); } catch { }
                _processWatchers.Remove(pid);
            }

            var timer = new System.Timers.Timer(1500);
            timer.Elapsed += (s, e) =>
            {
                try
                {
                    bool stillRunning;
                    try
                    {
                        var check = Process.GetProcessById(pid);
                        stillRunning = !check.HasExited;
                        check.Dispose();
                    }
                    catch { stillRunning = false; }

                    if (!stillRunning)
                        ClearInstanceState(pid);
                }
                catch
                {
                    ClearInstanceState(pid);
                }
            };

            timer.AutoReset = true;
            timer.Start();
            _processWatchers[pid] = timer;
        }

        private void StopProcessWatcher(int pid = -1)
        {
            if (pid == -1)
            {
                foreach (var kv in _processWatchers)
                {
                    try { kv.Value.Stop(); kv.Value.Dispose(); } catch { }
                }
                _processWatchers.Clear();
            }
            else
            {
                if (_processWatchers.TryGetValue(pid, out var timer))
                {
                    try { timer.Stop(); timer.Dispose(); } catch { }
                    _processWatchers.Remove(pid);
                }
            }
        }

        private void ClearInstanceState(int pid)
        {
            StopProcessWatcher(pid);
            _injectedPids.Remove(pid);

            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    Log($"PID {pid} exited.", "#888888");

                    if (_injectedPids.Count == 0)
                    {
                        _injected = false;
                        _lastInjectionTime = null;
                        _lastDetectedModuleName = null;
                        _lastInjectionConfirmed = false;
                        _moduleMissingCount = 0;

                        StatusGlow.BeginAnimation(DropShadowEffect.OpacityProperty, null);
                        StatusGlow.BeginAnimation(DropShadowEffect.BlurRadiusProperty, null);
                        StatusDotBrush.BeginAnimation(SolidColorBrush.ColorProperty, null);

                        StatusDotBrush.Color = Color.FromRgb(0xcc, 0x33, 0x33);
                        StatusText.Text = "Not Injected";
                        StatusText.Foreground = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55));
                        StatusGlow.Opacity = 0;
                        StatusGlow.BlurRadius = 0;
                        StatusDetail.Text = "All instances closed";
                    }
                    else
                    {
                        SetStatus(true, $"{_injectedPids.Count} instance(s) active");
                    }
                }
                catch { }
            }));
        }

        private async void ExecuteBtn_Click(object sender, RoutedEventArgs e)
        {
            if (!_monacoReady) { Log("Editor not ready.", "#cc3333"); return; }
            if (MonacoEditor?.CoreWebView2 == null) { Log("Editor not ready.", "#cc3333"); return; }

            string fnCheck = "\"undefined\"";
            for (int i = 0; i < 10; i++)
            {
                try { fnCheck = await MonacoEditor.CoreWebView2.ExecuteScriptAsync("typeof window.GetText"); }
                catch { }
                if (fnCheck == "\"function\"") break;
                await Task.Delay(500);
            }

            if (fnCheck != "\"function\"")
            {
                Log("Monaco API not ready yet.", "#cc3333");
                _monacoReady = false;
                return;
            }

            string code = "";
            try
            {
                var raw = await MonacoEditor.CoreWebView2.ExecuteScriptAsync("window.GetText()");
                if (string.IsNullOrEmpty(raw) || raw == "null" || raw == "\"\"")
                {
                    Log("Nothing to execute.", "#888888");
                    return;
                }
                code = System.Text.Json.JsonSerializer.Deserialize<string>(raw) ?? "";
            }
            catch (Exception ex) { Log($"[ERROR] Failed to get code: {ex.Message}", "#cc3333"); return; }

            if (string.IsNullOrWhiteSpace(code)) { Log("Nothing to execute.", "#888888"); return; }

            if (!_injected || _injectedPids.Count == 0)
            {
                Log("Not injected. Re-attaching all instances...", "#e0c060");

                var processes = Process.GetProcessesByName("RobloxPlayerBeta");
                if (processes.Length == 0)
                {
                    Log("Roblox not found.", "#cc3333");
                    SetStatus(false, "Lost attachment");
                    _injected = false;
                    return;
                }

                foreach (var p in processes)
                {
                    try
                    {
                        bool attached = await Task.Run(() =>
                        {
                            bool ok = false;
                            SafeApiCall(() =>
                            {
                                var mi = typeof(SpashAPIMadium.API).GetMethod("AttachAPI", new Type[] { typeof(string) });
                                if (mi != null)
                                {
                                    mi.Invoke(null, new object[] { p.Id.ToString() });
                                    ok = true;
                                }
                                else
                                {
                                    var mi2 = typeof(SpashAPIMadium.API).GetMethod("AttachAPI", Type.EmptyTypes);
                                    if (mi2 != null)
                                    {
                                        mi2.Invoke(null, null);
                                        ok = true;
                                    }
                                }
                            });
                            return ok;
                        });

                        if (attached)
                        {
                            _injectedPids.Add(p.Id);
                            Log($"Re-attached PID {p.Id}", "#e0c060");
                        }
                        else
                        {
                            Log($"Re-attach failed for PID {p.Id}: API call returned no result", "#cc3333");
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"Re-attach failed for PID {p.Id}: {ex.Message}", "#cc3333");
                    }
                }

                await Task.Delay(500);

                if (_injectedPids.Count == 0)
                {
                    Log("Re-attach failed on all instances.", "#cc3333");
                    SetStatus(false, "Lost attachment");
                    _injected = false;
                    return;
                }

                _injected = true;
                SetStatus(true, $"{_injectedPids.Count} instance(s) re-attached");
            }

            var targets = _selectedInstances.Any() ? _selectedInstances : null;

            if (targets != null && targets.Any())
            {
                foreach (var inst in targets)
                {
                    try
                    {
                        await ExecuteOnInstanceAsync(code, inst);
                    }
                    catch (Exception ex)
                    {
                        Log($"[ERROR] Execute failed on instance {inst.Pid}: {ex.Message}", "#cc3333");
                    }
                }
            }
            else
            {
                var pidSnapshot = _injectedPids.ToList();
                var failedPids = new List<int>();

                foreach (var pid in pidSnapshot)
                {
                    try
                    {
                        bool ok = await Task.Run(() =>
                        {
                            bool success = false;
                            SafeApiCall(() =>
                            {
                                SpashAPIMadium.API.ExecuteScript(code, pid.ToString());
                                success = true;
                            });
                            return success;
                        });

                        if (ok)
                            Log($"Executed on PID {pid}  ·  {code.Length} chars", "#4ec94e");
                        else
                        {
                            Log($"[ERROR] Execute failed on PID {pid} (API error)", "#cc3333");
                            failedPids.Add(pid);
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"[ERROR] Execute failed on PID {pid}: {ex.Message}", "#cc3333");
                        failedPids.Add(pid);
                    }
                }

                foreach (var pid in failedPids)
                {
                    _injectedPids.Remove(pid);
                    Log($"Removed dead PID {pid} from list.", "#888888");
                }

                if (_injectedPids.Count == 0)
                {
                    _injected = false;
                    SetStatus(false, "Lost attachment");
                    Log("All instances lost. Please re-inject.", "#cc3333");
                }
            }
        }

        //--------------------------------------------------------------------------
        private void ScriptsMenu_Click(object sender, RoutedEventArgs e)
        {
            var menu = new ContextMenu();

            var openWorkspace = new MenuItem
            {
                Header = "Open Workspace",
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
            };
            openWorkspace.Click += (_, _) =>
            {
                EnsureWorkspaceFolder();
                Process.Start(new ProcessStartInfo { FileName = _workspaceFolder, UseShellExecute = true });
            };

            var openScripts = new MenuItem
            {
                Header = "Open Scripts Folder",
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
            };
            openScripts.Click += (_, _) =>
            {
                EnsureScriptsFolder();
                Process.Start(new ProcessStartInfo { FileName = _scriptsFolder, UseShellExecute = true });
            };

            menu.Items.Add(openWorkspace);
            menu.Items.Add(new Separator());
            menu.Items.Add(openScripts);

            menu.PlacementTarget = ScriptsMenuBtn;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }

        private void ScriptSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            string query = ScriptSearchBox.Text.Trim().ToLowerInvariant();

            foreach (var item in ScriptsList.Items)
            {
                if (ScriptsList.ItemContainerGenerator.ContainerFromItem(item) is ListBoxItem container)
                {
                    if (item is ScriptItem si)
                        container.Visibility = string.IsNullOrEmpty(query) || si.FileName.ToLowerInvariant().Contains(query)
                            ? Visibility.Visible
                            : Visibility.Collapsed;
                }
            }
        }

        private async Task ExecuteOnInstanceAsync(string code, InstanceInfo info)
        {
            try
            {
                bool ok = await Task.Run(() =>
                {
                    bool success = false;
                    SafeApiCall(() =>
                    {
                        SpashAPIMadium.API.ExecuteScript(code, info.Pid.ToString());
                        success = true;
                    });
                    return success;
                });

                if (ok)
                    Log($"Executed on PID {info.Pid}  ·  {code.Length} chars", "#4ec94e");
                else
                    Log($"[ERROR] Execute failed on PID {info.Pid} (API error)", "#cc3333");
            }
            catch (Exception ex)
            {
                Log($"[ERROR] Execute failed on PID {info.Pid}: {ex.Message}", "#cc3333");
            }
        }

        private async void ClearBtn_Click(object sender, RoutedEventArgs e)
        {
            if (!_monacoReady) return;
            try
            {
                await MonacoEditor.CoreWebView2.ExecuteScriptAsync("ClearCode()");
                if (_activeTabIndex >= 0 && _activeTabIndex < _tabs.Count)
                    _tabs[_activeTabIndex].Content = "";
                Log("Editor cleared.", "#444");
            }
            catch { }
        }

        private async void OpenBtn_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "Open Script",
                Filter = "Lua (*.lua)|*.lua|Text (*.txt)|*.txt|All (*.*)|*.*",
                InitialDirectory = _scriptsFolder,
            };
            if (dlg.ShowDialog() != true) return;
            try
            {
                string text = await File.ReadAllTextAsync(dlg.FileName);
                await AddNewTab(IOPath.GetFileName(dlg.FileName), text);
                Log($"Opened: {IOPath.GetFileName(dlg.FileName)}", "#6d9ddb");
            }
            catch (Exception ex) { Log($"[ERROR] {ex.Message}", "#cc3333"); }
        }

        private async void SaveBtn_Click(object sender, RoutedEventArgs e)
        {
            string content = "";
            if (_monacoReady)
            {
                try
                {
                    var raw = await MonacoEditor.CoreWebView2.ExecuteScriptAsync("GetText()");
                    content = System.Text.Json.JsonSerializer.Deserialize<string>(raw) ?? "";
                }
                catch { }
            }

            var dlg = new SaveFileDialog
            {
                Title = "Save Script",
                Filter = "Lua (*.lua)|*.lua|Text (*.txt)|*.txt|All (*.*)|*.*",
                DefaultExt = ".lua",
                InitialDirectory = _scriptsFolder,
                FileName = _activeTabIndex >= 0 ? _tabs[_activeTabIndex].Title : "script",
            };
            if (dlg.ShowDialog() != true) return;
            try
            {
                await File.WriteAllTextAsync(dlg.FileName, content);
                if (_activeTabIndex >= 0)
                {
                    string name = IOPath.GetFileName(dlg.FileName);
                    _tabs[_activeTabIndex].Title = name;
                    if (TabsPanel.Children[_activeTabIndex] is Border brd && brd.Tag is TabHeaderRefs refs)
                        refs.Label.Text = name;
                }
                LoadScriptsList();
                Log($"Saved: {IOPath.GetFileName(dlg.FileName)}", "#6ddb6d");
            }
            catch (Exception ex) { Log($"[ERROR] {ex.Message}", "#cc3333"); }
        }

        // ══════════════════════════════════════════════════════════════════
        //  MULTI INSTANCE
        // ══════════════════════════════════════════════════════════════════

        private void OpenMultiInstance_Click(object sender, RoutedEventArgs e)
        {
            MonacoEditor.Visibility = Visibility.Collapsed;
            MultiInstanceOverlay.Visibility = Visibility.Visible;
            RefreshInstanceList();
        }

        private void CloseMultiInstance_Click(object sender, RoutedEventArgs e)
        {
            MultiInstanceOverlay.Visibility = Visibility.Collapsed;
            MonacoEditor.Visibility = Visibility.Visible;
        }

        private void RefreshInstances_Click(object sender, RoutedEventArgs e)
        {
            RefreshInstanceList();
        }

        private void RefreshInstanceList()
        {
            _activeInstances.Clear();
            InstancesPanel.Children.Clear();

            var robloxProcesses = new List<Process>();
            try
            {
                foreach (var p in Process.GetProcesses())
                {
                    try
                    {
                        string name = p.ProcessName ?? string.Empty;
                        if (name.Equals("RobloxPlayerBeta", StringComparison.OrdinalIgnoreCase) && !p.HasExited)
                            robloxProcesses.Add(p);
                    }
                    catch { }
                }
            }
            catch { }

            int count = robloxProcesses.Count;
            InstanceCountLabel.Text = $"  —  {count} instance{(count != 1 ? "s" : "")}";

            if (count == 0)
            {
                var empty = new TextBlock
                {
                    Text = "No Roblox instances found.\nOpen Roblox and click Refresh.",
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 11,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44)),
                    TextAlignment = TextAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 24, 0, 24),
                    TextWrapping = TextWrapping.Wrap,
                };
                TextOptions.SetTextFormattingMode(empty, TextFormattingMode.Display);
                InstancesPanel.Children.Add(empty);
                return;
            }

            foreach (var proc in robloxProcesses)
            {
                var info = BuildInstanceInfo(proc);
                _activeInstances.Add(info);
                var card = BuildInstanceCard(info);

                card.RenderTransform = new TranslateTransform(0, 12);
                card.Opacity = 0;
                int delay = _activeInstances.Count * 55;

                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(delay) };
                timer.Tick += (s, _) =>
                {
                    timer.Stop();
                    var slideIn = new DoubleAnimation(0, TimeSpan.FromMilliseconds(200))
                    { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
                    var fadeIn = new DoubleAnimation(1, TimeSpan.FromMilliseconds(200));
                    card.RenderTransform.BeginAnimation(TranslateTransform.YProperty, slideIn);
                    card.BeginAnimation(UIElement.OpacityProperty, fadeIn);
                };
                timer.Start();

                InstancesPanel.Children.Add(card);
            }

            _selectedInstances.RemoveAll(i => !_activeInstances.Any(a => a.Pid == i.Pid));
            if (_selectedInstances.Count == 0) ClearSelectedInstance();

            Log($"Instance scan: {count} Roblox process(es) found.", "#6d9ddb");
        }

        private InstanceInfo BuildInstanceInfo(Process proc)
        {
            string windowTitle = "";
            try { windowTitle = proc.MainWindowTitle ?? ""; } catch { }

            return new InstanceInfo
            {
                Pid = proc.Id,
                ProcessName = proc.ProcessName,
                WindowTitle = windowTitle,
                StartTime = TryGetStartTime(proc),
            };
        }

        private void Roblox_Exited(object? sender, EventArgs e)
        {
            if (sender is Process p)
            {
                int pid = p.Id;
                _ = Task.Delay(500).ContinueWith(_ => ClearInstanceState(pid));
            }
        }

        private static DateTime? TryGetStartTime(Process p)
        {
            try { return p.StartTime; } catch { return null; }
        }

        private Border BuildInstanceCard(InstanceInfo info)
        {
            var card = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x12, 0x12, 0x12)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x22)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Margin = new Thickness(0, 0, 0, 6),
                Padding = new Thickness(12, 10, 12, 10),
                Cursor = Cursors.Hand,
            };
            card.RenderTransform = new TranslateTransform();

            var pidLabel = new TextBlock
            {
                Text = $"PID  {info.Pid}",
                FontFamily = new FontFamily("Consolas"),
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44)),
                Margin = new Thickness(0, 0, 0, 3),
            };
            TextOptions.SetTextFormattingMode(pidLabel, TextFormattingMode.Display);

            var nameLabel = new TextBlock
            {
                Text = info.ProcessName,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(0xcc, 0xcc, 0xcc)),
                Margin = new Thickness(0, 0, 0, 2),
            };
            TextOptions.SetTextFormattingMode(nameLabel, TextFormattingMode.Display);

            string uptime = "";
            if (info.StartTime.HasValue)
            {
                var diff = DateTime.Now - info.StartTime.Value;
                uptime = diff.TotalHours >= 1
                    ? $"{(int)diff.TotalHours}h {diff.Minutes}m"
                    : $"{diff.Minutes}m {diff.Seconds}s";
            }

            var detailLabel = new TextBlock
            {
                Text = string.IsNullOrEmpty(uptime) ? $"PID {info.Pid}" : $"PID {info.Pid}  ·  up {uptime}",
                FontFamily = new FontFamily("Consolas"),
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
            };
            TextOptions.SetTextFormattingMode(detailLabel, TextFormattingMode.Display);

            var infoCol = new StackPanel { Orientation = Orientation.Vertical };
            infoCol.Children.Add(pidLabel);
            infoCol.Children.Add(nameLabel);
            infoCol.Children.Add(detailLabel);

            var selBar = new Border
            {
                Width = 3,
                Background = new SolidColorBrush(Color.FromRgb(0x6d, 0xdb, 0x6d)),
                CornerRadius = new CornerRadius(2),
                Opacity = 0,
                Margin = new Thickness(0, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Stretch,
            };

            var killBtn = new Button
            {
                Content = "Kill",
                Style = (Style)FindResource("Btn"),
                Width = 60,
                Height = 26,
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(0xcc, 0x55, 0x55)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x44, 0x22, 0x22)),
                Background = new SolidColorBrush(Color.FromRgb(0x1a, 0x0e, 0x0e)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0),
                Tag = info,
            };
            killBtn.Click += KillInstance_Click;

            var row = new Grid { VerticalAlignment = VerticalAlignment.Center };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(selBar, 0);
            Grid.SetColumn(infoCol, 1);
            Grid.SetColumn(killBtn, 2);
            row.Children.Add(selBar);
            row.Children.Add(infoCol);
            row.Children.Add(killBtn);
            card.Child = row;

            card.MouseEnter += (_, _) =>
            {
                ((SolidColorBrush)card.Background).BeginAnimation(SolidColorBrush.ColorProperty,
                    new ColorAnimation(Color.FromRgb(0x17, 0x17, 0x17), TimeSpan.FromMilliseconds(120)));
                ((SolidColorBrush)card.BorderBrush).BeginAnimation(SolidColorBrush.ColorProperty,
                    new ColorAnimation(Color.FromRgb(0x33, 0x33, 0x33), TimeSpan.FromMilliseconds(120)));
            };
            card.MouseLeave += (_, _) =>
            {
                bool isSel = _selectedInstances.Any(i => i.Pid == info.Pid);
                ((SolidColorBrush)card.Background).BeginAnimation(SolidColorBrush.ColorProperty,
                    new ColorAnimation(
                        isSel ? Color.FromRgb(0x16, 0x22, 0x16) : Color.FromRgb(0x12, 0x12, 0x12),
                        TimeSpan.FromMilliseconds(150)));
                ((SolidColorBrush)card.BorderBrush).BeginAnimation(SolidColorBrush.ColorProperty,
                    new ColorAnimation(
                        isSel ? Color.FromRgb(0x2a, 0x55, 0x2a) : Color.FromRgb(0x22, 0x22, 0x22),
                        TimeSpan.FromMilliseconds(150)));
            };

            card.MouseLeftButtonDown += (s, e) =>
            {
                if (e.OriginalSource is Button) return;
                SelectInstance(info, card, selBar, nameLabel);
                e.Handled = true;
            };

            if (_selectedInstances.Any(i => i.Pid == info.Pid))
            {
                ((SolidColorBrush)card.Background).Color = Color.FromRgb(0x16, 0x22, 0x16);
                ((SolidColorBrush)card.BorderBrush).Color = Color.FromRgb(0x2a, 0x55, 0x2a);
                selBar.Opacity = 1;
                ((SolidColorBrush)nameLabel.Foreground).Color = Color.FromRgb(0x6d, 0xdb, 0x6d);
            }

            return card;
        }

        private void SelectInstance(InstanceInfo info, Border card, Border selBar, TextBlock nameLabel)
        {
            if (_selectedInstances.Any(i => i.Pid == info.Pid))
                _selectedInstances.RemoveAll(i => i.Pid == info.Pid);
            else
                _selectedInstances.Add(info);

            bool isNowSelected = _selectedInstances.Any(i => i.Pid == info.Pid);

            selBar.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(isNowSelected ? 1 : 0, TimeSpan.FromMilliseconds(180)));
            ((SolidColorBrush)nameLabel.Foreground).BeginAnimation(SolidColorBrush.ColorProperty,
                new ColorAnimation(
                    isNowSelected ? Color.FromRgb(0x6d, 0xdb, 0x6d) : Color.FromRgb(0xcc, 0xcc, 0xcc),
                    TimeSpan.FromMilliseconds(180)));
            ((SolidColorBrush)card.Background).BeginAnimation(SolidColorBrush.ColorProperty,
                new ColorAnimation(
                    isNowSelected ? Color.FromRgb(0x16, 0x22, 0x16) : Color.FromRgb(0x12, 0x12, 0x12),
                    TimeSpan.FromMilliseconds(180)));
            ((SolidColorBrush)card.BorderBrush).BeginAnimation(SolidColorBrush.ColorProperty,
                new ColorAnimation(
                    isNowSelected ? Color.FromRgb(0x2a, 0x55, 0x2a) : Color.FromRgb(0x22, 0x22, 0x22),
                    TimeSpan.FromMilliseconds(180)));

            if (_selectedInstances.Count == 0)
            {
                SelectedInstanceLabel.Text = "No instance selected";
                ((SolidColorBrush)SelectedInstanceLabel.Foreground).BeginAnimation(SolidColorBrush.ColorProperty,
                    new ColorAnimation(Color.FromRgb(0x44, 0x44, 0x44), TimeSpan.FromMilliseconds(200)));
                SelectedDotBrush.BeginAnimation(SolidColorBrush.ColorProperty,
                    new ColorAnimation(Color.FromRgb(0x33, 0x33, 0x33), TimeSpan.FromMilliseconds(200)));
                ExecuteOnSelectedBtn.IsEnabled = false;
            }
            else
            {
                SelectedInstanceLabel.Text = _selectedInstances.Count == 1
                    ? $"PID {_selectedInstances[0].Pid}"
                    : $"{_selectedInstances.Count} instances selected";
                ((SolidColorBrush)SelectedInstanceLabel.Foreground).BeginAnimation(SolidColorBrush.ColorProperty,
                    new ColorAnimation(Color.FromRgb(0x6d, 0xdb, 0x6d), TimeSpan.FromMilliseconds(200)));
                SelectedDotBrush.BeginAnimation(SolidColorBrush.ColorProperty,
                    new ColorAnimation(Color.FromRgb(0x6d, 0xdb, 0x6d), TimeSpan.FromMilliseconds(200)));
                ExecuteOnSelectedBtn.IsEnabled = true;
            }

            Log($"Selected: {_selectedInstances.Count} instance(s)", "#6d9ddb");
        }

        private void ClearSelectedInstance()
        {
            _selectedInstances.Clear();
            SelectedInstanceLabel.Text = "No instance selected";
            ((SolidColorBrush)SelectedInstanceLabel.Foreground).BeginAnimation(SolidColorBrush.ColorProperty,
                new ColorAnimation(Color.FromRgb(0x44, 0x44, 0x44), TimeSpan.FromMilliseconds(200)));
            SelectedDotBrush.BeginAnimation(SolidColorBrush.ColorProperty,
                new ColorAnimation(Color.FromRgb(0x33, 0x33, 0x33), TimeSpan.FromMilliseconds(200)));
            ExecuteOnSelectedBtn.IsEnabled = false;
        }

        private void KillInstance_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not InstanceInfo info) return;

            var result = MessageBox.Show(
                $"Kill Roblox instance?\n\nPID: {info.Pid}",
                "JadeX — Kill Instance",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes) return;

            // FIX: Run kill entirely off-thread with SafeApiCall — never let
            // TypeLoadException bubble to the UI thread and crash the window.
            Task.Run(() =>
            {
                bool apiKillSucceeded = false;

                SafeApiCall(() =>
                {
                    SpashAPIMadium.API.KillRoblox(info.Pid.ToString());
                    apiKillSucceeded = true;
                });

                if (!apiKillSucceeded)
                {
                    // Fallback: kill via Process directly
                    try
                    {
                        var proc = Process.GetProcessById(info.Pid);
                        proc.Kill();
                        Dispatcher.Invoke(() => Log($"Process.Kill() → PID {info.Pid}", "#cc5555"));
                    }
                    catch (Exception ex2)
                    {
                        Dispatcher.Invoke(() => Log($"[ERROR] Kill failed: {ex2.Message}", "#cc3333"));
                    }
                }
                else
                {
                    Dispatcher.Invoke(() => Log($"Kill sent → PID {info.Pid}", "#cc5555"));
                }

                Dispatcher.Invoke(() =>
                {
                    if (_selectedInstances.Any(i => i.Pid == info.Pid))
                        _selectedInstances.RemoveAll(i => i.Pid == info.Pid);
                    if (_selectedInstances.Count == 0) ClearSelectedInstance();

                    var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
                    t.Tick += (s, _) => { t.Stop(); RefreshInstanceList(); };
                    t.Start();
                });
            });
        }

        private async void ExecuteOnSelected_Click(object sender, RoutedEventArgs e)
        {
            if (!_monacoReady) { Log("Editor not ready.", "#cc3333"); return; }
            if (!_injected) { Log("Not injected!", "#cc3333"); return; }
            if (_selectedInstances.Count == 0) { Log("No instances selected.", "#cc3333"); return; }

            string code = "";
            try
            {
                var raw = await MonacoEditor.CoreWebView2.ExecuteScriptAsync("GetText()");
                code = System.Text.Json.JsonSerializer.Deserialize<string>(raw) ?? "";
            }
            catch (Exception ex) { Log($"[ERROR] Failed to read editor: {ex.Message}", "#cc3333"); return; }

            if (string.IsNullOrWhiteSpace(code)) { Log("Nothing to execute.", "#888888"); return; }

            foreach (var inst in _selectedInstances)
                await ExecuteOnInstanceAsync(code, inst);
        }

        // ══════════════════════════════════════════════════════════════════
        //  WINDOW CHROME
        // ══════════════════════════════════════════════════════════════════

        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left) DragMove();
        }

        private void MinimizeBtn_Click(object sender, RoutedEventArgs e)
            => WindowState = WindowState.Minimized;

        private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();

        private void OpenSettings_Click(object sender, RoutedEventArgs e)
        {
            // FIX: Run settings open entirely on UI thread with full exception guard.
            // Never set Owner if main window might not be in a valid state.
            // Use a dispatcher-deferred call so any ongoing layout/render finishes first.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    var wnd = new SettingsWindow();
                    wnd.SettingsApplied += ApplySettings;
                    wnd.LogRequested += msg => Dispatcher.Invoke(() => Log(msg, "#888899"));

                    // Only set Owner when window is visible and in normal/minimized state
                    try
                    {
                        if (IsLoaded && IsVisible &&
                            (WindowState == WindowState.Normal || WindowState == WindowState.Maximized))
                        {
                            wnd.Owner = this;
                        }
                    }
                    catch { /* ignore — Owner is optional */ }

                    wnd.ShowDialog();
                }
                catch (Exception ex)
                {
                    try { Log($"[ERROR] Opening settings: {ex.Message}", "#cc3333"); } catch { }
                    MessageBox.Show(
                        $"Could not open Settings.\n\n{ex.Message}",
                        "JadeX", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }), DispatcherPriority.Background);
        }

        private void ApplySettings(JadeXSettings s)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => ApplySettings(s));
                return;
            }

            try { Topmost = s.AlwaysOnTop; } catch { }

            if (_monacoReady && MonacoEditor?.CoreWebView2 != null)
            {
                try
                {
                    var obj = new
                    {
                        theme = s.Theme,
                        fontSize = s.FontSize,
                        lineHeight = s.LineHeight,
                        fontFamily = s.FontFamily,
                        minimap = s.Minimap,
                        wordWrap = s.WordWrap,
                        folding = s.Folding,
                        autoIndent = s.AutoIndent,
                        links = s.Links,
                        fontLigatures = s.FontLigatures,
                        smoothScrolling = s.SmoothScrolling,
                        smoothCaret = s.SmoothCaret,
                        renderWhitespace = s.RenderWhitespace
                    };
                    string json = System.Text.Json.JsonSerializer.Serialize(obj);
                    MonacoEditor.CoreWebView2.PostWebMessageAsString(json);
                }
                catch { }
            }

            try { _autoInjectEnabled = s.AutoInject; } catch { }

            // FIX: All API calls off-thread + SafeApiCall
            Task.Run(() =>
            {
                SafeApiCall(() =>
                {
                    SpashAPIMadium.API.Custom.SetUserAgent("JadeX / 1.0.5");
                    SpashAPIMadium.API.Custom.SetIdentity("JadeX", "1.0.5", true);
                });

                SafeApiCall(() =>
                {
                    var setAuto = typeof(SpashAPIMadium.API).GetMethod(
                        "SetAutoInject",
                        System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
                    setAuto?.Invoke(null, new object[] { s.AutoInject });
                });
            });
        }

        // ══════════════════════════════════════════════════════════════════
        //  HELPER CLASSES
        // ══════════════════════════════════════════════════════════════════

        internal class TabHeaderRefs
        {
            public int Index { get; set; }
            public required TextBlock Label { get; set; }
            public required Border Accent { get; set; }
            public required Ellipse Dot { get; set; }
            public required Border Inner { get; set; }
        }

        internal class EditorTab
        {
            public string Title { get; set; } = "Script";
            public string Content { get; set; } = "";
        }

        internal class ScriptItem
        {
            public string FileName { get; set; } = "";
            public string FullPath { get; set; } = "";
            public override string ToString() => FileName;
        }

        internal class InstanceInfo
        {
            public int Pid { get; set; }
            public string ProcessName { get; set; } = "";
            public string WindowTitle { get; set; } = "";
            public DateTime? StartTime { get; set; }
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  GridLengthAnimation
    // ══════════════════════════════════════════════════════════════════
    public class GridLengthAnimation : AnimationTimeline
    {
        public static readonly DependencyProperty FromProperty =
            DependencyProperty.Register(nameof(From), typeof(GridLength), typeof(GridLengthAnimation));
        public static readonly DependencyProperty ToProperty =
            DependencyProperty.Register(nameof(To), typeof(GridLength), typeof(GridLengthAnimation));
        public static readonly DependencyProperty EasingFunctionProperty =
            DependencyProperty.Register(nameof(EasingFunction), typeof(IEasingFunction), typeof(GridLengthAnimation));

        public GridLength From
        {
            get => (GridLength)GetValue(FromProperty);
            set => SetValue(FromProperty, value);
        }
        public GridLength To
        {
            get => (GridLength)GetValue(ToProperty);
            set => SetValue(ToProperty, value);
        }
        public IEasingFunction? EasingFunction
        {
            get => (IEasingFunction?)GetValue(EasingFunctionProperty);
            set => SetValue(EasingFunctionProperty, value);
        }

        public override Type TargetPropertyType => typeof(GridLength);

        protected override Freezable CreateInstanceCore() => new GridLengthAnimation();

        public override object GetCurrentValue(object defaultOriginValue,
            object defaultDestinationValue, AnimationClock animationClock)
        {
            if (animationClock.CurrentProgress == null) return From;

            double progress = animationClock.CurrentProgress.Value;
            if (EasingFunction != null)
                progress = EasingFunction.Ease(progress);

            double fromVal = From.Value;
            double toVal = To.Value;
            double current = fromVal + (toVal - fromVal) * progress;

            if (From.GridUnitType == GridUnitType.Star && To.GridUnitType == GridUnitType.Star)
                return new GridLength(current, GridUnitType.Star);

            return new GridLength(current, GridUnitType.Pixel);
        }
    }
}