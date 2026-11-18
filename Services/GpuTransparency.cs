using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace QiDock.Services;

/// <summary>
/// See-through windows without WPF's layered-window mode. A layered WPF window copies every
/// frame from the graphics card back to memory, which makes large moving content stutter.
/// Instead the window stays an ordinary one whose glass frame covers all of it, so the desktop
/// compositor blends the transparent pixels itself and animation stays on the graphics card.
/// Call <see cref="Prepare"/> in the constructor and <see cref="Apply"/> in OnSourceInitialized.
/// </summary>
public static class GpuTransparency
{
    /// <summary>Whether this Windows can do it (Windows 10 and later, composition on).</summary>
    public static bool Supported => Composition
        && (RenderCapability.Tier >> 16) > 0 // drawn by the graphics card (not remote desktop's software path)
        && RenderOptions.ProcessRenderMode != System.Windows.Interop.RenderMode.SoftwareOnly
        && !SystemParameters.HighContrast;

    private static readonly bool Composition = Environment.OSVersion.Version.Major >= 10 && Composited();

    /// <summary>Makes the window see-through: layered mode only where the compositor can't help.</summary>
    public static void Prepare(Window window)
    {
        window.AllowsTransparency = !Supported;
        window.Background = Brushes.Transparent;
    }

    public static void Apply(Window window)
    {
        if (window.AllowsTransparency || !Supported || PresentationSource.FromVisual(window) is not HwndSource source) return;
        source.CompositionTarget.BackgroundColor = Colors.Transparent;
        var margins = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        DwmExtendFrameIntoClientArea(source.Handle, ref margins);
        var value = 1; // DWMWCP_DONOTROUND: no rounded window corners, the cards draw their own
        DwmSetWindowAttribute(source.Handle, 33, ref value, sizeof(int));
        value = unchecked((int)0xFFFFFFFE); // DWMWA_COLOR_NONE: no window border line
        DwmSetWindowAttribute(source.Handle, 34, ref value, sizeof(int));
        value = 1; // DWMSBT_NONE: no system backdrop behind the cards
        DwmSetWindowAttribute(source.Handle, 38, ref value, sizeof(int));
    }

    /// <summary>
    /// Frosted glass over the whole window: what lies behind is blurred and tinted with
    /// <paramref name="tint"/> (alpha = how much tint), and the window gets the rounded corners,
    /// thin edge and soft shadow Windows gives its own flyouts. Unlike the system backdrop this
    /// stays frosted while the window is not the active one (the panels never take focus).
    /// Returns false where Windows can't (then the caller draws an opaque background).
    /// A full-screen window passes <paramref name="rounded"/> false: square corners, no edge.
    /// </summary>
    public static bool Frost(Window window, Color tint, bool rounded = true)
    {
        if (window.AllowsTransparency || !Supported || PresentationSource.FromVisual(window) is not HwndSource source) return false;
        var policy = new AccentPolicy
        {
            AccentState = 4, // ACCENT_ENABLE_ACRYLICBLURBEHIND
            GradientColor = (uint)(tint.A << 24 | tint.B << 16 | tint.G << 8 | tint.R)
        };
        var size = Marshal.SizeOf<AccentPolicy>();
        var memory = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(policy, memory, false);
            var data = new CompositionAttributeData { Attribute = 19 /* WCA_ACCENT_POLICY */, Data = memory, Size = size };
            if (!SetWindowCompositionAttribute(source.Handle, ref data)) return false;
        }
        catch (EntryPointNotFoundException) { return false; }
        finally { Marshal.FreeHGlobal(memory); }
        if (rounded && Environment.OSVersion.Version.Build >= 22000)
        {
            var value = 2; // DWMWCP_ROUND
            DwmSetWindowAttribute(source.Handle, 33, ref value, sizeof(int));
            value = unchecked((int)0xFFFFFFFF); // DWMWA_COLOR_DEFAULT: the system's thin edge
            DwmSetWindowAttribute(source.Handle, 34, ref value, sizeof(int));
        }
        return true;
    }

    /// <summary>Takes the frosted glass off again (a window that draws its own shape), with square, edgeless corners.</summary>
    public static void Unfrost(Window window)
    {
        if (window.AllowsTransparency || !Supported || PresentationSource.FromVisual(window) is not HwndSource source) return;
        var policy = new AccentPolicy { AccentState = 0 };
        var size = Marshal.SizeOf<AccentPolicy>();
        var memory = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(policy, memory, false);
            var data = new CompositionAttributeData { Attribute = 19 /* WCA_ACCENT_POLICY */, Data = memory, Size = size };
            SetWindowCompositionAttribute(source.Handle, ref data);
        }
        catch (EntryPointNotFoundException) { }
        finally { Marshal.FreeHGlobal(memory); }
        var value = 1; // DWMWCP_DONOTROUND
        DwmSetWindowAttribute(source.Handle, 33, ref value, sizeof(int));
        value = unchecked((int)0xFFFFFFFE); // DWMWA_COLOR_NONE
        DwmSetWindowAttribute(source.Handle, 34, ref value, sizeof(int));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public int AccentState;
        public int AccentFlags;
        public uint GradientColor;
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CompositionAttributeData
    {
        public int Attribute;
        public nint Data;
        public int Size;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowCompositionAttribute(nint window, ref CompositionAttributeData data);

    private static bool Composited()
    {
        try { return DwmIsCompositionEnabled(out var on) == 0 && on; }
        catch (DllNotFoundException) { return false; }
        catch (EntryPointNotFoundException) { return false; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins
    {
        public int Left, Right, Top, Bottom;
    }

    [DllImport("dwmapi.dll")] private static extern int DwmExtendFrameIntoClientArea(nint window, ref Margins margins);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint window, int attribute, ref int value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmIsCompositionEnabled([MarshalAs(UnmanagedType.Bool)] out bool enabled);
}
