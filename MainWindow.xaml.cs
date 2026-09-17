using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Win11SysDash
{
    public partial class MainWindow : Window
    {
        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("shell32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern void SHRunDialog(IntPtr hwnd, IntPtr hIcon, string? pszPath, string? pszTitle, string? pszPrompt, uint flags);

        private DispatcherTimer _timer;
        private PerformanceCounter? _totalCpuCounter;
        private List<PerformanceCounter> _coreCounters = new();
        private PerformanceCounter? _commitCounter;
        private PerformanceCounter? _netRecvCounter;

        private Dictionary<int, List<float>> _cpuHistory = new();
        private List<float> _pfHistory = new();
        private List<float> _netHistory = new();
        private const int MaxPoints = 45;

        private ulong _totalPhysK = 2096620;
        private double _commitPeakK = 0;
        private Dictionary<int, TimeSpan> _lastCpuTimes = new();
        private DateTime _lastSampleTime = DateTime.MinValue;

        public MainWindow()
        {
            InitializeComponent();
            InitPerformanceCounters();
            GetPhysicalMemory();

            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000) };
            _timer.Tick += Timer_Tick;
            _timer.Start();

            var coreList = Enumerable.Range(0, Math.Min(Environment.ProcessorCount, 8)).Select(i => i).ToList();
            CpuHistoryItemsControl.ItemsSource = coreList;

            RefreshAllTabs();
        }

        private void InitPerformanceCounters()
        {
            try
            {
                _totalCpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
                _totalCpuCounter.NextValue();

                int cores = Math.Min(Environment.ProcessorCount, 8);
                for (int i = 0; i < cores; i++)
                {
                    var c = new PerformanceCounter("Processor", "% Processor Time", i.ToString());
                    c.NextValue();
                    _coreCounters.Add(c);
                    _cpuHistory[i] = new List<float>();
                }

                _commitCounter = new PerformanceCounter("Memory", "Committed Bytes");
                _commitCounter.NextValue();

                var category = new PerformanceCounterCategory("Network Interface");
                var instance = category.GetInstanceNames().FirstOrDefault();
                if (instance != null)
                {
                    _netRecvCounter = new PerformanceCounter("Network Interface", "Bytes Received/sec", instance);
                    _netRecvCounter.NextValue();
                }
            }
            catch { }
        }

        private void GetPhysicalMemory()
        {
            try
            {
                var searcher = new ManagementObjectSearcher("SELECT TotalPhysicalMemory FROM Win32_ComputerSystem");
                foreach (var obj in searcher.Get())
                {
                    ulong bytes = Convert.ToUInt64(obj["TotalPhysicalMemory"]);
                    _totalPhysK = bytes / 1024;
                }
            }
            catch { }
        }

        private void Timer_Tick(object? sender, EventArgs e)
        {
            UpdateCpuAndPf();
            UpdateSystemStats();
            RefreshAllTabs();
        }

        private void RefreshAllTabs()
        {
            UpdateApplicationsTab();
            UpdateProcessesTab();
            UpdateNetworkingTab();
            UpdateUsersTab();
        }

        // --- TAB 1: APPLICATIONS ---
        private void UpdateApplicationsTab()
        {
            try
            {
                var apps = Process.GetProcesses()
                    .Where(p => !string.IsNullOrEmpty(p.MainWindowTitle))
                    .Select(p => new AppTaskItem
                    {
                        Title = p.MainWindowTitle,
                        Status = p.Responding ? "Running" : "Not Responding",
                        ProcessId = p.Id,
                        MainWindowHandle = p.MainWindowHandle
                    })
                    .ToList();

                AppsListView.ItemsSource = apps;
            }
            catch { }
        }

        private void EndAppTask_Click(object sender, RoutedEventArgs e)
        {
            if (AppsListView.SelectedItem is AppTaskItem item)
            {
                KillProcessById(item.ProcessId);
                UpdateApplicationsTab();
            }
        }

        private void SwitchToApp_Click(object sender, RoutedEventArgs e)
        {
            if (AppsListView.SelectedItem is AppTaskItem item && item.MainWindowHandle != IntPtr.Zero)
            {
                SetForegroundWindow(item.MainWindowHandle);
            }
        }

        // --- TAB 2: PROCESSES ---
        private void UpdateProcessesTab()
        {
            try
            {
                DateTime now = DateTime.Now;
                double timeDifference = (_lastSampleTime == DateTime.MinValue) ? 1.0 : (now - _lastSampleTime).TotalSeconds;
                _lastSampleTime = now;

                var procs = Process.GetProcesses()
                    .Select(p =>
                    {
                        try
                        {
                            double cpuPercent = 0;
                            if (_lastCpuTimes.TryGetValue(p.Id, out TimeSpan lastTime))
                            {
                                TimeSpan currentTotal = p.TotalProcessorTime;
                                double timeUsed = (currentTotal - lastTime).TotalSeconds;
                                cpuPercent = (timeUsed / (timeDifference * Environment.ProcessorCount)) * 100.0;
                            }
                            _lastCpuTimes[p.Id] = p.TotalProcessorTime;

                            return new ProcessRowItem
                            {
                                Name = p.ProcessName + ".exe",
                                Id = p.Id,
                                CpuText = $"{Math.Min((int)Math.Round(cpuPercent), 99):D2}",
                                MemText = $"{p.WorkingSet64 / 1024:N0} K",
                                RawMem = p.WorkingSet64
                            };
                        }
                        catch
                        {
                            return new ProcessRowItem
                            {
                                Name = p.ProcessName + ".exe",
                                Id = p.Id,
                                CpuText = "00",
                                MemText = "N/A",
                                RawMem = 0
                            };
                        }
                    })
                    .OrderByDescending(p => p.RawMem)
                    .Take(45)
                    .ToList();

                ProcessesListView.ItemsSource = procs;
            }
            catch { }
        }

        private void EndProcess_Click(object sender, RoutedEventArgs e)
        {
            if (ProcessesListView.SelectedItem is ProcessRowItem item)
            {
                KillProcessById(item.Id);
                UpdateProcessesTab();
            }
        }

        private void KillProcessById(int id)
        {
            try
            {
                var target = Process.GetProcessById(id);
                target.Kill(true); // Aggressiv inkl. Unterprozesse
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Unable to terminate process: {ex.Message}", "Task Manager Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // Context Menu Handlers
        private void ContextMenu_EndProcess(object sender, RoutedEventArgs e) => EndProcess_Click(sender, e);
        
        private void ContextMenu_EndProcessTree(object sender, RoutedEventArgs e) => EndProcess_Click(sender, e);

        private void ContextMenu_OpenLocation(object sender, RoutedEventArgs e)
        {
            if (ProcessesListView.SelectedItem is ProcessRowItem item)
            {
                try
                {
                    var p = Process.GetProcessById(item.Id);
                    string? path = p.MainModule?.FileName;
                    if (path != null) Process.Start("explorer.exe", $"/select,\"{path}\"");
                }
                catch (Exception ex) { MessageBox.Show(ex.Message); }
            }
        }

        private void SetPriority(ProcessPriorityClass priority)
        {
            if (ProcessesListView.SelectedItem is ProcessRowItem item)
            {
                try
                {
                    Process.GetProcessById(item.Id).PriorityClass = priority;
                }
                catch (Exception ex) { MessageBox.Show(ex.Message); }
            }
        }

        private void SetPriority_Realtime(object sender, RoutedEventArgs e) => SetPriority(ProcessPriorityClass.RealTime);
        private void SetPriority_High(object sender, RoutedEventArgs e) => SetPriority(ProcessPriorityClass.High);
        private void SetPriority_AboveNormal(object sender, RoutedEventArgs e) => SetPriority(ProcessPriorityClass.AboveNormal);
        private void SetPriority_Normal(object sender, RoutedEventArgs e) => SetPriority(ProcessPriorityClass.Normal);
        private void SetPriority_BelowNormal(object sender, RoutedEventArgs e) => SetPriority(ProcessPriorityClass.BelowNormal);
        private void SetPriority_Low(object sender, RoutedEventArgs e) => SetPriority(ProcessPriorityClass.Idle);

        // --- TAB 3: PERFORMANCE ---
        private void UpdateCpuAndPf()
        {
            double totalCpu = _totalCpuCounter != null ? _totalCpuCounter.NextValue() : 0;
            CpuPercentText.Text = $"{Math.Round(totalCpu)} %";
            StatusCpu.Text = $"CPU Usage: {Math.Round(totalCpu)}%";
            DrawBarMeter(CpuMeterCanvas, (float)totalCpu);

            for (int i = 0; i < _coreCounters.Count; i++)
            {
                float val = _coreCounters[i].NextValue();
                var history = _cpuHistory[i];
                history.Add(val);
                if (history.Count > MaxPoints) history.RemoveAt(0);
            }
            RedrawCoreGraphs();

            double committedBytes = _commitCounter != null ? _commitCounter.NextValue() : 0;
            double committedMb = committedBytes / (1024.0 * 1024.0);
            double committedK = committedBytes / 1024.0;
            if (committedK > _commitPeakK) _commitPeakK = committedK;

            PfValueText.Text = $"{Math.Round(committedMb)} MB";
            double commitLimitK = (_totalPhysK + 4000000); 
            double pfPercent = Math.Min((committedK / commitLimitK) * 100.0, 100.0);
            DrawBarMeter(PfMeterCanvas, (float)pfPercent);

            _pfHistory.Add((float)pfPercent);
            if (_pfHistory.Count > MaxPoints) _pfHistory.RemoveAt(0);
            DrawLineGraph(PfGraphCanvas, _pfHistory);

            StatusCommit.Text = $"Commit Charge: {Math.Round(committedMb)}M / {Math.Round(commitLimitK / 1024.0)}M";
        }

        private void UpdateSystemStats()
        {
            try
            {
                var procs = Process.GetProcesses();
                int procCount = procs.Length;
                int threadCount = procs.Sum(p => { try { return p.Threads.Count; } catch { return 0; } });
                int handleCount = procs.Sum(p => { try { return p.HandleCount; } catch { return 0; } });

                TxtProcesses.Text = procCount.ToString();
                TxtThreads.Text = threadCount.ToString();
                TxtHandles.Text = handleCount.ToString();
                StatusProcesses.Text = $"Processes: {procCount}";

                TxtPhysTotal.Text = _totalPhysK.ToString();
                using (var perfAvail = new PerformanceCounter("Memory", "Available KBytes"))
                {
                    double availK = perfAvail.NextValue();
                    TxtPhysAvail.Text = Math.Round(availK).ToString();
                    TxtPhysCache.Text = Math.Round(availK * 0.9).ToString();
                }

                double committedK = (_commitCounter?.NextValue() ?? 0) / 1024.0;
                TxtCommitTotal.Text = Math.Round(committedK).ToString();
                TxtCommitLimit.Text = Math.Round((double)_totalPhysK + 4000000).ToString();
                TxtCommitPeak.Text = Math.Round(_commitPeakK).ToString();

                using (var paged = new PerformanceCounter("Memory", "Pool Paged Bytes"))
                using (var nonpaged = new PerformanceCounter("Memory", "Pool Nonpaged Bytes"))
                {
                    double pagedK = paged.NextValue() / 1024.0;
                    double nonpagedK = nonpaged.NextValue() / 1024.0;
                    TxtKernelPaged.Text = Math.Round(pagedK).ToString();
                    TxtKernelNonPaged.Text = Math.Round(nonpagedK).ToString();
                    TxtKernelTotal.Text = Math.Round(pagedK + nonpagedK).ToString();
                }
            }
            catch { }
        }

        // --- TAB 4: NETWORKING ---
        private void UpdateNetworkingTab()
        {
            try
            {
                float netVal = _netRecvCounter != null ? _netRecvCounter.NextValue() : 0;
                float netPercent = Math.Min((netVal / (1024f * 1024f)) * 100f, 100f);

                _netHistory.Add(netPercent);
                if (_netHistory.Count > MaxPoints) _netHistory.RemoveAt(0);

                DrawLineGraph(NetGraphCanvas, _netHistory);

                NetListView.ItemsSource = new List<NetItem>
                {
                    new NetItem { AdapterName = "Local Area Connection", Utilization = $"{netPercent:F1} %", LinkSpeed = "1 Gbps" }
                };
            }
            catch { }
        }

        // --- TAB 5: USERS ---
        private void UpdateUsersTab()
        {
            UsersListView.ItemsSource = new List<UserRowItem>
            {
                new UserRowItem { Username = Environment.UserName, SessionId = "1", Status = "Active", ClientName = Environment.MachineName }
            };
        }

        private void UserLogoff_Click(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show("Are you sure you want to log off?", "Log Off Windows", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
            {
                Process.Start("shutdown", "/l");
            }
        }

        private void UserDisconnect_Click(object sender, RoutedEventArgs e) => MessageBox.Show("User session disconnected.");

        // --- MENU ACTIONS ---
        private void NewTask_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                SHRunDialog(IntPtr.Zero, IntPtr.Zero, null, "Create New Task", "Type the name of a program, folder, document, or Internet resource, and Windows will open it for you.", 0);
            }
            catch
            {
                Process.Start("cmd.exe");
            }
        }

        private void Exit_Click(object sender, RoutedEventArgs e) => Close();
        private void RefreshNow_Click(object sender, RoutedEventArgs e) => RefreshAllTabs();
        private void MenuAlwaysOnTop_Click(object sender, RoutedEventArgs e) => Topmost = MenuAlwaysOnTop.IsChecked;

        private void Shutdown_Click(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show("Turn off computer?", "Shutdown", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
                Process.Start("shutdown", "/s /t 0");
        }

        private void Restart_Click(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show("Restart computer?", "Restart", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
                Process.Start("shutdown", "/r /t 0");
        }

        // --- RENDER HELPERS ---
        private void DrawBarMeter(Canvas canvas, float percent)
        {
            canvas.Children.Clear();
            double width = canvas.ActualWidth;
            double height = canvas.ActualHeight;
            if (width <= 0 || height <= 0) return;

            int totalBlocks = 12;
            int activeBlocks = (int)Math.Round((percent / 100.0) * totalBlocks);
            double blockHeight = (height / totalBlocks) - 2;

            for (int i = 0; i < totalBlocks; i++)
            {
                double top = height - ((i + 1) * (blockHeight + 2));
                var rect = new Rectangle
                {
                    Width = Math.Max(0, width),
                    Height = Math.Max(0, blockHeight),
                    Fill = (i < activeBlocks) ? new SolidColorBrush(Color.FromRgb(0, 255, 0)) : new SolidColorBrush(Color.FromRgb(0, 45, 0))
                };
                Canvas.SetLeft(rect, 0);
                Canvas.SetTop(rect, top);
                canvas.Children.Add(rect);
            }
        }

        private void DrawGrid(Canvas canvas)
        {
            canvas.Children.Clear();
            double w = canvas.ActualWidth;
            double h = canvas.ActualHeight;
            if (w <= 0 || h <= 0) return;

            var gridBrush = new SolidColorBrush(Color.FromRgb(0, 70, 0));
            for (double x = 0; x < w; x += 12)
                canvas.Children.Add(new Line { X1 = x, Y1 = 0, X2 = x, Y2 = h, Stroke = gridBrush, StrokeThickness = 1 });
            for (double y = 0; y < h; y += 12)
                canvas.Children.Add(new Line { X1 = 0, Y1 = y, X2 = w, Y2 = y, Stroke = gridBrush, StrokeThickness = 1 });
        }

        private void DrawLineGraph(Canvas canvas, List<float> values)
        {
            DrawGrid(canvas);
            double w = canvas.ActualWidth;
            double h = canvas.ActualHeight;
            if (w <= 0 || h <= 0 || values.Count < 2) return;

            double stepX = w / (MaxPoints - 1);
            var polyline = new Polyline { Stroke = new SolidColorBrush(Color.FromRgb(0, 255, 0)), StrokeThickness = 1.2 };

            for (int i = 0; i < values.Count; i++)
            {
                double x = i * stepX;
                double y = h - ((values[i] / 100.0) * h);
                polyline.Points.Add(new Point(x, y));
            }
            canvas.Children.Add(polyline);
        }

        private void RedrawCoreGraphs()
        {
            for (int i = 0; i < CpuHistoryItemsControl.Items.Count; i++)
            {
                var container = CpuHistoryItemsControl.ItemContainerGenerator.ContainerFromIndex(i) as FrameworkElement;
                if (container != null)
                {
                    var canvas = FindVisualChild<Canvas>(container);
                    if (canvas != null && _cpuHistory.ContainsKey(i))
                        DrawLineGraph(canvas, _cpuHistory[i]);
                }
            }
        }

        private void CpuGraphCanvas_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is Canvas canvas) DrawGrid(canvas);
        }

        private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T t) return t;
                var childOfChild = FindVisualChild<T>(child);
                if (childOfChild != null) return childOfChild;
            }
            return null;
        }
    }

    public class AppTaskItem
    {
        public string Title { get; set; } = "";
        public string Status { get; set; } = "";
        public int ProcessId { get; set; }
        public IntPtr MainWindowHandle { get; set; }
    }

    public class ProcessRowItem
    {
        public string Name { get; set; } = "";
        public int Id { get; set; }
        public string CpuText { get; set; } = "";
        public string MemText { get; set; } = "";
        public long RawMem { get; set; }
    }

    public class NetItem
    {
        public string AdapterName { get; set; } = "";
        public string Utilization { get; set; } = "";
        public string LinkSpeed { get; set; } = "";
    }

    public class UserRowItem
    {
        public string Username { get; set; } = "";
        public string SessionId { get; set; } = "";
        public string Status { get; set; } = "";
        public string ClientName { get; set; } = "";
    }
}