namespace TechBench.Services;

public interface IUserDialogService
{
    bool Confirm(
        string title,
        string message,
        string confirmText = "Yes",
        string cancelText = "No");

    void Info(string title, string message);
    void Error(string title, string message);

    string? Prompt(
        string title,
        string message,
        string initialValue = "",
        string confirmText = "OK",
        string cancelText = "Cancel");
}
