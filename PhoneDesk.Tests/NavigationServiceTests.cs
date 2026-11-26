using System.ComponentModel;
using Moq;
using PhoneDesk.Services;
using PhoneDesk.Services.Interfaces;

namespace PhoneDesk.Tests
{
    public class NavigationServiceTests
    {
        [Fact]
        public void CurrentPage_DefaultsToWelcomePage()
        {
            var logger = new Mock<ILoggingService>();

            var service = new NavigationService(logger.Object);

            Assert.Equal(ConstantsService.Pages.Welcome, service.CurrentPage);
        }

        [Fact]
        public void NavigateTo_UpdatesCurrentPage()
        {
            var logger = new Mock<ILoggingService>();
            var service = new NavigationService(logger.Object);

            service.NavigateTo(ConstantsService.Pages.Variables);

            Assert.Equal(ConstantsService.Pages.Variables, service.CurrentPage);
        }

        [Fact]
        public void NavigateTo_SamePageTwice_DoesNotRaisePropertyChangedTwice()
        {
            var logger = new Mock<ILoggingService>();
            var service = new NavigationService(logger.Object);
            service.NavigateTo(ConstantsService.Pages.Variables);

            var raiseCount = 0;
            service.PropertyChanged += (_, _) => raiseCount++;

            service.NavigateTo(ConstantsService.Pages.Variables);

            Assert.Equal(0, raiseCount);
        }

        [Fact]
        public void NavigateTo_DifferentPage_RaisesPropertyChangedOnce()
        {
            var logger = new Mock<ILoggingService>();
            var service = new NavigationService(logger.Object);

            var raisedProperties = new List<string?>();
            service.PropertyChanged += (_, e) => raisedProperties.Add(e.PropertyName);

            service.NavigateTo(ConstantsService.Pages.CallQueues);

            Assert.Single(raisedProperties);
            Assert.Equal(nameof(NavigationService.CurrentPage), raisedProperties[0]);
        }
    }
}
