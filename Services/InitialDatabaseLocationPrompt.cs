using System.IO;
using Microsoft.Win32;
using TechBench.Data;

namespace TechBench.Services;

public static class InitialDatabaseLocationPrompt
{
    public static void ConfigureIfNeeded()
    {
#if VISUAL_QA
        return;
#else
        if (!DatabaseLocationConfig.ShouldOfferInitialLocationChoice)
        {
            return;
        }

        var chooseCustomLocation = AppDialogWindow.Confirm(
            "Choose data location",
            "TechBench normally stores its database on this PC.\n\n"
            + "Choose another location if you want the database in OneDrive or Dropbox. "
            + "A cloud-synced database must only be open on one computer at a time.",
            confirmText: "Choose Location",
            cancelText: "Use This PC");
        if (!chooseCustomLocation)
        {
            return;
        }

        var useExisting = AppDialogWindow.Confirm(
            "Database setup",
            "Is there already a TechBench database from another computer that you want to use?",
            confirmText: "Use Existing",
            cancelText: "Create New");

        var selectedPath = useExisting ? ChooseExistingDatabase() : ChooseNewDatabase();
        if (string.IsNullOrWhiteSpace(selectedPath))
        {
            return;
        }

        if (useExisting)
        {
            var integrity = DatabaseLocationService.ValidateExistingDatabase(selectedPath);
            if (!integrity.IsHealthy)
            {
                AppDialogWindow.Error("Database setup", integrity.Message);
                return;
            }
        }
        else if (File.Exists(selectedPath))
        {
            AppDialogWindow.Error(
                "Database setup",
                "A file already exists at that location. Choose another filename, or restart TechBench and select Use Existing.");
            return;
        }

        DatabaseLocationConfig.SaveDatabasePath(selectedPath);
#endif
    }

    private static string? ChooseExistingDatabase()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Use Existing TechBench Database",
            Filter = "TechBench database (*.db)|*.db|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    private static string? ChooseNewDatabase()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Choose TechBench Database Location",
            Filter = "TechBench database (*.db)|*.db",
            FileName = "techbench.db",
            AddExtension = true,
            DefaultExt = ".db",
            OverwritePrompt = false
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
}
