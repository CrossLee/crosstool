using Crosio.Windows.Core.Activation;
using Crosio.Windows.Core.Features;

namespace Crosio.Windows.Core.Tests.Activation;

public sealed class ActivationInboxTests
{
    [Fact]
    public void ActivationsPublishedBeforeWindowExistsAreDeliveredInOrder()
    {
        var inbox = new ActivationInbox();
        var received = new List<FeatureId>();
        inbox.Publish(ActivationRoute.Navigate(FeatureId.RegionScreenshot));
        inbox.Publish(ActivationRoute.Navigate(FeatureId.TextTranslation));

        using var subscription = inbox.Connect(route => received.Add(route.Feature));

        Assert.Equal(new[] { FeatureId.RegionScreenshot, FeatureId.TextTranslation }, received);
    }

    [Fact]
    public void DisposingReceiverQueuesLaterActivationForNextWindow()
    {
        var inbox = new ActivationInbox();
        var firstReceiver = new List<FeatureId>();
        var subscription = inbox.Connect(route => firstReceiver.Add(route.Feature));
        subscription.Dispose();
        inbox.Publish(ActivationRoute.Navigate(FeatureId.Settings));

        var secondReceiver = new List<FeatureId>();
        using var secondSubscription = inbox.Connect(route => secondReceiver.Add(route.Feature));

        Assert.Empty(firstReceiver);
        Assert.Equal(new[] { FeatureId.Settings }, secondReceiver);
    }

    [Fact]
    public void ActivationPublishedWhileConnectingCannotOvertakeQueuedRoutes()
    {
        var inbox = new ActivationInbox();
        var received = new List<FeatureId>();
        inbox.Publish(ActivationRoute.Navigate(FeatureId.RegionScreenshot));
        inbox.Publish(ActivationRoute.Navigate(FeatureId.WindowScreenshot));

        using var subscription = inbox.Connect(route =>
        {
            received.Add(route.Feature);
            if (route.Feature == FeatureId.RegionScreenshot)
            {
                inbox.Publish(ActivationRoute.Navigate(FeatureId.ScreenScreenshot));
            }
        });

        Assert.Equal(
            new[]
            {
                FeatureId.RegionScreenshot,
                FeatureId.WindowScreenshot,
                FeatureId.ScreenScreenshot,
            },
            received);
    }
}
