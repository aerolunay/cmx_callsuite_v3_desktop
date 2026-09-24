using System.Windows.Input;
using CmxDialer.Infrastructure;

namespace CmxDialer.ViewModels;

/// <summary>Register SIP → open live connection → set Not Ready → show the dialer.</summary>
public sealed class ConnectingViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private string _stepText = "Getting ready…";
    private string? _error;
    private bool _isWorking;

    public ConnectingViewModel(MainViewModel main)
    {
        _main = main;
        RetryCommand = new AsyncCommand(RunAsync, () => !IsWorking);
        BackCommand = new AsyncCommand(() => _main.BackToCampaignSelectAsync(), () => !IsWorking);
    }

    public string StepText { get => _stepText; private set => Set(ref _stepText, value); }
    public string? Error { get => _error; private set { if (Set(ref _error, value)) Notify(nameof(HasError)); } }
    public bool HasError => Error != null;
    public bool IsWorking { get => _isWorking; private set { if (Set(ref _isWorking, value)) CommandManager.InvalidateRequerySuggested(); } }

    public string ServerText => $"{_main.Settings.ServerUrl} · extension {_main.Agent?.Extension}";

    public ICommand RetryCommand { get; }
    public ICommand BackCommand { get; }

    public async Task RunAsync()
    {
        Error = null;
        IsWorking = true;
        try
        {
            StepText = "Registering your phone…";
            await _main.RegisterPhoneAsync();

            StepText = "Connecting to the dialer…";
            await _main.ConnectSocketAsync();

            StepText = "Setting your status to Not Ready…";
            var status = await _main.PrepareStatusAsync();

            IsWorking = false;
            await _main.EnterDialerAsync(status);
        }
        catch (Exception ex)
        {
            Log.Error($"Session start failed at \"{StepText}\"", ex);
            Error = ex.Message;
        }
        finally
        {
            IsWorking = false;
        }
    }
}
