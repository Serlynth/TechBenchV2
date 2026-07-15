using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows.Data;
using TechBench.Models;
using TechBench.ViewModels;

namespace TechBench.Tests;

public sealed class CommonLinksViewTests
{
    [Fact]
    public void ReplaceCommonLinksSupportsConfiguredGroupedView()
    {
        RunInSta(() =>
        {
            var links = new ObservableCollection<CommonLink>();
            var view = CollectionViewSource.GetDefaultView(links);
            view.GroupDescriptions.Add(
                new PropertyGroupDescription(nameof(CommonLink.SectionName)));
            view.SortDescriptions.Add(
                new SortDescription(nameof(CommonLink.SectionOrder), ListSortDirection.Ascending));
            view.SortDescriptions.Add(
                new SortDescription(nameof(CommonLink.SortOrder), ListSortDirection.Ascending));
            view.SortDescriptions.Add(
                new SortDescription(nameof(CommonLink.Name), ListSortDirection.Ascending));

            MainWindowViewModel.ReplaceCommonLinks(
                links,
                [
                    new CommonLink
                    {
                        Id = 2,
                        Name = "Custom Portal",
                        Url = "https://portal.example.com/",
                        SortOrder = 20
                    },
                    new CommonLink
                    {
                        Id = 1,
                        Name = "Email2Phone",
                        Url = "https://user.email2phone.net/",
                        SortOrder = 4,
                        BuiltInKey = "email2phone"
                    }
                ]);

            Assert.Equal(2, links.Count);
            Assert.True(view.MoveCurrentToFirst());
            Assert.Equal("Email2Phone", Assert.IsType<CommonLink>(view.CurrentItem).Name);
            Assert.NotNull(view.Groups);
            Assert.Equal(2, view.Groups!.Count);
        });
    }

    private static void RunInSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
