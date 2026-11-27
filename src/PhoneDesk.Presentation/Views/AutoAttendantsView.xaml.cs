using Avalonia.Controls;
using Avalonia.Input;
using Microsoft.Extensions.DependencyInjection;
using PhoneDesk.Helpers;
using PhoneDesk.ViewModels;

namespace PhoneDesk.Views
{
    public partial class AutoAttendantsView : UserControl
    {
        public AutoAttendantsView()
        {
            InitializeComponent();
        }

        private AutoAttendantsViewModel? VM => DataContext as AutoAttendantsViewModel;

        // Backdrop handlers - close the dialog
        private void CreateResourceAccountBackdrop_PointerPressed(object? sender, PointerPressedEventArgs e)
            => DialogEventHelper.CloseOnBackdropClick(VM, VM?.CloseCreateResourceAccountDialogCommand);

        private void UpdateUsageLocationBackdrop_PointerPressed(object? sender, PointerPressedEventArgs e)
            => DialogEventHelper.CloseOnBackdropClick(VM, VM?.CloseUpdateUsageLocationDialogCommand);

        private void CreateAutoAttendantBackdrop_PointerPressed(object? sender, PointerPressedEventArgs e)
            => DialogEventHelper.CloseOnBackdropClick(VM, VM?.CloseCreateAutoAttendantDialogCommand);

        private void AssociateResourceAccountBackdrop_PointerPressed(object? sender, PointerPressedEventArgs e)
            => DialogEventHelper.CloseOnBackdropClick(VM, VM?.CloseAssociateDialogCommand);

        // Card handlers - stop click propagation (all identical)
        private void CreateResourceAccountCard_PointerPressed(object? sender, PointerPressedEventArgs e) => DialogEventHelper.StopPropagation(sender, e);
        private void UpdateUsageLocationCard_PointerPressed(object? sender, PointerPressedEventArgs e) => DialogEventHelper.StopPropagation(sender, e);
        private void CreateAutoAttendantCard_PointerPressed(object? sender, PointerPressedEventArgs e) => DialogEventHelper.StopPropagation(sender, e);
        private void AssociateResourceAccountCard_PointerPressed(object? sender, PointerPressedEventArgs e) => DialogEventHelper.StopPropagation(sender, e);
    }
}
