using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using ShiJian.Client.Controls.Charts;

namespace ShiJian.Client.Librarys;

/// <summary>
/// 集中追踪当前打开的轻量级浮层（ContextMenu / Flyout / 下拉 Popup），
/// 确保按 Esc 时优先关闭最顶层浮层，而不是穿透触发导航返回或关闭底层弹窗。
/// </summary>
public static class OverlayDismissTracker
{
    private static readonly List<ContextMenu> _openMenus = new();
    private static readonly List<Popup> _openPopups = new();
    private static bool _initialized;

    public static void EnsureInitialized()
    {
        if (_initialized) return;
        _initialized = true;

        ContextMenu.OpenedEvent.AddClassHandler<ContextMenu>((menu, _) =>
        {
            _openMenus.Remove(menu);
            _openMenus.Add(menu);
        });
        ContextMenu.ClosedEvent.AddClassHandler<ContextMenu>((menu, _) =>
        {
            _openMenus.Remove(menu);
        });

        Popup.IsOpenProperty.Changed.AddClassHandler<Popup>((popup, args) =>
        {
            if (args.NewValue is not bool isOpen) return;
            if (!isOpen)
            {
                _openPopups.Remove(popup);
                return;
            }

            if (ShouldIgnorePopup(popup)) return;
            _openPopups.Remove(popup);
            _openPopups.Add(popup);
        });
    }

    public static bool HasOpenOverlay()
    {
        CleanupClosed();
        return _openMenus.Count > 0 || _openPopups.Count > 0;
    }

    public static bool DismissTopMost()
    {
        CleanupClosed();

        if (_openMenus.Count > 0)
        {
            var topMenu = _openMenus[^1];
            _openMenus.RemoveAt(_openMenus.Count - 1);
            topMenu.Close();
            return true;
        }

        if (_openPopups.Count > 0)
        {
            var topPopup = _openPopups[^1];
            _openPopups.RemoveAt(_openPopups.Count - 1);
            topPopup.SetCurrentValue(Popup.IsOpenProperty, false);
            return true;
        }

        return false;
    }

    private static void CleanupClosed()
    {
        _openMenus.RemoveAll(m => !m.IsOpen);
        _openPopups.RemoveAll(p => !p.IsOpen);
    }

    private static bool ShouldIgnorePopup(Popup popup)
    {
        if (popup.Child is ToolTip || popup.TemplatedParent is ToolTip)
            return true;
        if (popup.FindAncestorOfType<ColumnChart>() != null)
            return true;
        return false;
    }
}
