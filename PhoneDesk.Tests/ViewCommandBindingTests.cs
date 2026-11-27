using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using PhoneDesk.ViewModels;

namespace PhoneDesk.Tests
{
    /// <summary>
    /// A button bound to a command its view model does not have shows, can be clicked, and does
    /// nothing: Avalonia reports the broken binding only in the debug log. The Auto Attendants page
    /// carried nine such buttons. This test reads every page and fails on the next one.
    /// </summary>
    public class ViewCommandBindingTests
    {
        private static readonly Regex PlainBinding = new(@"^\{Binding\s+(?:Path=)?([A-Za-z_][A-Za-z0-9_]*)\s*\}$");

        [Fact]
        public void EveryPageCommandExistsOnItsViewModel()
        {
            var views = Path.Combine(RepositoryRoot(), "src", "PhoneDesk.Presentation", "Views");
            var viewModels = typeof(ViewModelBase).Assembly.GetTypes()
                .Where(type => type.Namespace == "PhoneDesk.ViewModels" && !type.IsNested && type.Name.EndsWith("ViewModel", StringComparison.Ordinal))
                .ToDictionary(type => type.Name, StringComparer.Ordinal);
            var missing = new List<string>();
            var checkedPages = 0;

            foreach (var file in Directory.GetFiles(views, "*View.axaml", SearchOption.AllDirectories))
            {
                var page = Path.GetFileNameWithoutExtension(file);
                if (!viewModels.TryGetValue(page + "Model", out var viewModel))
                {
                    continue;
                }

                checkedPages++;
                foreach (var element in XDocument.Load(file).Descendants())
                {
                    // Inside a template the binding targets the item, not the page's view model.
                    if (element.Ancestors().Any(a => a.Name.LocalName.EndsWith("Template", StringComparison.Ordinal)))
                    {
                        continue;
                    }

                    foreach (var attribute in element.Attributes().Where(a => a.Name.LocalName == "Command"))
                    {
                        var match = PlainBinding.Match(attribute.Value.Trim());
                        if (match.Success && viewModel.GetProperty(match.Groups[1].Value, BindingFlags.Public | BindingFlags.Instance) is null)
                        {
                            missing.Add($"{page}: {match.Groups[1].Value}");
                        }
                    }
                }
            }

            Assert.True(checkedPages >= 18,$"Only {checkedPages} pages were checked; the view folder or naming changed.");
            Assert.Empty(missing);
        }

        private static string RepositoryRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null && !File.Exists(Path.Combine(directory.FullName, "PhoneDesk.slnx")))
            {
                directory = directory.Parent;
            }

            return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
        }
    }
}
