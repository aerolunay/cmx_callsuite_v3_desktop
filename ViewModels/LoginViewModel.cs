using System.Windows.Input;
using CmxDialer.Infrastructure;
using CmxDialer.Services;

namespace CmxDialer.ViewModels;

/// <summary>
/// Same login as the web app: email → (email code | authenticator code).
/// Uses /auth/check-user, /auth/request-otp, /auth/verify-otp, /auth/login-totp.
/// </summary>
public sealed class LoginViewModel : ObservableObject
{
    private enum Step { Email, Choose, EmailCode, AuthenticatorCode }

    private readonly MainViewModel _main;
    private Step _step = Step.Email;
    private string _email = "";
    private string _code = "";
    private bool _totpEnabled;
    private bool _busy;
    private string? _error;
    private string? _info;

    public LoginViewModel(MainViewModel main)
    {
        _main = main;
        ContinueCommand = new AsyncCommand(ContinueAsync, () => !Busy && Email.Contains('@'));
        UseEmailCodeCommand = new AsyncCommand(SendEmailCodeAsync, () => !Busy);
        UseAuthenticatorCommand = new RelayCommand(() => GoTo(Step.AuthenticatorCode), () => !Busy && _totpEnabled);
        VerifyCommand = new AsyncCommand(VerifyAsync, () => !Busy && Code.Trim().Length >= 6);
        ResendCommand = new AsyncCommand(SendEmailCodeAsync, () => !Busy);
        BackCommand = new RelayCommand(Back, () => !Busy);
    }

    public string Email
    {
        get => _email;
        set { if (Set(ref _email, value.Trim())) CommandManager.InvalidateRequerySuggested(); }
    }

    public string Code
    {
        get => _code;
        set { if (Set(ref _code, new string(value.Where(char.IsDigit).Take(8).ToArray()))) CommandManager.InvalidateRequerySuggested(); }
    }

    public bool Busy
    {
        get => _busy;
        private set { if (Set(ref _busy, value)) CommandManager.InvalidateRequerySuggested(); }
    }

    public string? Error { get => _error; set => Set(ref _error, value); }
    public string? Info { get => _info; private set => Set(ref _info, value); }

    public bool IsEmailStep => _step == Step.Email;
    public bool IsChooseStep => _step == Step.Choose;
    public bool IsCodeStep => _step is Step.EmailCode or Step.AuthenticatorCode;
    public bool IsEmailCode => _step == Step.EmailCode;
    public bool TotpEnabled => _totpEnabled;

    /// <summary>App version from the build (CmxDialer.csproj &lt;Version&gt;), e.g. "3.1.2".</summary>
    public string AppVersion { get; } = GetVersion();

    private static string GetVersion()
    {
        var v = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version;
        return v == null ? "" : $"{v.Major}.{v.Minor}.{v.Build}";
    }

    public string CodePrompt => _step == Step.EmailCode
        ? $"Enter the code we emailed to {Email}."
        : "Enter the 6-digit code from your authenticator app.";

    public ICommand ContinueCommand { get; }
    public ICommand UseEmailCodeCommand { get; }
    public ICommand UseAuthenticatorCommand { get; }
    public ICommand VerifyCommand { get; }
    public ICommand ResendCommand { get; }
    public ICommand BackCommand { get; }

    private void GoTo(Step step)
    {
        _step = step;
        Code = "";
        Error = null;
        if (step != Step.EmailCode) Info = null;
        RefreshAll();
    }

    private void Back()
    {
        GoTo(_step switch
        {
            Step.Choose => Step.Email,
            Step.EmailCode or Step.AuthenticatorCode => _totpEnabled ? Step.Choose : Step.Email,
            _ => Step.Email,
        });
    }

    private async Task ContinueAsync()
    {
        Error = null;
        Busy = true;
        try
        {
            _totpEnabled = await _main.Api.CheckUserAsync(Email);
            if (_totpEnabled)
            {
                GoTo(Step.Choose);
            }
            else
            {
                // No authenticator set up — email code is the only option, so send it straight away.
                Busy = false;
                await SendEmailCodeAsync();
            }
        }
        catch (ApiException ex) { Error = ex.Message; }
        finally { Busy = false; }
    }

    private async Task SendEmailCodeAsync()
    {
        Error = null;
        Busy = true;
        try
        {
            await _main.Api.RequestOtpAsync(Email);
            GoTo(Step.EmailCode);
            Info = "If that email is registered, a login code is on its way.";
        }
        catch (ApiException ex) { Error = ex.Message; }
        finally { Busy = false; }
    }

    private async Task VerifyAsync()
    {
        Error = null;
        Busy = true;
        try
        {
            var agent = _step == Step.EmailCode
                ? await _main.Api.VerifyOtpAsync(Email, Code.Trim())
                : await _main.Api.LoginTotpAsync(Email, Code.Trim());
            Busy = false;
            await _main.OnLoggedInAsync(agent);
        }
        catch (ApiException ex) { Error = ex.Message; }
        finally { Busy = false; }
    }
}
