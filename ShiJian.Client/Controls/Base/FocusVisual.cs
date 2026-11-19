using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;

namespace ShiJian.Client.Controls.Base;

/// <summary>
/// 键盘焦点环。自绘控件没有模板边框可用，只能自己画一圈，
/// 否则键盘用户看不出焦点在哪里。
/// </summary>
public static class FocusVisual
{
    private static IBrush ResolveBrush()
    {
        if (Application.Current is { } app &&
            app.CheckAccess() &&
            app.TryGetResource("ThemeBrush", app.ActualThemeVariant, out var resource) &&
            resource is IBrush brush)
        {
            // 不缓存：主题切换后要跟着换色
            return brush;
        }

        return new SolidColorBrush(Color.Parse("#5E81AC"));
    }

    public static bool ShouldDraw(Control? control) =>
        control != null && control.IsKeyboardFocusWithin && control.Classes.Contains(":focus-visible");

    public static void Draw(DrawingContext context, Control control, double cornerRadius = 10)
    {
        Draw(context, control.Bounds.Size, ShouldDraw(control), cornerRadius);
    }

    public static void Draw(DrawingContext context, Size size, Control control, double cornerRadius = 10)
    {
        Draw(context, size, ShouldDraw(control), cornerRadius);
    }

    public static void Draw(DrawingContext context, Size size, bool focused, double cornerRadius = 10)
    {
        if (!focused) return;

        var rect = new Rect(size).Deflate(1.5);
        if (rect.Width <= 4 || rect.Height <= 4) return;

        context.DrawRectangle(null, new Pen(ResolveBrush(), 2), rect, cornerRadius, cornerRadius);
    }
}
