param(
    [Parameter(Mandatory = $true)][int]$EditorProcessId,
    [string]$WindowTitle = 'NPC Observer',
    [Parameter(Mandatory = $true)][string]$OutputPath
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
if (-not ('SocialWindowCapture' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class SocialWindowCapture {
    public delegate bool Callback(IntPtr hwnd, IntPtr extra);
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool EnumWindows(Callback callback, IntPtr extra);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    public static IntPtr Find(uint wanted, string title) {
        var found = new List<IntPtr>();
        EnumWindows((hwnd, extra) => {
            uint pid; GetWindowThreadProcessId(hwnd, out pid);
            if (pid == wanted && IsWindowVisible(hwnd)) {
                var text = new StringBuilder(512); GetWindowText(hwnd, text, text.Capacity);
                if (text.ToString() == title) found.Add(hwnd);
            }
            return true;
        }, IntPtr.Zero);
        if (found.Count != 1) throw new InvalidOperationException("Expected exactly one visible target window, found " + found.Count);
        return found[0];
    }
}
'@
}

$window = [SocialWindowCapture]::Find($EditorProcessId, $WindowTitle)
if ([SocialWindowCapture]::IsIconic($window)) { throw 'Restore the target window before capturing it.' }
$path = [IO.Path]::GetFullPath($OutputPath)
$directory = [IO.Path]::GetDirectoryName($path)
New-Item -ItemType Directory -Path $directory -Force | Out-Null
$previousDpi = [SocialWindowCapture]::SetThreadDpiAwarenessContext([IntPtr](-4))
$bitmap = $null
$graphics = $null
try {
    $bounds = [SocialWindowCapture+Rect]::new()
    if (![SocialWindowCapture]::GetWindowRect($window, [ref]$bounds)) { throw 'Could not read window bounds.' }
    $width = $bounds.Right - $bounds.Left
    $height = $bounds.Bottom - $bounds.Top
    if ($width -lt 1 -or $height -lt 1) { throw 'The target window has no drawable area.' }
    $bitmap = [Drawing.Bitmap]::new($width, $height)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    $dc = $graphics.GetHdc()
    try {
        if (![SocialWindowCapture]::PrintWindow($window, $dc, 2)) { throw 'PrintWindow failed.' }
    }
    finally { $graphics.ReleaseHdc($dc) }
    $bitmap.Save($path, [Drawing.Imaging.ImageFormat]::Png)
    [PSCustomObject]@{ Path = $path; Width = $width; Height = $height; ProcessId = $EditorProcessId; Window = $WindowTitle }
}
finally {
    if ($null -ne $graphics) { $graphics.Dispose() }
    if ($null -ne $bitmap) { $bitmap.Dispose() }
    if ($previousDpi -ne [IntPtr]::Zero) { [SocialWindowCapture]::SetThreadDpiAwarenessContext($previousDpi) | Out-Null }
}
