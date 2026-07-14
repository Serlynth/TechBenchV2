namespace TechBench.Services;

public sealed class AppDialogService : IUserDialogService
{
    public bool Confirm(
        string title,
        string message,
        string confirmText = "Yes",
        string cancelText = "No")
    {
        return AppDialogWindow.Confirm(
            title,
            message,
            confirmText: confirmText,
            cancelText: cancelText);
    }

    public void Info(string title, string message)
    {
        AppDialogWindow.Info(title, message);
    }

    public void Error(string title, string message)
    {
        AppDialogWindow.Error(title, message);
    }
}
