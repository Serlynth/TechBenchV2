using TechBench.Models;

namespace TechBench.Services;

public interface IUserNotificationService
{
    void ShowNewWhdTickets(IReadOnlyList<WhdSyncedTicket> tickets);

    void ShowUpdateAvailable(string version);

    void ShowAdminMessage(string title, string message)
    {
    }
}
