using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using CmxDialer.ViewModels;

namespace CmxDialer.Views;

public partial class LoginView : UserControl
{
    public LoginView()
    {
        InitializeComponent();
        Loaded += (_, _) => FocusCurrentStep();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is INotifyPropertyChanged old) old.PropertyChanged -= OnVmChanged;
            if (e.NewValue is INotifyPropertyChanged vm) vm.PropertyChanged += OnVmChanged;
        };
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(LoginViewModel.IsCodeStep))
            Dispatcher.BeginInvoke(new Action(FocusCurrentStep), DispatcherPriority.Input);
    }

    private void FocusCurrentStep()
    {
        if (DataContext is not LoginViewModel vm) return;
        if (vm.IsEmailStep) EmailBox.Focus();
        else if (vm.IsCodeStep) CodeBox.Focus();
    }
}
