using TechBench.Models;

namespace TechBench.Services;

public interface IUserNotificationService
{
    void ShowNewWhdTickets(IReadOnlyList<WhdSyncedTicket> tickets);
}
