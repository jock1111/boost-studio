using BoostStudio.Core.Models;

namespace BoostStudio.App.Services;

public enum DialogKind
{
    Information,
    Success,
    Warning,
    Error,
    Question
}

public enum WelcomeChoice
{
    Close,
    Download,
    Recheck
}

public interface IDialogService
{
    void ShowInfo(string title, string message, string? detail = null);

    void ShowSuccess(string title, string message, string? detail = null);

    void ShowWarning(string title, string message, string? detail = null);

    void ShowError(string title, string message, string? detail = null);

    bool Confirm(string title, string message, string confirmText = "Continue",
        string cancelText = "Cancel", bool danger = false, string? detail = null);

    string? Prompt(string title, string message, string label, string initialValue = "",
        string? help = null, int maxLength = 80);

    void ShowText(string title, string message, string content, string? copyLabel = null);

    WelcomeChoice ShowWelcome(EngineStatus status);
}
