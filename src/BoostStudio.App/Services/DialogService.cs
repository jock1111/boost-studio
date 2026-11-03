using System.Windows;
using BoostStudio.App.Views.Dialogs;
using BoostStudio.Core.Models;

namespace BoostStudio.App.Services;

public sealed class DialogService : IDialogService
{
    public void ShowInfo(string title, string message, string? detail = null) =>
        ShowMessage(title, message, detail, DialogKind.Information);

    public void ShowSuccess(string title, string message, string? detail = null) =>
        ShowMessage(title, message, detail, DialogKind.Success);

    public void ShowWarning(string title, string message, string? detail = null) =>
        ShowMessage(title, message, detail, DialogKind.Warning);

    public void ShowError(string title, string message, string? detail = null) =>
        ShowMessage(title, message, detail, DialogKind.Error);

    public bool Confirm(string title, string message, string confirmText = "Continue",
        string cancelText = "Cancel", bool danger = false, string? detail = null)
    {
        var dialog = new MessageDialog(title, message, detail, DialogKind.Question,
            confirmText, cancelText, danger)
        {
            Owner = GetOwner()
        };
        return dialog.ShowDialog() == true;
    }

    public string? Prompt(string title, string message, string label, string initialValue = "",
        string? help = null, int maxLength = 80)
    {
        var dialog = new PromptDialog(title, message, label, initialValue, help, maxLength)
        {
            Owner = GetOwner()
        };
        return dialog.ShowDialog() == true ? dialog.Value : null;
    }

    public void ShowText(string title, string message, string content, string? copyLabel = null)
    {
        var dialog = new TextViewDialog(title, message, content, copyLabel)
        {
            Owner = GetOwner()
        };
        dialog.ShowDialog();
    }

    public WelcomeChoice ShowWelcome(EngineStatus status)
    {
        var dialog = new WelcomeWindow(status)
        {
            Owner = GetOwner()
        };
        dialog.ShowDialog();
        return dialog.Choice;
    }

    private static void ShowMessage(string title, string message, string? detail, DialogKind kind)
    {
        var dialog = new MessageDialog(title, message, detail, kind)
        {
            Owner = GetOwner()
        };
        dialog.ShowDialog();
    }

    private static Window? GetOwner()
    {
        var main = Application.Current?.MainWindow;
        return main is { IsVisible: true } ? main : null;
    }
}
