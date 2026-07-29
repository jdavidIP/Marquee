using FluentAssertions;
using Marquee.Api.Auth;
using Marquee.Domain.Enums;

namespace Marquee.UnitTests;

public class RolePermissionsTests
{
    [Fact]
    public void An_ordinary_user_holds_no_administrative_permission()
    {
        RolePermissions.For(UserRole.User).Should().BeEmpty();
    }

    [Fact]
    public void An_admin_holds_every_administrative_permission()
    {
        RolePermissions.For(UserRole.Admin).Should().BeEquivalentTo([
            MarqueePermissions.ManagePremieres,
            MarqueePermissions.ViewUsers,
            MarqueePermissions.BlockUsers
        ]);
    }

    /// <summary>
    /// The point of permissions over a role check is that the two can diverge. This asserts they are
    /// genuinely separate values rather than three names for the same thing — if they collapsed, a
    /// policy requiring one would silently grant the others.
    /// </summary>
    [Fact]
    public void The_permissions_are_distinct_capabilities()
    {
        string[] all =
        [
            MarqueePermissions.ManagePremieres,
            MarqueePermissions.ViewUsers,
            MarqueePermissions.BlockUsers
        ];

        all.Should().OnlyHaveUniqueItems();
    }
}
