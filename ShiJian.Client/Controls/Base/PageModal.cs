using System;
using System.Collections.Generic;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Metadata;
using Avalonia.VisualTree;

namespace ShiJian.Client.Controls.Base;

/// <summary>
/// 页面模态框声明控件：将页面内的弹窗内容托管到 MainWindow 全窗口遮罩层，
/// 实现整窗变暗阻断、点击遮罩关闭以及 Esc 优先关闭；页面卸载时自动清理状态。
/// </summary>
public class PageModal : Control
{
    private static readonly List<PageModal> _activeModals = new();

    public static readonly StyledProperty<bool> IsOpenProperty =
        AvaloniaProperty.Register<PageModal, bool>(nameof(IsOpen));

    public static readonly StyledProperty<Control?> ModalContentProperty =
        AvaloniaProperty.Register<PageModal, Control?>(nameof(ModalContent));

    public static readonly StyledProperty<ICommand?> CloseCommandProperty =
        AvaloniaProperty.Register<PageModal, ICommand?>(nameof(CloseCommand));

    public static readonly StyledProperty<object?> CloseCommandParameterProperty =
        AvaloniaProperty.Register<PageModal, object?>(nameof(CloseCommandParameter));

    public static event Action? ActiveModalChanged;

    public static PageModal? ActiveModal
    {
        get
        {
            for (var i = _activeModals.Count - 1; i >= 0; i--)
            {
                var candidate = _activeModals[i];
                if (candidate.IsOpen && candidate.IsAttachedToVisualTree())
                    return candidate;
                _activeModals.RemoveAt(i);
            }
            return null;
        }
    }

    static PageModal()
    {
        IsOpenProperty.Changed.AddClassHandler<PageModal>((modal, _) => modal.SyncActivation());
        ModalContentProperty.Changed.AddClassHandler<PageModal>((modal, _) =>
        {
            if (modal.IsOpen) ActiveModalChanged?.Invoke();
        });
    }

    public PageModal()
    {
        IsVisible = false;
        IsHitTestVisible = false;
        Width = 0;
        Height = 0;
    }

    public bool IsOpen
    {
        get => GetValue(IsOpenProperty);
        set => SetValue(IsOpenProperty, value);
    }

    [Content]
    public Control? ModalContent
    {
        get => GetValue(ModalContentProperty);
        set => SetValue(ModalContentProperty, value);
    }

    public ICommand? CloseCommand
    {
        get => GetValue(CloseCommandProperty);
        set => SetValue(CloseCommandProperty, value);
    }

    public object? CloseCommandParameter
    {
        get => GetValue(CloseCommandParameterProperty);
        set => SetValue(CloseCommandParameterProperty, value);
    }

    public void Dismiss()
    {
        if (CloseCommand != null && CloseCommand.CanExecute(CloseCommandParameter))
        {
            CloseCommand.Execute(CloseCommandParameter);
            return;
        }

        SetCurrentValue(IsOpenProperty, false);
    }

    public static bool DismissTopModal()
    {
        var modal = ActiveModal;
        if (modal == null) return false;
        modal.Dismiss();
        return true;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        SyncActivation();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_activeModals.Remove(this))
            ActiveModalChanged?.Invoke();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (IsOpen && this.IsAttachedToVisualTree())
            ActiveModalChanged?.Invoke();
    }

    private void SyncActivation()
    {
        if (IsOpen && this.IsAttachedToVisualTree())
        {
            _activeModals.Remove(this);
            _activeModals.Add(this);
        }
        else
        {
            _activeModals.Remove(this);
        }

        ActiveModalChanged?.Invoke();
    }
}
