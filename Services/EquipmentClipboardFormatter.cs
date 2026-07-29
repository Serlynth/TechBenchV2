using System.Text;
using TechBench.Models;

namespace TechBench.Services;

internal static class EquipmentClipboardFormatter
{
    public static string Format(EquipmentItem equipment)
    {
        ArgumentNullException.ThrowIfNull(equipment);

        var text = new StringBuilder();
        Append(text, "Equipment", equipment.Name);
        Append(text, "Device type", equipment.DeviceType);
        Append(text, "Asset tag", equipment.AssetTag);
        Append(text, "Serial number", equipment.SerialNumber);
        Append(text, "Part number", equipment.PartNumber);
        Append(text, "Manufacturer", equipment.Manufacturer);
        Append(text, "Model", equipment.Model);
        Append(text, "IP address", equipment.IpAddress);
        Append(text, "AnyDesk number", equipment.AnyDeskNumber);
        Append(text, "Client", equipment.ClientName);
        Append(text, "Client user", equipment.ClientUserDisplayName);
        Append(text, "Client user email", equipment.ClientUserEmail);
        Append(text, "Site / room / desk", equipment.LocationName);
        Append(text, "Assigned technician", equipment.AssignedToDisplayName);
        Append(text, "Workflow stage", equipment.WorkflowStage);
        Append(text, "Notes", equipment.Notes);
        return text.ToString().TrimEnd();
    }

    private static void Append(
        StringBuilder text,
        string label,
        string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            text.Append(label)
                .Append(": ")
                .AppendLine(value.Trim());
        }
    }
}
