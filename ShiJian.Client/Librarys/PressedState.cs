using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using ShiJian.Client.Controls.Button;
using ShiJian.Client.Controls.Category;
using ShiJian.Client.Controls.DatePickerBar;
using ShiJian.Client.Controls.Input;
using ShiJian.Client.Controls.List;
using ShiJian.Client.Controls.Navigation;
using ShiJian.Client.Controls.Select;
using ToggleControl = ShiJian.Client.Controls.Toggle.Toggle;
using SelectControl = ShiJian.Client.Controls.Select.Select;

namespace ShiJian.Client.Librarys;

/// <summary>
/// 为不继承自 <see cref="Avalonia.Controls.Button"/> 的自定义交互控件
/// （如 <see cref="IconButton"/>、<see cref="NavigationItem"/>、<see cref="Option"/>、
/// <see cref="DatePickerBarItem"/>、<see cref="DatePickerGridItem"/>、<see cref="BaseListItem"/>、
/// <see cref="CategoryCard"/>、<see cref="ListBoxItem"/> 等）统一补齐 <c>:pressed</c> 伪类状态，
/// 驱动声明式按压微缩放过渡，并在松开、移出或丢失指针捕获时可靠归位。
/// </summary>
public static class PressedState
{
    private const string PressedPseudoClass = ":pressed";
    private static readonly object RegistrationLock = new();
    private static bool _registered;

    public static void EnsureRegistered()
    {
        lock (RegistrationLock)
        {
            if (_registered) return;
            _registered = true;

            RegisterClass<IconButton>(handledEventsToo: true, skipWhenNestedInteractive: false);
            RegisterClass<NavigationItem>(handledEventsToo: true, skipWhenNestedInteractive: false);
            RegisterClass<Option>(handledEventsToo: true, skipWhenNestedInteractive: false);
            RegisterClass<DatePickerBarItem>(handledEventsToo: true, skipWhenNestedInteractive: false);
            RegisterClass<DatePickerGridItem>(handledEventsToo: true, skipWhenNestedInteractive: false);
            RegisterClass<BaseListItem>(handledEventsToo: true, skipWhenNestedInteractive: false);
            RegisterClass<CategoryCard>(handledEventsToo: true, skipWhenNestedInteractive: false);
            RegisterClass<ListBoxItem>(handledEventsToo: false, skipWhenNestedInteractive: true);
        }
    }

    /// <summary>为单个非泛型模板节点（如 Expander HeaderBorder）挂载按压伪类跟踪</summary>
    public static void Attach(Control? control)
    {
        if (control == null) return;

        control.PointerPressed += (_, e) =>
        {
            if (control.IsEnabled && e.GetCurrentPoint(control).Properties.IsLeftButtonPressed)
                SetPressed(control, true);
        };
        control.PointerReleased += (_, _) => SetPressed(control, false);
        control.PointerCaptureLost += (_, _) => SetPressed(control, false);
        control.PointerExited += (_, _) => SetPressed(control, false);
    }

    private static void RegisterClass<TControl>(bool handledEventsToo, bool skipWhenNestedInteractive)
        where TControl : Control
    {
        InputElement.PointerPressedEvent.AddClassHandler<TControl>((control, e) =>
        {
            if (!control.IsEnabled) return;
            if (!e.GetCurrentPoint(control).Properties.IsLeftButtonPressed) return;
            if (skipWhenNestedInteractive && IsInsideNestedInteractive(e.Source as Visual, control)) return;
            SetPressed(control, true);
        }, RoutingStrategies.Bubble, handledEventsToo);

        InputElement.PointerReleasedEvent.AddClassHandler<TControl>(
            (control, _) => SetPressed(control, false),
            RoutingStrategies.Bubble | RoutingStrategies.Tunnel,
            handledEventsToo: true);

        InputElement.PointerCaptureLostEvent.AddClassHandler<TControl>(
            (control, _) => SetPressed(control, false),
            RoutingStrategies.Bubble | RoutingStrategies.Tunnel,
            handledEventsToo: true);

        InputElement.PointerExitedEvent.AddClassHandler<TControl>(
            (control, _) => SetPressed(control, false),
            RoutingStrategies.Bubble | RoutingStrategies.Tunnel,
            handledEventsToo: true);
    }

    private static void SetPressed(Control control, bool pressed)
    {
        ((IPseudoClasses)control.Classes).Set(PressedPseudoClass, pressed);
    }

    private static bool IsInsideNestedInteractive(Visual? source, Control host)
    {
        var cursor = source;
        while (cursor != null && !ReferenceEquals(cursor, host))
        {
            if (cursor is Avalonia.Controls.Button or IconButton or ToggleControl
                or SelectControl or DateSelect or TextBox or InputBox)
                return true;
            cursor = cursor.GetVisualParent();
        }
        return false;
    }
}
