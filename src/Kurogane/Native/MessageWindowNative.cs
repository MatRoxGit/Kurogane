using System.Runtime.InteropServices;

namespace Kurogane.Native;

/// <summary>
/// Fenêtre de messages, sans surface visible.
///
/// Un processus résident a besoin d'une boucle de messages pour être
/// réveillé par le shell. HWND_MESSAGE crée une fenêtre qui ne s'affiche
/// jamais, n'apparaît dans aucune barre, et ne sert qu'à recevoir.
/// </summary>
internal static class MessageWindowNative
{
    internal static readonly IntPtr HWND_MESSAGE = new(-3);

    internal const uint WM_DESTROY       = 0x0002;
    internal const uint WM_TIMER         = 0x0113;
    internal const uint WM_DISPLAYCHANGE = 0x007E;

    internal delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct WNDCLASSEX
    {
        public int    cbSize;
        public uint   style;
        public WndProc lpfnWndProc;
        public int    cbClsExtra;
        public int    cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string  lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MSG
    {
        public IntPtr hwnd;
        public uint   message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint   time;
        public int    ptX, ptY;
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode,
               EntryPoint = "RegisterClassExW")]
    internal static extern ushort RegisterClassEx(ref WNDCLASSEX classe);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode,
               EntryPoint = "CreateWindowExW")]
    internal static extern IntPtr CreateWindowEx(uint styleEtendu, string classe, string? titre,
        uint style, int x, int y, int largeur, int hauteur,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode,
               EntryPoint = "DefWindowProcW")]
    internal static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode,
               EntryPoint = "RegisterWindowMessageW")]
    internal static extern uint RegisterWindowMessage(string nom);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode,
               EntryPoint = "GetMessageW")]
    internal static extern int GetMessage(out MSG message, IntPtr hwnd, uint min, uint max);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "DispatchMessageW")]
    internal static extern IntPtr DispatchMessage(ref MSG message);

    [DllImport("user32.dll")]
    internal static extern void PostQuitMessage(int code);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr SetTimer(IntPtr hwnd, IntPtr id, uint millisecondes, IntPtr rappel);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool KillTimer(IntPtr hwnd, IntPtr id);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
}
