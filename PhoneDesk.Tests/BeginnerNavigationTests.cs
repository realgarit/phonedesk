using System.Xml.Linq;

namespace PhoneDesk.Tests;

public class BeginnerNavigationTests
{
    [Fact]
    public void SidebarPlacesBeginnerStepsBeforeManualProvisioningAndTools()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "PhoneDesk.slnx")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        var document = XDocument.Load(Path.Combine(directory.FullName, "src", "PhoneDesk.Presentation", "MainWindow.axaml"));
        var sidebar = document.Descendants().Single(e => e.Name.LocalName == "ListBox" &&
            e.Attributes().Any(a => a.Name.LocalName == "Name" && a.Value == "NavigationListBox"));
        var groups = new List<List<string>>();
        foreach (var item in sidebar.Elements().Where(e => e.Name.LocalName == "ListBoxItem"))
        {
            if (item.Attribute("Tag") is { } tag) groups[^1].Add(tag.Value);
            else groups.Add(new List<string>());
        }
        Assert.Equal(3, groups.Count);
        Assert.Equal(new[] { "Welcome", "GetStarted", "Variables", "Wizard" }, groups[0]);
        Assert.Equal(new[] { "M365Groups", "CallQueues", "AutoAttendants", "Holidays" }, groups[1]);
        Assert.Equal(new[] { "Dashboard", "HealthCheck", "HolidayManager", "PhoneNumbers", "EditSettings", "SetupRuns", "BulkOperations", "Documentation", "History" }, groups[2]);
    }
}
