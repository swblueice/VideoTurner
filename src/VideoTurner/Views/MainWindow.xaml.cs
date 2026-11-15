using VideoTurner.ViewModels;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;
using WpfMenuItem = System.Windows.Controls.MenuItem;

namespace VideoTurner.Views;

public partial class MainWindow : UiWindow
{
    private readonly MainWindowViewModel _viewModel;
    private Forms.NotifyIcon? _trayIcon;
    private Forms.ToolStripMenuItem? _trayRoutingItem;
    private Forms.ToolStripMenuItem? _trayAutoMatchItem;
    private bool _forceClose;

    private Border? _activePage;
    private readonly Border[] _pages;

    public bool ShouldStartMinimized => _viewModel.StartMinimized;

    public MainWindow()
    {
        InitializeComponent();
        _viewModel = new MainWindowViewModel();
        DataContext = _viewModel;

        // 顺序必须与侧边栏 RadioButton 上的 Tag 对应：
        //   0 = 首页，1 = 应用清单，2 = 设置，3 = 关于。
        _pages = new[] { HomePage, PinnedPage, SettingsPage, AboutPage };

        SetupTrayIcon();

        _viewModel.PropertyChanged += OnViewModelPropertyChanged;

        // Listen for system theme changes to re-override when in manual mode
        Theme.Changed += OnGlobalThemeChanged;

        Loaded += (_, _) =>
        {
            // Start watcher for Mica backdrop effect (always needed)
            Watcher.Watch(this, BackgroundType.Mica, true);

            // Override with the user's chosen theme
            ApplyTheme(_viewModel.SelectedThemeIndex);

            ShowPage(0, animate: false);
        };
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.SelectedThemeIndex))
        {
            ApplyTheme(_viewModel.SelectedThemeIndex);
        }
        else if (e.PropertyName == nameof(MainWindowViewModel.IsRoutingEnabled))
        {
            // 托盘提示与菜单里的勾选状态跟着走，让用户不打开窗口也能看出当前是开是关。
            SyncTrayMenuState();
        }
    }

    private void OnGlobalThemeChanged(ThemeType currentTheme, System.Windows.Media.Color systemAccent)
    {
        // When the watcher changes the theme (system theme changed),
        // re-override if user has a manual theme selected
        var themeIndex = _viewModel.SelectedThemeIndex;
        if (themeIndex == 1 || themeIndex == 2)
        {
            // User wants a fixed theme, re-apply it
            Dispatcher.BeginInvoke(() => ApplyTheme(themeIndex));
        }
    }

    private void ApplyTheme(int themeIndex)
    {
        switch (themeIndex)
        {
            case 1: // Light
                Theme.Apply(ThemeType.Light, BackgroundType.Mica, true, true);
                break;
            case 2: // Dark
                Theme.Apply(ThemeType.Dark, BackgroundType.Mica, true, true);
                break;
            default: // System - detect current system theme and apply it
                var systemTheme = Theme.GetSystemTheme();
                var isDark = systemTheme is SystemThemeType.Dark
                    or SystemThemeType.CapturedMotion or SystemThemeType.Glow;
                Theme.Apply(isDark ? ThemeType.Dark : ThemeType.Light, BackgroundType.Mica, true, true);
                break;
        }
    }

    private void SetupTrayIcon()
    {
        _trayIcon = new Forms.NotifyIcon();

        // Previously this used Icon.ExtractAssociatedIcon(exePath), which pulls
        // a single arbitrary frame (typically the 32x32) from the exe's Win32
        // icon resource. The NotifyIcon then scaled that down to the system
        // tray size, producing a visibly smaller, fuzzier glyph than neighbour
        // icons that were authored at the correct pixel size.
        //
        // Load from the WPF-resource-packed app.ico instead and hand the Icon
        // ctor SystemInformation.SmallIconSize — that's the DPI-adjusted tray
        // size (16 at 100%, 20 at 125%, 24 at 150%, ...). The Icon(Stream,Size)
        // ctor picks the closest-matching frame from the multi-size file, so
        // the 16/20/24 frames we pack in app.ico all get used at their native
        // size with no resampling.
        try
        {
            _trayIcon.Icon = LoadTrayIconFromResources();
        }
        catch
        {
            // Any failure here (missing resource, exe rewritten in an odd way,
            // etc.) falls back to a neutral shell icon rather than crashing
            // the tray setup — the app still works without the tray glyph.
            _trayIcon.Icon = Drawing.SystemIcons.Application;
        }

        _trayIcon.Text = "VideoTurner — 多显示器音频路由";
        _trayIcon.Visible = true;

        // 托盘菜单：打开主界面 / 启用或暂停路由 / 按型号自动匹配 / 退出。
        // 勾选状态在 Opening 时刷新（见 SyncTrayMenuState），所以用户任何时候右键看到的都是实况。
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(new Forms.ToolStripMenuItem("打开主界面", null, (_, _) => ShowFromTray()));
        menu.Items.Add(new Forms.ToolStripSeparator());

        _trayRoutingItem = new Forms.ToolStripMenuItem("音频路由：已启用", null, (_, _) =>
        {
            _viewModel.ToggleRouting();
            SyncTrayMenuState();
        });
        menu.Items.Add(_trayRoutingItem);

        _trayAutoMatchItem = new Forms.ToolStripMenuItem("按显示器型号自动匹配", null, (_, _) =>
        {
            // 自动匹配的结果显示在首页顶部那行提示里，所以顺手把窗口叫出来
            _viewModel.AutoMatchCommand.Execute(null);
            ShowFromTray();
        });
        menu.Items.Add(_trayAutoMatchItem);

        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(new Forms.ToolStripMenuItem("退出", null, (_, _) => ForceClose()));

        menu.Opening += (_, _) => SyncTrayMenuState();
        _trayIcon.ContextMenuStrip = menu;
        _trayIcon.DoubleClick += (_, _) => ShowFromTray();

        SyncTrayMenuState();
    }

    /// <summary>
    /// 把托盘菜单与提示文字同步到当前状态。任何线程都可以调用（ToolStrip 内部会 marshal）。
    /// </summary>
    private void SyncTrayMenuState()
    {
        if (_trayIcon == null) return;

        if (_trayRoutingItem != null)
        {
            _trayRoutingItem.Checked = _viewModel.IsRoutingEnabled;
            _trayRoutingItem.Text = _viewModel.IsRoutingEnabled ? "音频路由：已启用" : "音频路由：已暂停";
        }

        if (_trayAutoMatchItem != null)
            _trayAutoMatchItem.Enabled = _viewModel.Monitors.Count > 0;

        _trayIcon.Text = _viewModel.IsRoutingEnabled
            ? "VideoTurner — 路由中"
            : "VideoTurner — 已暂停";
    }

    /// <summary>
    /// Loads the multi-size app.ico (packed as a WPF resource via the csproj
    /// &lt;Resource&gt; item) and asks for the frame matching the current tray
    /// icon size. Done once at startup; the tray doesn't update the icon when
    /// DPI changes mid-session, which matches how every other tray app behaves.
    /// </summary>
    private static Drawing.Icon LoadTrayIconFromResources()
    {
        var uri = new Uri("pack://application:,,,/app.ico", UriKind.Absolute);
        var streamInfo = System.Windows.Application.GetResourceStream(uri)
            ?? throw new InvalidOperationException("app.ico resource not found");
        using var stream = streamInfo.Stream;

        // SystemInformation.SmallIconSize is DPI-aware: returns 16x16 at 100%,
        // 20x20 at 125%, 24x24 at 150%. Our ICO contains exact matches for all
        // three so the Icon ctor returns a perfect, unscaled frame.
        var size = Forms.SystemInformation.SmallIconSize;
        return new Drawing.Icon(stream, size);
    }

    private void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void ForceClose()
    {
        _forceClose = true;
        Close();
    }

    private void NavButton_Checked(object sender, RoutedEventArgs e)
    {
        // Guard: Checked fires during InitializeComponent before _pages is set
        if (_pages == null) return;

        if (sender is RadioButton rb && rb.Tag is string page
            && int.TryParse(page, out var index))
        {
            _viewModel.CurrentPage = index;
            ShowPage(index, animate: true);
        }
    }

    private void ShowPage(int index, bool animate)
    {
        if (index < 0 || index >= _pages.Length) return;

        var target = _pages[index];
        if (target == _activePage) return;

        // Hide the previous page instantly
        if (_activePage != null)
        {
            _activePage.Visibility = Visibility.Collapsed;
            _activePage.Opacity = 0;
        }

        // Show and animate the new page
        target.Visibility = Visibility.Visible;

        if (animate)
        {
            var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
            var duration = new Duration(TimeSpan.FromMilliseconds(250));

            // Fade in
            var fadeIn = new DoubleAnimation(0, 1, duration) { EasingFunction = ease };
            target.BeginAnimation(OpacityProperty, fadeIn);

            // Slide up
            var transform = target.RenderTransform as TranslateTransform;
            if (transform == null)
            {
                transform = new TranslateTransform();
                target.RenderTransform = transform;
            }
            var slideUp = new DoubleAnimation(20, 0, duration) { EasingFunction = ease };
            transform.BeginAnimation(TranslateTransform.YProperty, slideUp);
        }
        else
        {
            target.Opacity = 1;
        }

        _activePage = target;
    }

    // ── 右键菜单（分配扬声器 / 取消分配） ─────────────────────────────────
    //
    // 会话行的 ContextMenu 用两个具名 MenuItem（PinToDeviceMenu / RemovePinMenu）声明。
    // 打开时按当前音频设备列表重建「分配扬声器」子菜单，并根据该行是否已分配决定
    // 「取消分配」是否显示。会话对象通过行 Border 的 Tag 传递，
    // 菜单打开时经由 PlacementTarget.Tag 取回 —— ContextMenu 在自己的弹出窗口里，
    // 拿不到行的 DataContext。
    //
    // 行内已经有扬声器下拉框（首页），这份菜单主要留给键盘用户（菜单键 / Shift+F10）。

    /// <summary>
    /// Keyboard entry point for the session-row context menu. The row is
    /// <c>Focusable="True"</c> so Tab can reach it; this handler opens the same
    /// ContextMenu that right-click opens when the user presses the Apps key
    /// or Shift+F10 — matching standard Windows list-row keyboarding.
    /// </summary>
    private void SessionRow_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.ContextMenu is not ContextMenu menu)
            return;

        bool shouldOpen = e.Key == Key.Apps
            || (e.Key == Key.F10 && (Keyboard.Modifiers & ModifierKeys.Shift) != 0);
        if (!shouldOpen) return;

        // Anchor the popup at the row and let ContextMenuOpening populate it,
        // so the keyboard path goes through the same flow as the mouse path.
        menu.PlacementTarget = fe;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private void SessionRow_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        if (fe.ContextMenu is not ContextMenu menu) return;
        if (fe.Tag is not SessionDisplayViewModel session)
        {
            e.Handled = true;
            return;
        }

        var pinSubmenu = menu.Items.OfType<WpfMenuItem>()
            .FirstOrDefault(m => m.Name == "PinToDeviceMenu");
        var removeItem = menu.Items.OfType<WpfMenuItem>()
            .FirstOrDefault(m => m.Name == "RemovePinMenu");
        var separator = menu.Items.OfType<Separator>()
            .FirstOrDefault(s => s.Name == "PinSeparator");

        if (pinSubmenu == null || removeItem == null || separator == null) return;

        // Rebuild the device submenu fresh each open — devices can be hot-plugged
        // between menu uses, and the user's currently-pinned device should appear
        // checked even if it's not the row's currently-routed device.
        pinSubmenu.Items.Clear();

        // Skip the synthetic "(No mapping)" entry — its Id is empty and pinning
        // to it would mean "no device", which the override schema doesn't model
        // (use Remove pin for that).
        var devices = _viewModel.AudioDevices.Where(d => !string.IsNullOrEmpty(d.Id)).ToList();
        if (devices.Count == 0)
        {
            pinSubmenu.Items.Add(new WpfMenuItem { Header = "（没有可用的音频设备）", IsEnabled = false });
        }
        else
        {
            // Find the existing pin (if any) so we can show a check next to the
            // device the user already chose for this app.
            var existingPin = _viewModel.AppOverrides.FirstOrDefault(
                o => string.Equals(o.ProcessName, session.ProcessName,
                                   StringComparison.OrdinalIgnoreCase));

            foreach (var device in devices)
            {
                var item = new WpfMenuItem
                {
                    Header = device.FriendlyName,
                    Tag = device.Id,
                    IsCheckable = true,
                    IsChecked = existingPin != null && existingPin.AudioDeviceId == device.Id,
                };
                item.Click += PinToDevice_Click;
                pinSubmenu.Items.Add(item);
            }
        }

        // Hide remove pin (and its separator) when there's nothing to remove.
        var hasPin = session.IsOverridden ||
                     _viewModel.AppOverrides.Any(o => string.Equals(
                         o.ProcessName, session.ProcessName, StringComparison.OrdinalIgnoreCase));
        removeItem.Visibility = hasPin ? Visibility.Visible : Visibility.Collapsed;
        separator.Visibility = hasPin ? Visibility.Visible : Visibility.Collapsed;
    }

    private void PinToDevice_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not WpfMenuItem item) return;
        if (item.Tag is not string deviceId || string.IsNullOrEmpty(deviceId)) return;

        // Walk up to the owning ContextMenu to recover the row's session VM.
        var menu = ItemsControl.ItemsControlFromItemContainer(item) as ContextMenu
                   ?? FindParentContextMenu(item);
        if (menu?.PlacementTarget is FrameworkElement fe && fe.Tag is SessionDisplayViewModel session)
        {
            // Carry the session's already-extracted icon straight over to the
            // pin so the Pinned page shows it without waiting for the next
            // sessions update.
            _viewModel.SetAppOverride(session.ProcessName, deviceId, session.Icon);
        }
    }

    private void RemovePin_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfMenuItem item) return;
        var menu = FindParentContextMenu(item);
        if (menu?.PlacementTarget is FrameworkElement fe && fe.Tag is SessionDisplayViewModel session)
        {
            _viewModel.RemoveAppOverride(session.ProcessName);
        }
    }

    // Walks up logical/visual parents to find the enclosing ContextMenu. Needed
    // because ItemsControlFromItemContainer doesn't reach across submenu levels.
    private static ContextMenu? FindParentContextMenu(DependencyObject start)
    {
        DependencyObject? current = start;
        while (current != null)
        {
            if (current is ContextMenu cm) return cm;
            current = LogicalTreeHelper.GetParent(current) ?? VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private void RemovePinFromList_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is AppOverrideViewModel ovr)
        {
            _viewModel.RemoveAppOverride(ovr.ProcessName);
        }
    }

    /// <summary>虚拟通道：把某个进程从托管名单里移掉。</summary>
    private void RemoveManagedProcess_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is string processName)
        {
            _viewModel.RemoveManagedProcess(processName);
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_forceClose && _viewModel.MinimizeOnClose)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        Theme.Changed -= OnGlobalThemeChanged;
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;

        if (_trayIcon != null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }

        _viewModel.Dispose();
        base.OnClosed(e);
    }
}
