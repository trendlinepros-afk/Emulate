using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Emulate.Services;

namespace Emulate.Views;

/// <summary>
/// Hosts the Android Emulator's own window inside our UI. The emulator renders with the GPU into its
/// native window; we strip its frame, make it a child of this control and keep it sized to fit.
/// Its floating side toolbar is hidden (our own toolbar replaces it).
/// </summary>
public sealed class EmulatorHost : HwndHost
{
    private IntPtr _container;
    private IntPtr _emu;
    private long _origStyle, _origExStyle;
    private double _aspect = 9.0 / 19.5;
    private Native.RECT _lastSet;
    private readonly DispatcherTimer _timer;

    public EmulatorHost()
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _timer.Tick += (_, _) => Maintain();
    }

    public IntPtr EmulatorWindow => _emu;
    public bool IsAttached => _emu != IntPtr.Zero && Native.IsWindow(_emu);

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        _container = Native.CreateWindowEx(0, "STATIC", "", (int)(Native.WS_CHILD | Native.WS_VISIBLE | Native.WS_CLIPCHILDREN),
            0, 0, 100, 100, hwndParent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        return new HandleRef(this, _container);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        // Never let the emulator window die with us: hand it back to the desktop first.
        Detach();
        Native.DestroyWindow(hwnd.Handle);
        _container = IntPtr.Zero;
    }

    /// <summary>Moves the emulator window into this control.</summary>
    public void Attach(IntPtr emuWindow)
    {
        if (_container == IntPtr.Zero || emuWindow == IntPtr.Zero) return;
        if (_emu == emuWindow && Native.GetParent(_emu) == _container) return;

        _emu = emuWindow;
        if (Native.GetClientRect(_emu, out var rc) && rc.Width > 0 && rc.Height > 0) _aspect = (double)rc.Width / rc.Height;

        _origStyle = Native.GetStyle(_emu, Native.GWL_STYLE);
        _origExStyle = Native.GetStyle(_emu, Native.GWL_EXSTYLE);
        var style = _origStyle & ~(Native.WS_POPUP | Native.WS_CAPTION | Native.WS_THICKFRAME | Native.WS_SYSMENU |
                                   Native.WS_MINIMIZEBOX | Native.WS_MAXIMIZEBOX | Native.WS_BORDER);
        style |= Native.WS_CHILD | Native.WS_VISIBLE | Native.WS_CLIPSIBLINGS;
        var ex = _origExStyle & ~(Native.WS_EX_APPWINDOW | Native.WS_EX_WINDOWEDGE | Native.WS_EX_CLIENTEDGE | Native.WS_EX_DLGMODALFRAME);

        Native.SetStyle(_emu, Native.GWL_STYLE, style);
        Native.SetStyle(_emu, Native.GWL_EXSTYLE, ex);
        Native.SetParent(_emu, _container);
        _lastSet = default;
        Fit(force: true);
        HideCompanionWindows();
        _timer.Start();
    }

    /// <summary>Gives the emulator window back to the desktop as a normal window.</summary>
    public void Detach()
    {
        _timer.Stop();
        if (_emu == IntPtr.Zero) return;
        var emu = _emu;
        _emu = IntPtr.Zero;
        if (!Native.IsWindow(emu)) return;

        Native.SetParent(emu, IntPtr.Zero);
        Native.SetStyle(emu, Native.GWL_STYLE, _origStyle == 0 ? Native.GetStyle(emu, Native.GWL_STYLE) & ~Native.WS_CHILD : _origStyle);
        if (_origExStyle != 0) Native.SetStyle(emu, Native.GWL_EXSTYLE, _origExStyle);

        // Put it somewhere sensible on screen, keeping its size.
        Native.GetWindowRect(emu, out var r);
        var w = Math.Max(r.Width, 360);
        var h = Math.Max(r.Height, 640);
        var area = SystemParameters.WorkArea;
        Native.SetWindowPos(emu, IntPtr.Zero, (int)area.Left + 80, (int)area.Top + 40, w, h,
            Native.SWP_NOZORDER | Native.SWP_FRAMECHANGED | Native.SWP_SHOWWINDOW);
        ShowCompanionWindows(emu);
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        Fit(force: true);
    }

    protected override IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_SIZE = 0x0005;
        if (msg == WM_SIZE) Fit(force: true);
        return base.WndProc(hwnd, msg, wParam, lParam, ref handled);
    }

    private void Maintain()
    {
        if (!IsAttached)
        {
            _timer.Stop();
            _emu = IntPtr.Zero;
            return;
        }
        // The emulator resizes itself when the device rotates: learn the new orientation and refit.
        if (Native.GetParent(_emu) != _container)
        {
            Attach(_emu);
        }
        else if (Native.GetClientRect(_emu, out var client) && client.Width > 0 && client.Height > 0)
        {
            var newAspect = (double)client.Width / client.Height;
            if ((newAspect > 1) != (_aspect > 1))
            {
                _aspect = newAspect;
                Fit(force: true);
            }
        }
        HideCompanionWindows();
    }

    private void Fit(bool force)
    {
        if (_emu == IntPtr.Zero || _container == IntPtr.Zero || !Native.IsWindow(_emu)) return;
        Native.GetClientRect(_container, out var box);
        if (box.Width <= 0 || box.Height <= 0) return;

        const int margin = 12;
        var availW = Math.Max(50, box.Width - margin * 2);
        var availH = Math.Max(50, box.Height - margin * 2);
        int w, h;
        if ((double)availW / availH > _aspect)
        {
            h = availH;
            w = (int)Math.Round(h * _aspect);
        }
        else
        {
            w = availW;
            h = (int)Math.Round(w / _aspect);
        }
        var x = (box.Width - w) / 2;
        var y = (box.Height - h) / 2;
        if (!force && _lastSet.Width == w && _lastSet.Height == h) return;
        _lastSet = new Native.RECT { Left = 0, Top = 0, Right = w, Bottom = h };
        Native.SetWindowPos(_emu, IntPtr.Zero, x, y, w, h, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_FRAMECHANGED | Native.SWP_SHOWWINDOW);
    }

    /// <summary>Hides the emulator's floating side toolbar while docked.</summary>
    private void HideCompanionWindows()
    {
        if (_emu == IntPtr.Zero) return;
        var pid = Native.ProcessIdOf(_emu);
        foreach (var w in Native.TopLevelWindows(visibleOnly: true))
        {
            if (w == _emu || Native.ProcessIdOf(w) != pid) continue;
            // Only the narrow side toolbar: leave real dialogs ("Extended controls", prompts) alone.
            if (!LooksLikeToolbar(w)) continue;
            Native.ShowWindow(w, Native.SW_HIDE);
        }
    }

    private static void ShowCompanionWindows(IntPtr emu)
    {
        var pid = Native.ProcessIdOf(emu);
        foreach (var w in Native.TopLevelWindows(visibleOnly: false))
        {
            if (w == emu || Native.ProcessIdOf(w) != pid) continue;
            if (LooksLikeToolbar(w)) Native.ShowWindow(w, Native.SW_SHOWNOACTIVATE);
        }
    }

    private static bool LooksLikeToolbar(IntPtr w) =>
        Native.GetWindowRect(w, out var r) && r.Width > 20 && r.Width < 160 && r.Height > 150;

    public void FocusEmulator()
    {
        if (IsAttached) Native.SetFocus(_emu);
    }
}
