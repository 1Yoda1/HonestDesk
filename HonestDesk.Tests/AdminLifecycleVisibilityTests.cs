using HonestDesk.Infrastructure.Api;
using Xunit;

namespace HonestDesk.Tests;

public class AdminLifecycleVisibilityTests
{
    [Fact]
    public void Deleted_device_is_hidden_by_default_and_visible_with_filter()
    {
        var deleted = new AdminDevice { Status = "Deleted" };
        var active = new AdminDevice { Status = "Active" };

        Assert.False(AdminLifecycleVisibility.DeviceIsVisible(deleted, showDeleted: false));
        Assert.True(AdminLifecycleVisibility.DeviceIsVisible(deleted, showDeleted: true));
        Assert.True(AdminLifecycleVisibility.DeviceIsVisible(active, showDeleted: false));
    }

    [Fact]
    public void Disabled_client_is_hidden_by_default_and_visible_with_filter()
    {
        var disabled = new AdminClient { IsActive = false };
        var active = new AdminClient { IsActive = true };

        Assert.False(AdminLifecycleVisibility.ClientIsVisible(disabled, showInactive: false));
        Assert.True(AdminLifecycleVisibility.ClientIsVisible(disabled, showInactive: true));
        Assert.True(AdminLifecycleVisibility.ClientIsVisible(active, showInactive: false));
    }

    [Fact]
    public void Current_bound_device_is_selected_by_internal_row_despite_released_history()
    {
        AdminDevice[] devices =
        [
            new() { Id = 91, ClientId = "client-a", DeviceId = "device-x", Status = "Deleted", DeviceIdReleased = true },
            new() { Id = 92, ClientId = "client-a", DeviceId = "device-x", Status = "Deleted", DeviceIdReleased = true },
            new() { Id = 93, ClientId = "client-a", DeviceId = "device-x", Status = "Active", DeviceIdReleased = false },
            new() { Id = 94, ClientId = "client-b", DeviceId = "device-x", Status = "Deleted", DeviceIdReleased = true }
        ];

        AdminDevice? selected = AdminLifecycleVisibility.FindCurrentBoundDevice(
            devices, "client-a", "device-x");

        Assert.NotNull(selected);
        Assert.Equal(93, selected.Id);
    }

    [Fact]
    public void Released_history_without_current_bound_device_is_not_selected_for_delete()
    {
        AdminDevice[] devices =
        [
            new() { Id = 91, ClientId = "client-a", DeviceId = "device-x", Status = "Deleted", DeviceIdReleased = true },
            new() { Id = 92, ClientId = "client-a", DeviceId = "device-x", Status = "Deleted", DeviceIdReleased = true }
        ];

        Assert.Null(AdminLifecycleVisibility.FindCurrentBoundDevice(
            devices, "client-a", "device-x"));
    }

    [Fact]
    public void Multiple_current_bound_devices_fail_with_invariant_diagnostic()
    {
        AdminDevice[] devices =
        [
            new() { Id = 1, ClientId = "client-a", DeviceId = "device-x", Status = "Active" },
            new() { Id = 2, ClientId = "client-a", DeviceId = "device-x", Status = "Disabled" }
        ];

        var exception = Assert.Throws<InvalidDataException>(() =>
            AdminLifecycleVisibility.FindCurrentBoundDevice(devices, "client-a", "device-x"));
        Assert.Contains("multiple current Device rows", exception.Message);
    }
}
